using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Win32;

namespace WhiteProxy.Service;

public sealed record ServiceInstallation(bool Installed, string State, string Executable, string Account, string Error, string StartMode = "Unknown")
{
    public string StartupDescription => !Installed
        ? "初回登録すると、Windows起動時に自動で開始する設定になります。"
        : StartMode switch
        {
            "Automatic" => "Windows起動時に自動開始します（ログイン・GUI起動は不要）。\n「停止」は今回だけの停止です。Windowsを再起動すると再び開始します。",
            "Manual" => "起動方法：手動。Windows再起動後は、この画面の「開始」が必要です。",
            "Disabled" => "起動方法：無効。Windows再起動後も自動では開始しません。",
            _ => "自動起動の設定を読み取れません。「詳細な診断情報を表示」で確認してください。"
        };
    public string Description => !Installed ? "未登録 — 初期設定・登録管理の「初回登録して開始」を押してください。" : State switch
    {
        "Running" => "Windowsサービス稼働中",
        "Stopped" => "停止中 — 「開始」を押してください。",
        "StartPending" => "サービス起動中…",
        "StopPending" => "サービス停止中…",
        _ => "状態: " + State
    };
}

public static class ServiceDiagnostics
{
    public static ServiceInstallation Inspect()
    {
        try
        {
            using var service = new ServiceController("WhiteProxy");
            var state = service.Status.ToString();
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WhiteProxy");
            var startMode = key?.GetValue("Start") switch { 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown" };
            return new(true, state, key?.GetValue("ImagePath") as string ?? "", key?.GetValue("ObjectName") as string ?? "", "", startMode);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        { return new(false, "NotInstalled", "", "", ""); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(true, "Unknown", "", "", "サービス状態の読取り失敗: " + ex.GetType().Name); }
    }
}
