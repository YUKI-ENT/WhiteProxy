using System.Diagnostics;

namespace WhiteProxy.Admin;

internal static class FirewallConsole
{
    internal static ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mmc.exe")) { UseShellExecute = true };
        info.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "wf.msc"));
        return info;
    }

    internal static void Open() => Process.Start(StartInfo())?.Dispose();
}
