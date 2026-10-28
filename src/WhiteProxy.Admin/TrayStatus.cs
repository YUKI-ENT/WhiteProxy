using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

internal enum TrayAppearance { Stopped, Running, Attention }

internal sealed record TrayStatus(bool Visible, TrayAppearance Appearance, string Text)
{
    internal static TrayStatus From(ServiceInstallation installation, bool connected, bool compatible,
        ServiceStatus? status, bool globalBlock)
    {
        if (!installation.Installed) return new(false, TrayAppearance.Stopped, "WhiteProxy — 未登録");
        if (installation.Error.Length > 0) return Attention("状態確認に失敗");
        if (installation.State == "Stopped") return new(true, TrayAppearance.Stopped, "WhiteProxy — 停止中");
        if (installation.State != "Running") return Attention(installation.State switch
        {
            "StartPending" => "起動中", "StopPending" => "停止処理中", _ => "状態確認中"
        });
        if (!connected) return Attention("サービス稼働・GUI未接続");
        if (!compatible) return Attention("サービス更新が必要");
        if (status is not { Healthy: true, Running: true }) return Attention("異常・遮断中");
        if (globalBlock) return Attention("稼働中・全遮断");
        return new(true, TrayAppearance.Running, "WhiteProxy — 稼働中");
    }

    private static TrayStatus Attention(string text) => new(true, TrayAppearance.Attention, "WhiteProxy — " + text);
}
