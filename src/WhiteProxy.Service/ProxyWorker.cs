using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using WhiteProxy.Core;
using WhiteProxy.Proxy;
using WhiteProxy.Shared;

namespace WhiteProxy.Service;

public sealed class ProxyWorker(ConfigurationRepository repository) : BackgroundService
{
    private Configuration current = new();
    private ProxyEngine? engine;
    private LogStore? logs;
    private ClockGuard? clock;
    private readonly SemaphoreSlim mutation = new(1);
    private volatile bool healthy;
    private string error = "初期化中";
    private byte[] expectedHash = [];
    private DateTimeOffset lastHeartbeat;
    private readonly TimeProvider time = TimeProvider.System;
    private readonly StartupRetry startupRetry = new();
    private WebControlServer? webControl;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            healthy = false;
            engine?.Dispose();
            StartupReport.TryWriteFailure(repository.DirectoryPath, startupStage, ex);
            Environment.ExitCode = 1;
            throw;
        }
    }

    private string startupStage = "保存先の権限設定";
    private void Report(string stage, string state = "Starting", string message = "")
    {
        startupStage = stage;
        StartupReport.Write(repository.DirectoryPath, stage, state, message);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        repository.SecureDirectory();
        Report("ログ保存先の準備");
        logs = new(repository.DirectoryPath);
        Report("時計・設定の読込");
        var stamp = Path.Combine(repository.DirectoryPath, "last-clock-utc.txt");
        DateTimeOffset? saved = null;
        if (File.Exists(stamp))
        {
            if (!DateTimeOffset.TryParse(await File.ReadAllTextAsync(stamp, stoppingToken), out var parsed))
                saved = DateTimeOffset.MaxValue;
            else saved = parsed;
        }
        clock = new(time, saved);
        await TryStartProxyAsync(stoppingToken);
        Audit("起動", "Windowsサービス", error);
        using var ipcCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        startupStage = "管理用接続口の作成";
        if (healthy) Report(startupStage);
        using var readPipe = new NamedPipeListener(() => CreatePipe(false));
        using var adminPipe = new NamedPipeListener(() => CreatePipe(true));
        var read = ServePipe(readPipe, false, ipcCancellation.Token);
        var admin = ServePipe(adminPipe, true, ipcCancellation.Token);
        webControl = new(repository.DirectoryPath, HandleWebCommandAsync, Audit);
        if (healthy) Report("管理GUI接続待受", "Ready");
        try
        {
            await webControl.InitializeAsync(stoppingToken);
            using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await ticker.WaitForNextTickAsync(stoppingToken))
            {
                if (read.IsFaulted || admin.IsFaulted) throw new InvalidOperationException("IPC待受の作成に失敗しました。別のWhiteProxyプロセスを確認してください。");
                await mutation.WaitAsync(stoppingToken);
                try
                {
                    if (!clock.Check()) { healthy = false; error = "時計の異常を検知。時刻を修正してサービスを再起動してください。"; }
                    if (!logs.Healthy) { healthy = false; error = "ログの保存に失敗しました。保存先・容量を確認してサービスを再起動してください。"; }
                    if (startupRetry.Pending)
                    {
                        if (!clock.Healthy || !logs.Healthy)
                        {
                            startupRetry.Complete();
                            Report("起動再試行の中止", "Degraded", error);
                        }
                        else if (time.GetUtcNow() >= startupRetry.NextAttemptUtc)
                            await TryStartProxyAsync(stoppingToken);
                        // 再試行待ちのエラーを、未取得の設定ハッシュで上書きしない。
                        continue;
                    }
                    try
                    {
                        if (expectedHash.Length > 0)
                        {
                            var bytes = await File.ReadAllBytesAsync(repository.FilePath, stoppingToken);
                            if (!SHA256.HashData(bytes).SequenceEqual(expectedHash)) { healthy = false; error = "設定ファイルの外部変更・破損を検出。GUIから設定を再適用してください。"; }
                        }
                        if (time.GetUtcNow() - lastHeartbeat >= TimeSpan.FromSeconds(10))
                        {
                            var tmp = stamp + ".tmp";
                            await File.WriteAllTextAsync(tmp, time.GetUtcNow().ToString("O"), stoppingToken);
                            File.Move(tmp, stamp, true); lastHeartbeat = time.GetUtcNow();
                        }
                    }
                    catch (IOException) { healthy = false; error = "設定・時計ファイルへのアクセスに失敗しました。"; }
                    engine?.Sweep();
                }
                finally { mutation.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            ipcCancellation.Cancel();
            await webControl.DisposeAsync();
            engine?.Dispose();
            try { await Task.WhenAll(read, admin); } catch (OperationCanceledException) { }
            Audit("停止", "Windowsサービス", "");
            await logs.DisposeAsync();
        }
    }

    private Configuration? ActiveConfiguration() => healthy && clock?.Healthy == true && logs?.Healthy == true ? Volatile.Read(ref current) : null;
    private async Task TryStartProxyAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(repository.FilePath) && !File.Exists(repository.FilePath + ".bak"))
                await repository.SaveAsync(Templates.CreateDefault(), ct);
            current = await repository.LoadAsync(ct);
            expectedHash = SHA256.HashData(await File.ReadAllBytesAsync(repository.FilePath, ct));
            healthy = true; error = "";
            StartEngine();
            var recovered = startupRetry.Pending;
            startupRetry.Complete();
            Report("設定の読込・待受開始", "Ready");
            if (recovered) Audit("起動再試行成功", "Windowsサービス", "待受を開始しました。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            healthy = false;
            engine?.Dispose(); engine = null;
            var delay = clock?.Healthy == true && logs?.Healthy == true ? startupRetry.Failed(ex, time.GetUtcNow()) : null;
            if (delay is null) startupRetry.Complete();
            error = StartupReport.DescribeFailure("設定の読込・待受開始", ex) +
                (delay is { } wait ? $" {wait.TotalSeconds:0}秒後に自動再試行します。" : " 自動再試行を終了しました。原因を解消してサービスを再起動してください。");
            StartupReport.Write(repository.DirectoryPath, "設定の読込・待受開始", delay is null ? "Degraded" : "Retrying", error, ex);
            Audit("起動失敗", "Windowsサービス", error);
        }
    }
    private void StartEngine()
    {
        engine = new(ActiveConfiguration, time, e => logs!.Add(new(e.TimeUtc, "通信", e.Peer, e.Host, e.Port, e.Allowed, e.Reason, e.ServiceId, e.Bytes, e.Seconds,
            RequestPath: e.RequestPath, IsTunnel: e.IsTunnel)));
        logs!.RetentionDays = current.Proxy.LogRetentionDays;
        engine.Start();
    }
    private void Audit(string operation, string actor, string detail)
    {
        logs?.Add(new(time.GetUtcNow(), "監査", "", "", 0, true, operation + ": " + detail, Actor: actor));
    }

    private static NamedPipeServerStream CreatePipe(bool admin)
    {
        var acl = PipeAuthorization.CreateSecurity(admin);
        return NamedPipeServerStreamAcl.Create(admin ? Protocol.AdminPipe : Protocol.ReadPipe, PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, acl);
    }

    internal Task ServePipe(NamedPipeListener listener, bool admin, CancellationToken ct)
    {
        return listener.RunAsync(async (pipe, requestToken) =>
        {
            var request = await Protocol.ReadAsync<IpcRequest>(pipe, requestToken);
            var authorized = false; var actor = "";
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(true);
                actor = identity?.User?.Value ?? "unknown";
                authorized = PipeAuthorization.IsAdmin(identity);
            });
            var response = await Handle(request, admin && authorized, actor, requestToken);
            await Protocol.WriteResponseAsync(pipe, response, requestToken);
        }, ex => Audit("IPC要求拒否", "local", ex.GetType().Name), ct, admin ? TimeSpan.FromSeconds(45) : null);
    }

    private async Task<IpcResponse> Handle(IpcRequest request, bool authorized, string actor, CancellationToken ct)
    {
        if (request.Operation is null || request.Operation.Length > 32 || request.DeviceIds is null || request.DeviceIds.Count > 4096 ||
            request.DeviceIds.Any(id => id is null || id.Length > 64) || request.Reason is null || request.Reason.Length > 500 ||
            request.ServiceId is null || request.ServiceId.Length > 64 || request.GrantId is null || request.GrantId.Length > 64)
            return Fail("IPC要求の形式が不正です。");
        if (request.Operation == "web-settings")
        {
            if (!authorized) return Fail("管理者として起動してください。");
            if (webControl is null || request.WebControl is null) return Fail("サービスを最新版に更新してください。");
            try
            {
                var result = await webControl.UpdateAsync(request.WebControl, current.Proxy.Port, ct);
                Audit("Web管理設定", actor, $"enabled={result.Enabled}; port={result.Port}; revision={result.Revision}");
                return new() { Success = true, WebControl = result, Message = "Web設定を保存しました。" };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException or FormatException or System.Security.Cryptography.CryptographicException)
            { return Fail("Web設定を適用できませんでした。" + (ex is InvalidOperationException ? ex.Message : " ポート・証明書・保存先を確認してください。")); }
        }
        await mutation.WaitAsync(ct);
        try
        {
            if (request.Operation == "status") return new() { Success = true, Message = Protocol.OrderedRulesCapability, Configuration = JsonDefaults.Clone(current), Status = Status(), WebControl = webControl?.Status() };
            if (request.Operation == "logs") return new() { Success = true, Logs = logs!.Snapshot() };
            if (!authorized) { Audit("設定変更の権限拒否", actor, request.Operation.Length <= 40 ? request.Operation : "invalid"); return Fail("管理者として起動してください。"); }
            if (request.Operation == "validate")
            {
                if (request.Configuration is null) return Fail("設定がありません。");
                var errors = ConfigurationValidator.Validate(request.Configuration);
                return new() { Success = errors.Count == 0, Message = errors.Count == 0 ? "検証に成功しました。まだ適用されていません。" : string.Join("\n", errors) };
            }
            if (request.ExpectedRevision != current.Revision) return Fail("別の操作で設定が更新されています。再読込してください。");
            var next = JsonDefaults.Clone(current);
            switch (request.Operation)
            {
                case "apply":
                    if (request.Configuration is null) return Fail("設定がありません。");
                    next = request.Configuration;
                    // 一時開放は専用の操作のみ。操作者と最大有効期間の検証を回避させない。
                    next.TemporaryGrants = JsonDefaults.Clone(current.TemporaryGrants);
                    break;
                case "block": next.Proxy.GlobalBlock = request.Block; break;
                case "grant":
                    var now = time.GetUtcNow(); var start = request.StartsAtUtc?.ToUniversalTime() ?? now;
                    var end = request.ExpiresAtUtc?.ToUniversalTime();
                    if (request.DeviceIds.Count is < 1 or > 4096 || request.Reason.Length > 500 ||
                        !end.HasValue || end <= start || end <= now || end - now > TimeSpan.FromHours(next.Proxy.MaximumGrantHours) || start < now.AddSeconds(-30))
                        return Fail("対象端末、開始・終了と一時許可上限を確認してください。");
                    foreach (var device in request.DeviceIds.Distinct())
                    {
                        if (!next.Devices.Any(d => d.Id == device && d.Enabled && !d.Paused) || !next.Services.Any(s => s.Id == request.ServiceId && s.Enabled && s.Hosts.Count > 0))
                            return Fail("有効な端末・許可先登録済みサービスを選択してください。");
                        // 既存の枠を監査用に保持して取消し、新しい枠にまとめる。
                        var existing = next.TemporaryGrants.Where(g => g.DeviceId == device && g.ServiceId == request.ServiceId && !g.Revoked && g.ExpiresAtUtc > now).ToList();
                        if (existing.Any(g => g.ExpiresAtUtc > end)) return Fail("既存の期限より短くできません。即時終了を使用してください。");
                        foreach (var g in existing) g.Revoked = true;
                        next.TemporaryGrants.Add(new() { DeviceId = device, ServiceId = request.ServiceId, StartsAtUtc = start,
                            ExpiresAtUtc = end.Value, CreatedAtUtc = now, CreatedBy = actor, Reason = request.Reason });
                    }
                    break;
                case "revoke":
                    var grant = next.TemporaryGrants.FirstOrDefault(g => g.Id == request.GrantId);
                    if (grant is null) return Fail("一時許可が見つかりません。");
                    grant.Revoked = true; break;
                default: return Fail("未対応のIPC操作です。");
            }
            var validation = ConfigurationValidator.Validate(next);
            if (validation.Count > 0) return Fail(string.Join("\n", validation));
            if (webControl?.Status() is { Enabled: true } webState && next.Proxy.Port == webState.Port) return Fail("プロキシーポートはWeb管理ポートと別にしてください。");
            if (!clock!.Healthy || !logs!.Healthy) return Fail("時計またはログの異常を解消してサービスを再起動してください。");
            next.Revision = current.Revision + 1;
            var previous = current;
            try
            {
                await repository.SaveAsync(next, ct);
                expectedHash = SHA256.HashData(await File.ReadAllBytesAsync(repository.FilePath, ct));
                if (NeedsFirewallSync(previous, next) && string.Equals(repository.DirectoryPath, ServiceHost.DataDirectory, StringComparison.OrdinalIgnoreCase) && ServiceDiagnostics.Inspect().Installed)
                    await FirewallSyncTask.RunAsync(ct);
                Volatile.Write(ref current, next); healthy = true; error = "";
                bool restart = engine?.Running != true || previous.Proxy.ListenAddress != next.Proxy.ListenAddress ||
                    previous.Proxy.Port != next.Proxy.Port || previous.Proxy.MaxConnections != next.Proxy.MaxConnections;
                if (restart) { engine?.Dispose(); engine = null; StartEngine(); }
                else { logs.RetentionDays = next.Proxy.LogRetentionDays; engine?.Sweep(); }
                startupRetry.Complete();
                Report("設定適用・待受開始", "Ready");
                Audit(request.Operation, actor, $"revision={next.Revision}; devices={string.Join(',', request.DeviceIds)}; service={request.ServiceId}; grant={request.GrantId}; end={request.ExpiresAtUtc:O}; reason={request.Reason}");
                return new() { Success = true, Message = "適用しました。", Configuration = JsonDefaults.Clone(current), Status = Status() };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
            {
                healthy = false; error = "適用に失敗したため通信を遮断しました。保存先・待受IP・ポートを確認してください。";
                engine?.Sweep(); engine?.Dispose(); engine = null;
                Volatile.Write(ref current, previous);
                try { await repository.SaveAsync(previous, CancellationToken.None); expectedHash = SHA256.HashData(await File.ReadAllBytesAsync(repository.FilePath)); }
                catch { error += " 元の設定の保存にも失敗しました。"; }
                if (NeedsFirewallSync(previous, next) && string.Equals(repository.DirectoryPath, ServiceHost.DataDirectory, StringComparison.OrdinalIgnoreCase) && ServiceDiagnostics.Inspect().Installed)
                    try { await FirewallSyncTask.RunAsync(CancellationToken.None); } catch { error += " Firewallの復旧にも失敗しました。GUIから再同期してください。"; }
                Audit("適用失敗・ロールバック", actor, ex.GetType().Name);
                return Fail(error);
            }
        }
        finally { mutation.Release(); }
    }
    private ServiceStatus Status() => new(engine?.Running == true, current.Proxy.ListenAddress + ":" + current.Proxy.Port,
        engine?.SessionCount ?? 0, engine?.DeviceCount ?? 0, engine?.DeniedCount ?? 0,
        healthy && clock?.Healthy == true && logs?.Healthy == true, error, time.GetUtcNow(), logs?.Dropped ?? 0);
    private static IpcResponse Fail(string message) => new() { Message = message };
    private Task<IpcResponse> HandleWebCommandAsync(IpcRequest request, string actor, CancellationToken ct) => Handle(request, true, actor, ct);
    private static bool NeedsFirewallSync(Configuration before, Configuration after) => before.Proxy.ListenAddress != after.Proxy.ListenAddress || before.Proxy.Port != after.Proxy.Port ||
        !before.Devices.Where(d => d.Enabled).Select(d => d.Address).Order().SequenceEqual(after.Devices.Where(d => d.Enabled).Select(d => d.Address).Order());
}
