using System.Diagnostics;

namespace WhiteProxy.Admin;

internal enum ApplicationMode { Gui, Service, Console, ServiceManagement }

internal static class StartupPolicy
{
    internal static ApplicationMode Mode(string[] args)
    {
        if (args.Contains("--service")) return ApplicationMode.Service;
        if (args.Contains("--console")) return ApplicationMode.Console;
        if (args.Length == 2 && args[0] == "--manage-service" || args.SequenceEqual(["--sync-firewall"])) return ApplicationMode.ServiceManagement;
        return ApplicationMode.Gui;
    }

    // 同じEXEを使うNetworkServiceの起動経路ではUACを要求しない。
    internal static bool NeedsElevation(ApplicationMode mode, bool administrator) =>
        mode != ApplicationMode.Service && !administrator;

    internal static ProcessStartInfo ElevatedStart(string executable, string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
}
