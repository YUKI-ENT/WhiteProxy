using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using WhiteProxy.Admin;
using WhiteProxy.Core;
using WhiteProxy.Proxy;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class WebControlTests
{
    [Fact]
    public void PasswordStoreDefaultsToDisabledAndHashesAndPreservesPassword()
    {
        var root = NewRoot();
        try
        {
            var store = new WebControlStore(root);
            Assert.False(store.Read().Enabled); Assert.Equal(3129, store.Read().Port);
            Assert.Throws<InvalidOperationException>(() => store.Prepare(new() { Enabled = true }, 3128));
            Assert.Throws<InvalidOperationException>(() => store.Prepare(new() { Enabled = true, Password = "short" }, 3128));
            Assert.Throws<InvalidOperationException>(() => store.Prepare(new() { Enabled = true, Password = Password, Port = 3128 }, 3128));
            var first = store.Prepare(new() { Enabled = true, Password = Password }, 3128); store.Save(first);
            Assert.True(WebControlStore.Verify(store.Read(), Password));
            Assert.False(WebControlStore.Verify(store.Read(), "another-password"));
            Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(root, "web-control.json")));
            var next = store.Prepare(new() { Enabled = true, Port = 3130, ExpectedRevision = 1 }, 3128);
            Assert.Equal(first.PasswordHash, next.PasswordHash); store.Save(next);
            Assert.Throws<InvalidOperationException>(() => store.Prepare(new() { ExpectedRevision = 1 }, 3128));
            var status = WebControlStore.Public(store.Read());
            Assert.True(status.PasswordConfigured); Assert.Equal(3130, status.Port);
            Assert.DoesNotContain("PasswordHash", JsonSerializer.Serialize(status));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HttpsRequiresLoginAndCsrfAndSharesWorkerRevisionAndValidation()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("api/status")).StatusCode);
        var page = await f.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", string.Join(';', page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.Equal("text/javascript", (await f.Client.GetAsync("app.js")).Content.Headers.ContentType!.MediaType);
        Assert.Equal("text/css", (await f.Client.GetAsync("app.css")).Content.Headers.ContentType!.MediaType);
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "api/login") { Content = JsonContent.Create(new { password = Password }) })
        {
            crossOrigin.Headers.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.SendAsync(crossOrigin)).StatusCode);
        }
        var login = await f.Login();
        var cookie = string.Join(';', login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var status = await f.State();
        Assert.Equal("web", status.Configuration!.Services.Single().Id);
        Assert.NotNull(status.Status);
        var missingCsrf = await f.Client.PostAsJsonAsync("api/command", new IpcRequest { Operation = "block", Block = true });
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        var block = await f.Command(new() { Operation = "block", ExpectedRevision = 0, Block = true });
        Assert.True(block.Success); Assert.True(block.Configuration!.Proxy.GlobalBlock);
        Assert.Equal(1, (await f.Repository.LoadAsync(default)).Revision);
        var stale = await f.Command(new() { Operation = "block", ExpectedRevision = 0, Block = false }, HttpStatusCode.Conflict);
        Assert.False(stale.Success);
        var changed = JsonDefaults.Clone(block.Configuration);
        changed.Proxy.Port = FreePort();
        Assert.False((await f.Command(new() { Operation = "apply", ExpectedRevision = 1, Configuration = changed }, HttpStatusCode.BadRequest)).Success);
        Assert.False((await f.Command(new() { Operation = "web-settings", WebControl = new() }, HttpStatusCode.BadRequest)).Success);
        Assert.Contains(f.Logs.Snapshot(), entry => entry.Actor.StartsWith("Web管理者/"));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(f.Logs.Snapshot()));
    }

    [Fact]
    public async Task WebCanEditRulesServicesAndTemporaryGrantsAndReadLogs()
    {
        await using var f = await Fixture.Create(); await f.Login();
        var state = await f.State(); var config = state.Configuration!;
        config.Services.Add(new() { Id = "cdn", Name = "画像CDN", Hosts = ["cdn.example.com"] });
        config.Policies.Add(new() { DeviceId = "pc", ServiceId = "cdn", AlwaysAllow = true });
        config.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "cdn", StartsAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) });
        var apply = await f.Command(new() { Operation = "apply", ExpectedRevision = 0, Configuration = config });
        Assert.True(apply.Success); Assert.Empty(apply.Configuration!.TemporaryGrants);
        Assert.Equal(2, apply.Configuration.Services.Count);
        var grant = await f.Command(new() { Operation = "grant", ExpectedRevision = 1, DeviceIds = ["pc"], ServiceId = "web", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10) });
        Assert.True(grant.Success);
        var issued = Assert.Single(grant.Configuration!.TemporaryGrants);
        Assert.StartsWith("Web管理者/", issued.CreatedBy);
        var revoke = await f.Command(new() { Operation = "revoke", ExpectedRevision = 2, GrantId = issued.Id });
        Assert.True(revoke.Success); Assert.True(revoke.Configuration!.TemporaryGrants.Single().Revoked);
        var snapshot = JsonDocument.Parse(await f.Client.GetStringAsync("api/status"));
        Assert.Equal(2, snapshot.RootElement.GetProperty("rules").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("api/logs")).StatusCode);
        var invalid = JsonDefaults.Clone(revoke.Configuration); invalid.Services[0].Hosts = ["https://example.com"];
        Assert.False((await f.Command(new() { Operation = "apply", ExpectedRevision = 3, Configuration = invalid }, HttpStatusCode.Conflict)).Success);
        Assert.Equal(3, (await f.Repository.LoadAsync(default)).Revision);
    }

    [Fact]
    public async Task ChangingPasswordRevokesSessionsAndPortConflictKeepsOldListener()
    {
        await using var f = await Fixture.Create(); await f.Login();
        using var occupied = new TcpListener(IPAddress.Any, 0); occupied.Start();
        await Assert.ThrowsAnyAsync<IOException>(() => f.Server.UpdateAsync(new() {
            Enabled = true, Port = ((IPEndPoint)occupied.LocalEndpoint).Port, ExpectedRevision = 1
        }, f.ProxyPort, default));
        Assert.True((await f.State()).Success); // 失敗した変更ではログインと旧ポートを維持する。
        Assert.Equal(1, f.Server.Status().Revision);
        await f.Server.UpdateAsync(new() { Enabled = true, Port = f.WebPort, Password = "replacement-password", ExpectedRevision = 1 }, f.ProxyPort, default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.PostAsJsonAsync("api/login", new { password = Password })).StatusCode);
        await f.Login("replacement-password");
        Assert.True((await f.State()).Success);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "api/logout"); logout.Headers.Add("X-CSRF-Token", f.Csrf);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.SendAsync(logout)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("api/status")).StatusCode);
    }

    [Fact]
    public async Task LocalWebSettingsRequireAdminAndCanStopWebWithoutStoppingProxy()
    {
        await using var f = await Fixture.Create();
        var request = new IpcRequest { Operation = "web-settings", WebControl = new() { Enabled = false, Port = f.WebPort, ExpectedRevision = 1 } };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var handle = typeof(ProxyWorker).GetMethod("Handle", flags)!;
        var denied = await (Task<IpcResponse>)handle.Invoke(f.Worker, [request, false, "unprivileged", CancellationToken.None])!;
        Assert.False(denied.Success); Assert.True(f.Server.Status().Running);
        var saved = await (Task<IpcResponse>)handle.Invoke(f.Worker, [request, true, "local-admin", CancellationToken.None])!;
        Assert.True(saved.Success); Assert.False(saved.WebControl!.Enabled); Assert.False(saved.WebControl.Running);
        Assert.True(((ProxyEngine)typeof(ProxyWorker).GetField("engine", flags)!.GetValue(f.Worker)!).Running);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task RepeatedLoginFailuresAreRateLimited()
    {
        await using var f = await Fixture.Create();
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.PostAsJsonAsync("api/login", new { password = "incorrect" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Client.PostAsJsonAsync("api/login", new { password = Password })).StatusCode);
    }

    [Fact]
    public async Task BrokenWebConfigurationDoesNotOpenUnauthenticatedListener()
    {
        var root = NewRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "web-control.json"), "{broken");
            await using var server = new WebControlServer(root, (_, _, _) => Task.FromResult(new IpcResponse()), (_, _, _) => { });
            await server.InitializeAsync(default);
            Assert.False(server.Status().Running);
            Assert.NotEmpty(server.Status().Error);
            await Assert.ThrowsAsync<JsonException>(() => server.UpdateAsync(new() { Enabled = true, Password = Password }, 3128, default));
            Assert.Equal("{broken", await File.ReadAllTextAsync(Path.Combine(root, "web-control.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SettingsPollingPreservesPasswordAndPortDraftAndOriginalRevision()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(openSettings: true);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainForm).GetField("connected", flags)!.SetValue(form, true);
                var update = typeof(MainForm).GetMethod("UpdateWebControl", flags)!;
                update.Invoke(form, [new WebControlStatus(false, 3129, false, 3)]);
                var port = (NumericUpDown)typeof(MainForm).GetField("webPort", flags)!.GetValue(form)!;
                var password = (TextBox)typeof(MainForm).GetField("webPassword", flags)!.GetValue(form)!;
                var enabled = (CheckBox)typeof(MainForm).GetField("webEnabled", flags)!.GetValue(form)!;
                Assert.Equal(3129, port.Value); Assert.False(enabled.Checked); Assert.True(password.UseSystemPasswordChar);
                port.Value = 3130; password.Text = "draft-not-saved"; enabled.Checked = true;
                update.Invoke(form, [new WebControlStatus(false, 3140, true, 4)]);
                Assert.Equal(3130, port.Value); Assert.Equal("draft-not-saved", password.Text); Assert.True(enabled.Checked);
                Assert.Equal(3L, typeof(MainForm).GetField("webEditRevision", flags)!.GetValue(form));
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void FirewallBrokerRunsOnlyFixedProtectedExecutableAndWebFirewallIsScoped()
    {
        var xml = System.Xml.Linq.XDocument.Parse(FirewallSyncTask.Xml(@"C:\Program Files\WhiteProxy\WhiteProxy.exe"));
        var ns = xml.Root!.Name.Namespace;
        Assert.Equal("--sync-firewall", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("S-1-5-18", xml.Descendants(ns + "UserId").Single().Value);
        Assert.Contains("GRGX;;;NS", FirewallSyncTask.Permissions);
        var security = new System.Security.AccessControl.RawSecurityDescriptor(FirewallSyncTask.Permissions);
        Assert.Equal(3, security.DiscretionaryAcl!.Count);
        object? scheduler = null, folder = null, validation = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)scheduler!).Connect(); folder = ((dynamic)scheduler).GetFolder("\\");
            // TASK_VALIDATE_ONLY: 構文をOSで検証し、タスクは登録しない。
            validation = ((dynamic)folder!).RegisterTask("WhiteProxy-test-" + Guid.NewGuid().ToString("N"), xml.ToString(), 1, "SYSTEM", null, 5, null);
        }
        finally
        {
            foreach (var instance in new[] { validation, folder, scheduler })
                if (instance is not null && System.Runtime.InteropServices.Marshal.IsComObject(instance)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
        }
        var rule = FirewallManager.CreateRule(new(FirewallManager.ExecutablePath, "*", 3129, ["LocalSubnet"], ""), FirewallManager.WebRuleName);
        try
        {
            dynamic web = rule;
            Assert.Equal("3129", (string)web.LocalPorts);
            Assert.Equal("LocalSubnet", (string)web.RemoteAddresses);
            Assert.Equal(3, (int)web.Profiles);
            Assert.Equal(FirewallManager.WebRuleName, (string)web.Name);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(rule); }
    }

    private const string Password = "test-web-control-password";
    private static string NewRoot() { var root = Path.Combine(Path.GetTempPath(), "WhiteProxy-web-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static int FreePort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root = "";
        internal ConfigurationRepository Repository = null!;
        internal LogStore Logs = null!;
        internal ProxyWorker Worker = null!;
        internal WebControlServer Server = null!;
        internal HttpClient Client = null!;
        internal string Csrf = "";
        internal int ProxyPort, WebPort;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture { Root = NewRoot(), ProxyPort = FreePort(), WebPort = FreePort() };
            f.Repository = new(f.Root);
            var config = new Configuration {
                Proxy = new() { Port = f.ProxyPort },
                Devices = [new() { Id = "pc", Name = "端末", Address = "127.0.0.1" }],
                Services = [new() { Id = "web", Name = "Web", Hosts = ["example.com"] }],
                Policies = [new() { DeviceId = "pc", ServiceId = "web" }]
            };
            await f.Repository.SaveAsync(config, default);
            f.Logs = new(f.Root); f.Worker = new(f.Repository);
            typeof(ProxyWorker).GetField("logs", Flags)!.SetValue(f.Worker, f.Logs);
            typeof(ProxyWorker).GetField("clock", Flags)!.SetValue(f.Worker, new ClockGuard(TimeProvider.System));
            await (Task)typeof(ProxyWorker).GetMethod("TryStartProxyAsync", Flags)!.Invoke(f.Worker, [CancellationToken.None])!;
            var store = new WebControlStore(f.Root);
            store.Save(store.Prepare(new() { Enabled = true, Password = Password, Port = f.WebPort }, f.ProxyPort));
            f.Server = new(f.Root, (request, actor, ct) => (Task<IpcResponse>)typeof(ProxyWorker).GetMethod("Handle", Flags)!.Invoke(f.Worker, [request, true, actor, ct])!,
                (operation, actor, detail) => f.Logs.Add(new(DateTimeOffset.UtcNow, "監査", "", "", 0, true, operation + ": " + detail, Actor: actor)));
            await f.Server.InitializeAsync(default);
            typeof(ProxyWorker).GetField("webControl", Flags)!.SetValue(f.Worker, f.Server);
            Assert.True(f.Server.Status().Running, f.Server.Status().Error);
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            f.Client = new(handler) { BaseAddress = new Uri($"https://localhost:{f.WebPort}/"), Timeout = TimeSpan.FromSeconds(10) };
            return f;
        }
        internal async Task<HttpResponseMessage> Login(string password = Password)
        {
            var response = await Client.PostAsJsonAsync("api/login", new { password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Csrf = document.RootElement.GetProperty("csrfToken").GetString()!;
            return response;
        }
        internal async Task<IpcResponse> State()
        {
            using var response = await Client.GetAsync("api/status"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Web専用のrules拡張はIPC DTOには含めない。
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return new() { Success = document.RootElement.GetProperty("success").GetBoolean(),
                Configuration = document.RootElement.GetProperty("configuration").Deserialize<Configuration>(JsonDefaults.Options),
                Status = document.RootElement.GetProperty("status").Deserialize<ServiceStatus>(JsonDefaults.Options) };
        }
        internal async Task<IpcResponse> Command(IpcRequest request, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "api/command") { Content = JsonContent.Create(request, options: JsonDefaults.Options) };
            message.Headers.Add("X-CSRF-Token", Csrf);
            using var response = await Client.SendAsync(message);
            Assert.Equal(expected, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<IpcResponse>(JsonDefaults.Options))!;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Server.DisposeAsync();
            (typeof(ProxyWorker).GetField("engine", Flags)!.GetValue(Worker) as ProxyEngine)?.Dispose(); Worker.Dispose();
            await Logs.DisposeAsync(); Directory.Delete(Root, true);
        }
    }
}
