using System.Net;
using System.Net.Sockets;
using System.Text;
using WhiteProxy.Core;
using WhiteProxy.Proxy;

namespace WhiteProxy.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

public class ProxyTests
{
    [Fact]
    public async Task ExpiredUpperGrantKeepsTunnelWhileLowerAllowanceIsActive()
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var config = Config(FreePort(), originPort, clock);
        config.Services.Add(new() { Id = "other", Hosts = ["127.0.0.1"], AllowedPorts = [originPort] });
        config.Policies = [new() { DeviceId = "pc", ServiceId = "web" }, new() { DeviceId = "pc", ServiceId = "other", AlwaysAllow = true }];
        using var proxy = new ProxyEngine(() => config, clock, _ => { }, false); proxy.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, config.Proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));
        Assert.Contains("200", await ReadHeaders(stream));
        using var upstream = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.UtcNow = clock.UtcNow.AddMinutes(90); proxy.Sweep();
        await stream.WriteAsync("PING\r\n\r\n"u8.ToArray());
        var bytes = new byte[8];
        await upstream.GetStream().ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PING\r\n\r\n", Encoding.ASCII.GetString(bytes));
        var closed = JsonDefaults.Clone(config); closed.Policies[1].AlwaysAllow = false; config = closed;
        proxy.Sweep(); await AssertClosed(stream);
    }

    [Fact]
    public async Task MovingSubnetDenyAboveHostAllowCutsExistingTunnel()
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var config = Config(FreePort(), originPort, clock); config.TemporaryGrants.Clear();
        config.Devices.Add(new() { Id = "subnet", Address = "127.0.0.0/24" });
        config.Policies = [new() { DeviceId = "pc", ServiceId = "web", AlwaysAllow = true }, new() { DeviceId = "subnet", ServiceId = "web", Deny = true }];
        using var proxy = new ProxyEngine(() => config, clock, _ => { }, false); proxy.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, config.Proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));
        Assert.Contains("200", await ReadHeaders(stream));
        using var upstream = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var reordered = JsonDefaults.Clone(config); reordered.Policies.Reverse(); config = reordered;
        proxy.Sweep(); await AssertClosed(stream); Assert.Equal(0, proxy.SessionCount);
        using var denied = new TcpClient(); await denied.ConnectAsync(IPAddress.Loopback, config.Proxy.Port);
        await denied.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));
        Assert.Contains("403", await ReadHeaders(denied.GetStream()));
    }

    internal static Configuration Config(int proxyPort, int originPort, ManualClock clock) => new()
    {
        Proxy = new() { Port = proxyPort },
        Devices = [new() { Id = "pc", Address = "127.0.0.1" }],
        Services = [new() { Id = "web", Hosts = ["127.0.0.1"], AllowedPorts = [originPort] }],
        TemporaryGrants = [new() { Id = "g", DeviceId = "pc", ServiceId = "web", StartsAtUtc = clock.UtcNow, ExpiresAtUtc = clock.UtcNow.AddMinutes(90) }]
    };
    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task ActiveConnectIsCutOffAtExpiry()
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var config = Config(FreePort(), originPort, clock);
        var events = new System.Collections.Concurrent.ConcurrentQueue<TrafficEvent>();
        using var proxy = new ProxyEngine(() => config, clock, events.Enqueue, false);
        proxy.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, config.Proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));
        var response = await ReadHeaders(stream);
        Assert.True(response.Contains("200"), response + string.Join(";", events.Select(e => e.Reason)));
        using var upstream = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await stream.WriteAsync("PING\r\n\r\n"u8.ToArray());
        var ping = new byte[8];
        await upstream.GetStream().ReadExactlyAsync(ping).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await upstream.GetStream().WriteAsync("hello"u8.ToArray());
        var bytes = new byte[5];
        await stream.ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello", Encoding.ASCII.GetString(bytes));
        clock.UtcNow = clock.UtcNow.AddMinutes(90);
        proxy.Sweep();
        await AssertClosed(stream);
        Assert.Equal(0, proxy.SessionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HttpDeniedBeforeOriginConnection(bool unknownPeer, bool mismatchedHost)
    {
        var clock = new ManualClock();
        var c = Config(FreePort(), FreePort(), clock);
        c.Services[0].Hosts = ["allowed.example"];
        if (unknownPeer) c.Devices.Clear();
        using var proxy = new ProxyEngine(() => c, clock, _ => { }); proxy.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, c.Proxy.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET http://{(mismatchedHost ? "allowed.example" : "denied.example")}/ HTTP/1.1\r\nHost: denied.example\r\nConnection: close\r\n\r\n"));
        Assert.Contains("403", await ReadHeaders(client.GetStream()));
    }

    [Fact]
    public async Task HttpRangeResponseStreamsWithoutChanging206()
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var c = Config(FreePort(), port, clock);
        using var proxy = new ProxyEngine(() => c, clock, _ => { }, false); proxy.Start();
        var originTask = Task.Run(async () =>
        {
            using var server = await origin.AcceptTcpClientAsync();
            Assert.Contains("Range: bytes=2-5", await ReadHeaders(server.GetStream()));
            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Length: 4\r\nContent-Range: bytes 2-5/10\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n2345"));
        });
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{c.Proxy.Port}"), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/file");
        request.Headers.Range = new(2, 5);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("2345", await response.Content.ReadAsStringAsync());
        await originTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static async Task<string> ReadHeaders(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = new List<byte>(); var b = new byte[1];
        while (result.Count < 32768)
        {
            if (await stream.ReadAsync(b, timeout.Token) == 0) break;
            result.Add(b[0]);
            if (result.Count >= 4 && Encoding.ASCII.GetString(result.TakeLast(4).ToArray()) == "\r\n\r\n") break;
        }
        return Encoding.ASCII.GetString(result.ToArray());
    }
    internal static async Task AssertClosed(Stream stream)
    {
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5))); }
        catch (IOException) { }
    }
}
