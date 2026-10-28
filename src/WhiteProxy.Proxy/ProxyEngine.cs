using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;
using WhiteProxy.Core;

[assembly: InternalsVisibleTo("WhiteProxy.Tests")]
namespace WhiteProxy.Proxy;

public sealed record TrafficEvent(DateTimeOffset TimeUtc, string Peer, string Host, int Port,
    bool Allowed, string Reason, string? ServiceId = null, long Bytes = 0, double Seconds = 0);

public sealed class ProxyEngine : IDisposable
{
    private readonly ProxyServer server;
    private readonly Func<Configuration?> configuration;
    private readonly PolicyEngine policy;
    private readonly TimeProvider clock;
    private readonly Action<TrafficEvent> log;
    private readonly ConcurrentDictionary<string, Socket> sockets = new();
    private readonly ConcurrentDictionary<long, TrackedSession> sessions = new();
    private readonly ConditionalWeakTable<SessionEventArgsBase, object> subscribed = new();
    private readonly System.Threading.Timer monitor;
    private int sweeping;
    public int SessionCount => sessions.Count;
    public int DeviceCount => sessions.Values.Select(s => s.Peer.ToString()).Distinct().Count();
    public long DeniedCount => Interlocked.Read(ref deniedCount);
    private long deniedCount;
    public bool Running { get; private set; }

    public ProxyEngine(Func<Configuration?> configuration, TimeProvider clock, Action<TrafficEvent> log)
        : this(configuration, clock, log, true) { }

    // テスト専用: ローカル模擬サーバー用。製品・IPC・設定からは切替不可。
    internal ProxyEngine(Func<Configuration?> configuration, TimeProvider clock, Action<TrafficEvent> log, bool protectDestinations)
    {
        this.configuration = configuration; this.clock = clock; this.log = log;
        policy = new(clock);
        server = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false)
        {
            ForwardToUpstreamGateway = false,
            EnableConnectionPool = false,
            EnableTcpServerConnectionPrefetch = false,
            EnableHttp2 = false,
            BlockPrivateNetworkDestinations = protectDestinations,
            BlockLoopbackDestinations = protectDestinations,
            ClientHeaderTimeoutSeconds = 15,
            ConnectTimeOutSeconds = 15,
            IdleReadTimeoutSeconds = 300,
            IdleWriteTimeoutSeconds = 300,
            MaxConcurrentClientConnections = configuration()?.Proxy.MaxConnections ?? 256
        };
        // ライブラリの標準ログはURL/例外本文を記録し得るため、型とイベント番号だけに限定する。
        server.Logging.LoggerFactory = new SafeLoggerFactory((eventId, exceptionType) =>
            log(new(clock.GetUtcNow(), "", "", 0, false, $"エンジン警告・接続エラー ({eventId} / {exceptionType})")));
        server.ApplyLoggingConfiguration();
        server.OnClientConnectionCreate += (_, socket) =>
        {
            if (socket.RemoteEndPoint is { } peer) sockets[peer.ToString()!] = socket;
            return Task.CompletedTask;
        };
        server.BeforeRequest += BeforeRequest;
        server.AfterResponse += (_, e) =>
        {
            if (e.HttpClient.Response.StatusCode != 101) Finish(e.ClientConnectionId, "通信終了");
            return Task.CompletedTask;
        };
        monitor = new(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        var c = configuration() ?? throw new InvalidOperationException("設定異常のため開始できません。");
        ListenAddresses.EnsureCurrent(c.Proxy.ListenAddress);
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Parse(c.Proxy.ListenAddress), c.Proxy.Port, decryptSsl: false);
        endpoint.BeforeTunnelConnectRequest += BeforeConnect;
        server.AddEndPoint(endpoint);
        server.Start();
        Running = true;
        monitor.Change(250, 250);
    }

    private Task BeforeConnect(object sender, TunnelConnectSessionEventArgs e)
    {
        e.DecryptSsl = false;
        e.EstablishServerConnectionBeforeResponse = true;
        try
        {
            var authority = e.HttpClient.Request.RequestUriString;
            var target = ParseAuthority(authority, 443, true);
            ValidateHostHeader(e, target.Host, target.Port, 443);
            if (!Authorize(e, target.Host, target.Port)) e.DenyConnect = true;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
        {
            e.DenyConnect = true;
            Reject(e, "", 0, "CONNECT・Host形式不正");
        }
        return Task.CompletedTask;
    }

    private Task BeforeRequest(object sender, SessionEventArgs e)
    {
        try
        {
            var raw = e.HttpClient.Request.RequestUriString;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException();
            var host = HostRules.Normalize(uri.IdnHost);
            ValidateHostHeader(e, host, uri.Port, 80);
            if (!Authorize(e, host, uri.Port))
                e.GenericResponse("WhiteProxy: 管理ポリシーにより拒否されました。", HttpStatusCode.Forbidden);
            // プロキシー認証情報はオリジンサーバーへ転送しない。
            e.HttpClient.Request.Headers.RemoveHeader("Proxy-Authorization");
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
        {
            Reject(e, "", 0, "HTTP URI・Host形式不正");
            e.GenericResponse("WhiteProxy: invalid authority", HttpStatusCode.Forbidden);
        }
        return Task.CompletedTask;
    }

    public static (string Host, int Port) ParseAuthority(string value, int defaultPort, bool requirePort = false)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '@' or '\\')) throw new ArgumentException();
        if (!Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException();
        bool hasPort = value.StartsWith('[') ? value.Contains("]:", StringComparison.Ordinal) : value.Contains(':');
        if (requirePort && !hasPort) throw new ArgumentException();
        if (hasPort && (!int.TryParse(value[(value.LastIndexOf(':') + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var explicitPort) || explicitPort is < 1 or > 65535)) throw new ArgumentException();
        return (HostRules.Normalize(uri.IdnHost), hasPort ? uri.Port : defaultPort);
    }

    private static void ValidateHostHeader(SessionEventArgsBase e, string host, int port, int defaultPort)
    {
        var headers = e.HttpClient.Request.Headers.GetHeaders("Host");
        if (headers is null || headers.Count != 1) throw new ArgumentException();
        var actual = ParseAuthority(headers[0].Value, defaultPort);
        if (actual.Host != host || actual.Port != port) throw new ArgumentException();
    }

    private bool Authorize(SessionEventArgsBase e, string host, int port)
    {
        var c = configuration();
        var decision = c is null ? new Decision(false, "設定・時計・ログ異常") : policy.Evaluate(c, e.ClientRemoteEndPoint.Address, host, port);
        if (!decision.Allowed) { Reject(e, host, port, decision.Reason); return false; }
        var key = e.ClientRemoteEndPoint.ToString();
        if (!sockets.TryGetValue(key, out var socket)) { Reject(e, host, port, "接続ハンドル取得失敗"); return false; }
        Finish(e.ClientConnectionId, "次のリクエスト");
        var tracked = new TrackedSession(e, socket, e.ClientRemoteEndPoint.Address, host, port, decision, clock.GetUtcNow());
        sessions[e.ClientConnectionId] = tracked;
        if (!subscribed.TryGetValue(e, out _))
        {
            subscribed.Add(e, new object());
            void Count(object? sender, DataEventArgs data)
            { if (sessions.TryGetValue(e.ClientConnectionId, out var active)) Interlocked.Add(ref active.Bytes, data.Count); }
            e.DataSent += Count;
            e.DataReceived += Count;
        }
        log(new(clock.GetUtcNow(), tracked.Peer.ToString(), host, port, true, decision.Reason, decision.ServiceId));
        return true;
    }

    private void Reject(SessionEventArgsBase e, string host, int port, string reason)
    {
        Interlocked.Increment(ref deniedCount);
        log(new(clock.GetUtcNow(), e.ClientRemoteEndPoint.Address.ToString(), host, port, false, reason));
    }

    public void Sweep()
    {
        if (Interlocked.Exchange(ref sweeping, 1) != 0) return;
        try
        {
            var c = configuration();
            foreach (var (id, s) in sessions)
            {
                var decision = c is null ? new(false, "設定・時計・ログ異常") : policy.Evaluate(c, s.Peer, s.Host, s.Port);
                if (!decision.Allowed)
                {
                    try { s.Args.TerminateSession(); } catch (ObjectDisposedException) { }
                    s.Socket.Dispose();
                    Finish(id, "強制切断: " + decision.Reason);
                }
            }
            foreach (var (key, socket) in sockets)
            {
                bool ended;
                try { ended = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0; }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { ended = true; }
                if (!ended) continue;
                sockets.TryRemove(key, out _);
                foreach (var (id, s) in sessions.Where(p => ReferenceEquals(p.Value.Socket, socket))) Finish(id, "接続終了");
            }
        }
        catch
        {
            // 監視に失敗した場合にも既存接続を残さない。
            foreach (var s in sessions.Values) s.Socket.Dispose();
        }
        finally { Volatile.Write(ref sweeping, 0); }
    }

    private void Finish(long id, string reason)
    {
        if (sessions.TryRemove(id, out var s))
            log(new(clock.GetUtcNow(), s.Peer.ToString(), s.Host, s.Port, true, reason, s.Decision.ServiceId,
                Interlocked.Read(ref s.Bytes), (clock.GetUtcNow() - s.Start).TotalSeconds));
    }

    public void Dispose()
    {
        monitor.Dispose();
        foreach (var s in sockets.Values) s.Dispose();
        if (Running) server.Stop();
        server.Dispose(); Running = false;
    }

    private sealed class TrackedSession(SessionEventArgsBase args, Socket socket, IPAddress peer, string host, int port, Decision decision, DateTimeOffset start)
    {
        public SessionEventArgsBase Args { get; } = args;
        public Socket Socket { get; } = socket;
        public IPAddress Peer { get; } = peer;
        public string Host { get; } = host;
        public int Port { get; } = port;
        public Decision Decision { get; } = decision;
        public DateTimeOffset Start { get; } = start;
        public long Bytes;
    }
}
