using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WhiteProxy.Service;

namespace WhiteProxy.Admin;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [STAThread]
    private static void Main(string[] args)
    {
        var mode = StartupPolicy.Mode(args);
        if (mode == ApplicationMode.Gui && !args.Contains("--tray") && GuiInstance.ActivateExisting()) return;
        if (StartupPolicy.NeedsElevation(mode, IsAdmin()))
        {
            if (Debugger.IsAttached && mode is ApplicationMode.Gui or ApplicationMode.Console)
            {
                MessageBox.Show("WhiteProxyの管理GUIとコンソール本体は管理者権限で実行します。\n\nVisual Studioを終了し、「管理者として実行」で開き直してからデバッグしてください。\n発行したEXEを通常起動する場合は、起動時のUACで管理者権限を確認します。",
                    "WhiteProxy — 管理者権限が必要です", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Environment.ExitCode = 1;
                return;
            }
            try { Process.Start(StartupPolicy.ElevatedStart(Environment.ProcessPath!, args)); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                MessageBox.Show(ex.NativeErrorCode == 1223 ? "起動をキャンセルしました。管理GUIの利用には管理者権限が必要です。" : "管理者権限で起動できませんでした。\n" + ex.Message,
                    "WhiteProxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Environment.ExitCode = 1;
            }
            return;
        }
        if (mode is ApplicationMode.Service or ApplicationMode.Console)
        {
            if (mode == ApplicationMode.Console) AllocConsole();
            try { ServiceHost.RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("WhiteProxy起動失敗: " + ex.GetType().Name);
                if (mode == ApplicationMode.Console)
                    MessageBox.Show("プロキシー本体を起動できませんでした。\n\n管理者権限、C:\\ProgramData\\WhiteProxy のアクセス権、別のサービス/コンソールの起動状態を確認してください。\n\n" + ex.Message,
                        "WhiteProxy — 起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.SequenceEqual(["--sync-firewall"]))
        {
            try { Task.Run(() => ServiceOperations.ExecuteAsync("sync-firewall")).GetAwaiter().GetResult(); }
            catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine("Firewall再同期失敗: " + ex.GetType().Name); }
            return;
        }
        ApplicationConfiguration.Initialize();
        if (mode == ApplicationMode.ServiceManagement)
        {
            try { Task.Run(() => ServiceOperations.ExecuteAsync(args[1])).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                MessageBox.Show(ex.Message, "サービス操作エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }
        using var instance = new GuiInstance();
        if (!instance.Acquired)
        {
            if (args.Contains("--tray")) return;
            if (!GuiInstance.ActivateExisting()) MessageBox.Show("管理GUIは既に起動中です。画面が開くまでお待ちください。", "WhiteProxy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new MainForm(openSettings: args.Contains("--settings"), startInTray: args.Contains("--tray")));
    }
    internal static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    internal static Task<string> RunServiceOperationAsync(string operation)
    {
        if (!IsAdmin()) throw new UnauthorizedAccessException("管理GUIを閉じて起動し直し、起動時の管理者確認を許可してください。");
        return ServiceOperations.ExecuteAsync(operation);
    }
}
