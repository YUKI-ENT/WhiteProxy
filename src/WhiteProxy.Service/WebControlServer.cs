using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Service;

internal sealed class WebControlServer(string root, Func<IpcRequest, string, CancellationToken, Task<IpcResponse>> command,
    Action<string, string, string> audit) : IAsyncDisposable
{
    private const string CookieName = "__Host-WhiteProxy";
    private readonly WebControlStore store = new(root);
    private readonly SemaphoreSlim settingsGate = new(1);
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly object loginGate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> loginAttempts = new();
    private readonly SemaphoreSlim loginWorkers = new(2);
    private WebControlOptions options = new();
    private WebApplication? application;
    private X509Certificate2? certificate;
    private string error = "";
    private bool initialized;
    private bool disposed;

    internal WebControlStatus Status() => WebControlStore.Public(options) with {
        Running = application is not null, Error = error,
        CertificateFingerprint = certificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? "",
        Urls = ListenAddresses.CurrentIpv4().Select(ip => $"https://{ip}:{options.Port}/").ToList()
    };

    internal async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            options = store.Read();
            if (options.Enabled)
            {
                var state = await command(new(), "Windowsサービス", ct);
                if (state.Configuration?.Proxy.Port == options.Port) throw new InvalidOperationException("Web管理とプロキシーのポートが重複しています。");
                application = await StartApplicationAsync(options.Port, ct);
            }
            initialized = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"Web管理を開始できませんでした（{ex.GetType().Name}, 0x{ex.HResult:X8}）。ポート・設定・証明書を確認してください。";
            audit("Web管理起動失敗", "Windowsサービス", error);
        }
    }

    internal async Task<WebControlStatus> UpdateAsync(WebControlChange change, int proxyPort, CancellationToken ct)
    {
        await settingsGate.WaitAsync(ct);
        try
        {
            if (disposed) throw new InvalidOperationException("Web管理は終了中です。");
            if (!initialized && error.Length > 0) store.Read(); // 破損した設定を暗黙に上書きしない。
            var candidate = store.Prepare(change, proxyPort);
            WebApplication? replacement = null;
            bool replace = application is null || !candidate.Enabled || candidate.Port != options.Port;
            try
            {
                if (replace && candidate.Enabled) replacement = await StartApplicationAsync(candidate.Port, ct);
                store.Save(candidate);
            }
            catch { if (replacement is not null) await replacement.DisposeAsync(); throw; }
            var previous = application;
            lock (loginGate) { options = candidate; sessions.Clear(); }
            initialized = true; error = "";
            if (replace) application = replacement;
            if (replace && previous is not null) await StopApplicationAsync(previous);
            return Status();
        }
        finally { settingsGate.Release(); }
    }

    private async Task<WebApplication> StartApplicationAsync(int port, CancellationToken ct)
    {
        certificate ??= WebControlCertificate.LoadOrCreate(root);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = Protocol.MaxMessageBytes;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            k.Listen(IPAddress.Any, port, listen => listen.UseHttps(certificate, h => h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidDataException or ArgumentException)
            { await Error(context, 400, "入力の形式が不正です。"); }
            catch (Exception ex)
            {
                audit("Web要求エラー", Peer(context), ex.GetType().Name);
                if (!context.Response.HasStarted) await Error(context, 500, "操作を完了できませんでした。管理GUIの状態を確認してください。");
            }
        });
        foreach (var (route, file, type) in new[] { ("/", "index.html", "text/html; charset=utf-8"), ("/app.css", "app.css", "text/css; charset=utf-8"), ("/app.js", "app.js", "text/javascript; charset=utf-8") })
        {
            using var stream = typeof(WebControlServer).Assembly.GetManifestResourceStream("WhiteProxy.Web." + file) ?? throw new InvalidOperationException("Web画面が見つかりません。");
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            app.MapGet(route, () => Results.Content(content, type));
        }
        app.MapPost("/api/login", LoginAsync);
        app.MapGet("/api/session", async context =>
        {
            if (Authenticate(context) is not { } session) { await Error(context, 401, "ログインしてください。"); return; }
            await context.Response.WriteAsJsonAsync(new { authenticated = true, csrfToken = session.Csrf });
        });
        app.MapPost("/api/logout", async context =>
        {
            if (!await AuthorizeMutation(context)) return;
            sessions.TryRemove(context.Request.Cookies[CookieName]!, out _);
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
            await context.Response.WriteAsJsonAsync(new { success = true });
        });
        app.MapGet("/api/status", context => ReadAsync(context, "status"));
        app.MapGet("/api/logs", context => ReadAsync(context, "logs"));
        app.MapPost("/api/command", async context =>
        {
            if (!await AuthorizeMutation(context)) return;
            var request = await context.Request.ReadFromJsonAsync<IpcRequest>(JsonDefaults.Options, context.RequestAborted);
            if (request is null || request.Operation is not ("apply" or "block" or "grant" or "revoke")) { await Error(context, 400, "未対応の操作です。"); return; }
            if (request.Operation == "apply" && request.Configuration is { } next)
            {
                var before = await command(new(), Peer(context), context.RequestAborted);
                // Webからサービスの待受・保存先関連設定を変更しない。
                if (before.Configuration is null || JsonSerializer.Serialize(next.Proxy, JsonDefaults.Options) != JsonSerializer.Serialize(before.Configuration.Proxy, JsonDefaults.Options))
                { await Error(context, 400, "プロキシーの運用設定はサーバーGUIから変更してください。"); return; }
            }
            var response = await command(request, Peer(context), context.RequestAborted);
            if (!response.Success) context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(response, JsonDefaults.Options, context.RequestAborted);
        });
        try { await app.StartAsync(ct); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private async Task ReadAsync(HttpContext context, string operation)
    {
        if (Authenticate(context) is null) { await Error(context, 401, "ログインしてください。"); return; }
        var response = await command(new() { Operation = operation }, Peer(context), context.RequestAborted);
        if (operation == "status" && response.Configuration is { } config)
        {
            var now = response.Status?.TimeUtc ?? DateTimeOffset.UtcNow;
            var rules = DashboardRules.List(config, now).Select(rule => new {
                key = DashboardRules.Key(rule), rule.DeviceId, rule.ServiceId,
                allowed = response.Status is { Healthy: true, Running: true } && DashboardRules.Evaluate(config, rule, now).Allowed,
                reason = response.Status is { Healthy: true, Running: true } ? DashboardRules.Reason(config, rule, now) : "サービス異常・通信遮断中",
                schedule = DashboardRules.ScheduleText(rule), nextStart = DashboardRules.NextStart(config, rule, now)
            });
            var zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(config.Proxy.TimeZoneId, out var iana) ? iana : config.Proxy.TimeZoneId;
            await context.Response.WriteAsJsonAsync(new { response.Success, response.Message, response.Configuration, response.Status, rules, timeZoneId = zone }, JsonDefaults.Options, context.RequestAborted);
        }
        else await context.Response.WriteAsJsonAsync(response, JsonDefaults.Options, context.RequestAborted);
    }

    private async Task LoginAsync(HttpContext context)
    {
        if (!SameOrigin(context) || !context.Request.HasJsonContentType()) { await Error(context, 403, "同じ管理画面から操作してください。"); return; }
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!AllowLogin(ip)) { await Error(context, 429, "ログイン試行が多すぎます。1分後に再試行してください。"); return; }
        if (context.Request.ContentLength is > 4096) { await Error(context, 400, "入力が長すぎます。"); return; }
        var login = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted);
        if (!await loginWorkers.WaitAsync(0, context.RequestAborted)) { await Error(context, 429, "ログイン処理中です。しばらく待って再試行してください。"); return; }
        var checkedOptions = options;
        bool valid;
        try { valid = login?.Password is not null && checkedOptions.Enabled && WebControlStore.Verify(checkedOptions, login.Password); }
        finally { loginWorkers.Release(); }
        if (!valid) { audit("Webログイン失敗", Peer(context), "パスワード不一致"); await Error(context, 401, "パスワードが違います。"); return; }
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in sessions.Where(e => e.Value.Expires <= now)) sessions.TryRemove(entry.Key, out _);
        if (sessions.Count >= 128) { await Error(context, 429, "接続数の上限です。しばらく待って再試行してください。"); return; }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now.AddMinutes(30));
        lock (loginGate)
        {
            if (!ReferenceEquals(checkedOptions, options)) valid = false;
            else sessions[id] = session;
        }
        if (!valid) { await Error(context, 401, "パスワードが変更されました。ログインし直してください。"); return; }
        context.Response.Cookies.Append(CookieName, id, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(30) });
        audit("Webログイン", Peer(context), "");
        await context.Response.WriteAsJsonAsync(new { success = true, csrfToken = session.Csrf });
    }
    private bool AllowLogin(string ip)
    {
        lock (loginGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in loginAttempts.Where(p => now - p.Value.Start >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList()) loginAttempts.Remove(key);
            if (loginAttempts.Values.Sum(v => v.Count) >= 30) return false;
            var window = loginAttempts.GetValueOrDefault(ip, (Start: now, Count: 0));
            if (window.Count >= 5) return false;
            loginAttempts[ip] = (window.Start, window.Count + 1); return true;
        }
    }
    private Session? Authenticate(HttpContext context)
    {
        var id = context.Request.Cookies[CookieName];
        if (id is null || !sessions.TryGetValue(id, out var session)) return null;
        if (session.Expires <= DateTimeOffset.UtcNow) { sessions.TryRemove(id, out _); return null; }
        return session;
    }
    private async Task<bool> AuthorizeMutation(HttpContext context)
    {
        if (Authenticate(context) is not { } session) { await Error(context, 401, "ログインしてください。"); return false; }
        if (!context.Request.HasJsonContentType() && context.Request.Path != "/api/logout" || !SameOrigin(context) ||
            context.Request.Headers["X-CSRF-Token"].ToString() != session.Csrf)
        { await Error(context, 403, "操作を確認できませんでした。画面を開き直してください。"); return false; }
        return true;
    }
    private static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 || Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
    private static string Peer(HttpContext context) => "Web管理者/" + context.Connection.RemoteIpAddress;
    private static Task Error(HttpContext context, int code, string message)
    {
        context.Response.StatusCode = code;
        return context.Response.WriteAsJsonAsync(new { success = false, message });
    }
    private static async Task StopApplicationAsync(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await settingsGate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            var previous = application; application = null;
            if (previous is not null) await StopApplicationAsync(previous);
            sessions.Clear(); certificate?.Dispose(); certificate = null;
        }
        finally { settingsGate.Release(); }
    }
    private sealed record Session(string Csrf, DateTimeOffset Expires);
    private sealed record LoginRequest(string Password);
}
