using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

internal static class ServiceOperations
{
    public static async Task<string> ExecuteAsync(string operation)
    {
        if (!Program.IsAdmin()) throw new InvalidOperationException("Windows管理者権限が必要です。");
        switch (operation)
        {
            case "setup":
            case "register":
                var controllers = System.ServiceProcess.ServiceController.GetServices();
                bool exists;
                try
                {
                    var existing = controllers.FirstOrDefault(c => c.ServiceName == "WhiteProxy");
                    exists = existing is not null;
                    if (existing is not null && existing.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        existing.Stop();
                        await Task.Run(() => existing.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)));
                    }
                }
                finally { foreach (var controller in controllers) controller.Dispose(); }
                var executable = DeploySingleFile();
                if (exists)
                    await Sc("config", "WhiteProxy", "binPath=", $"\"{executable}\" --service", "start=", "auto", "obj=", "NT AUTHORITY\\NetworkService");
                else
                    await Sc("create", "WhiteProxy", "binPath=", $"\"{executable}\" --service", "start=", "auto", "obj=", "NT AUTHORITY\\NetworkService", "DisplayName=", "WhiteProxy ホワイトリストプロキシー");
                await Sc("description", "WhiteProxy", "日本語GUI付きホワイトリスト式HTTP/HTTPSプロキシー。TLS復号なし。");
                await Sc("failure", "WhiteProxy", "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
                // サービスのTEMPに依存せず、NetworkServiceが展開できる保護済みの場所を指定する。
                var repository = new ConfigurationRepository(ServiceHost.DataDirectory);
                repository.SecureDirectory();
                Directory.CreateDirectory(Path.Combine(ServiceHost.DataDirectory, "runtime"));
                Directory.CreateDirectory(Path.Combine(ServiceHost.DataDirectory, "logs"));
                using (var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WhiteProxy", true)
                    ?? throw new InvalidOperationException("サービス登録情報を開けません。"))
                {
                    var environment = (serviceKey.GetValue("Environment") as string[] ?? [])
                        .Where(e => !e.StartsWith("DOTNET_BUNDLE_EXTRACT_BASE_DIR=", StringComparison.OrdinalIgnoreCase)).ToList();
                    environment.Add("DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + Path.Combine(ServiceHost.DataDirectory, "runtime"));
                    serviceKey.SetValue("Environment", environment.ToArray(), RegistryValueKind.MultiString);
                }
                var setupFirewall = await SyncSavedFirewallAsync();
                StartupReport.Write(ServiceHost.DataDirectory, "サービス登録", "Registered", "登録済み・停止中");
                if (operation == "setup")
                {
                    await Sc("start", "WhiteProxy");
                    await WaitForConnectionAsync();
                    return "サービス登録・開始・管理GUIへの接続を確認しました。\n" + setupFirewall;
                }
                break;
            case "start":
                var installation = ServiceDiagnostics.Inspect();
                if (!installation.Installed) throw new InvalidOperationException("サービスは未登録です。初期設定・登録管理の「初回登録して開始」を実行してください。");
                var startFirewall = await SyncSavedFirewallAsync();
                if (installation.State == "Stopped") await Sc("start", "WhiteProxy");
                await WaitForConnectionAsync();
                return "サービス開始と管理GUIへの接続を確認しました。\n" + startFirewall;
            case "stop":
                using (var stopping = new System.ServiceProcess.ServiceController("WhiteProxy"))
                {
                    if (stopping.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    { stopping.Stop(); await Task.Run(() => stopping.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30))); }
                }
                return "サービスを停止しました。";
            case "remove":
                using (var controller = new System.ServiceProcess.ServiceController("WhiteProxy"))
                {
                    if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    { controller.Stop(); controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                }
                await FirewallManager.RemoveAsync();
                await Sc("delete", "WhiteProxy"); break;
            default: throw new ArgumentException("未対応のサービス操作です。");
        }
        return operation == "register" ? "サービスを登録しました。続けて「開始」を実行してください。" : "サービス登録を削除しました。";
    }
    private static async Task<string> SyncSavedFirewallAsync()
    {
        var repository = new ConfigurationRepository(ServiceHost.DataDirectory);
        var config = File.Exists(repository.FilePath) ? await repository.LoadAsync(default) :
            File.Exists(repository.FilePath + ".bak") ? throw new InvalidOperationException("設定の現行ファイルがありません。バックアップを確認して復旧してください。") : Templates.CreateDefault();
        try { return await FirewallManager.SyncAsync(config); }
        catch (InvalidOperationException ex) { StartupReport.TryWriteFailure(ServiceHost.DataDirectory, "Windows Firewallの自動設定", ex); throw; }
    }
    internal static async Task WaitForConnectionAsync()
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            var installation = ServiceDiagnostics.Inspect();
            if (!installation.Installed || installation.State == "Stopped")
                throw new InvalidOperationException("サービスは起動後に停止しました。\n" + FailureDetails());
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var response = await new PipeClient().SendAsync(new() { Operation = "status" }, ct: timeout.Token);
                if (response.Success && response.Status is { } status)
                {
                    if (!status.Healthy || !status.Running) throw new InvalidOperationException("管理接続はできましたがプロキシーは異常状態です。\n" + status.Error);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
            await Task.Delay(250);
        }
        throw new InvalidOperationException("Windowsサービスから管理GUIへの応答がありません。\n" + FailureDetails());
    }
    private static string FailureDetails()
    {
        var report = StartupReport.Read(ServiceHost.DataDirectory);
        return report is null ? "起動情報を取得できません。設定画面でサービス状態と実行ファイルを確認してください。" :
            $"最終段階: {report.Stage}\n{report.Message}\n{report.ErrorType} 0x{report.HResult:X8}\n時刻: {report.TimeUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss}";
    }
    private static string DeploySingleFile()
    {
        var source = Environment.ProcessPath!;
        if (File.Exists(Path.ChangeExtension(source, ".runtimeconfig.json")))
            throw new InvalidOperationException("サービス登録には単一ファイル発行したWhiteProxy.exeを使用してください。開発時は管理者権限で --console を実行できます。");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhiteProxy");
        Directory.CreateDirectory(directory);
        if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("配置先にリンクは使用できません。");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in new[] { WellKnownSidType.BuiltinUsersSid, WellKnownSidType.NetworkServiceSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        var destination = Path.Combine(directory, "WhiteProxy.exe");
        if (!string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("配置先EXEにリンクは使用できません。");
            var temporary = Path.Combine(directory, "WhiteProxy.new.exe");
            if (File.Exists(temporary)) File.Delete(temporary);
            File.Copy(source, temporary); File.Move(temporary, destination, true);
        }
        var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        foreach (var sid in new[] { WellKnownSidType.BuiltinUsersSid, WellKnownSidType.NetworkServiceSid })
            fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(destination).SetAccessControl(fileAcl);
        return destination;
    }
    private static async Task Sc(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("サービス操作に失敗しました。\n" + output + error);
    }
}
