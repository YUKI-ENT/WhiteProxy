using System.Security.Principal;

namespace WhiteProxy.Service;

internal static class RuntimeAccess
{
    internal static bool CanHost(SecurityIdentifier? user, bool administrator) => administrator ||
        user?.IsWellKnown(WellKnownSidType.NetworkServiceSid) == true ||
        user?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;

    internal static void RequireDataDirectoryAccess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (!CanHost(identity.User, administrator))
            throw new UnauthorizedAccessException("プロキシー本体には管理者権限が必要です。Visual Studioを終了し、「管理者として実行」で開き直してからコンソールをデバッグしてください。通常利用ではWindowsサービスを開始してください。");
    }
}
