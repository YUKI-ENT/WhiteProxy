using System.Runtime.CompilerServices;
using WhiteProxy.Service;

[assembly: InternalsVisibleTo("WhiteProxy.Windows.Tests")]
namespace WhiteProxy.Admin;

internal sealed record ServiceUiState(bool CanStart, bool CanStop, bool CanRefresh, bool CanConfigure,
    bool ShowRegister, bool ShowMaintenance, string StatusText, Color StatusColor)
{
    internal static ServiceUiState From(ServiceInstallation installation, bool proxyConnected, bool busy)
    {
        var stopped = !installation.Installed || installation.State == "Stopped";
        var running = installation.Installed && installation.State == "Running";
        var known = installation.Error.Length == 0 && (stopped || running);
        return new(
            !busy && known && installation.Installed && stopped && !proxyConnected,
            !busy && running,
            !busy,
            !busy && known && stopped && !proxyConnected,
            !installation.Installed && installation.Error.Length == 0,
            installation.Installed && stopped,
            busy ? "操作中…" : !installation.Installed ? "未登録" : installation.State switch
            {
                "Running" => proxyConnected ? "稼働中・接続済み" : "稼働中・接続待ち",
                "Stopped" => "停止中",
                "StartPending" => "起動中…",
                "StopPending" => "停止処理中…",
                _ => "状態を確認してください"
            },
            busy || installation.State is "StartPending" or "StopPending" ? Color.FromArgb(154, 99, 20) :
            running && proxyConnected ? Color.FromArgb(28, 117, 69) :
            running || installation.Error.Length > 0 ? Color.FromArgb(175, 45, 45) : Color.FromArgb(80, 91, 109));
    }
}
