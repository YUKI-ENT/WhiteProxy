using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace WhiteProxy.Admin;

internal static class TrayAutostart
{
    internal const string TaskName = "WhiteProxy Tray";

    internal static string TaskXml(string executable, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XDocument(new XDeclaration("1.0", "utf-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                E("RegistrationInfo", E("Description", "ログオン後にWhiteProxyの状態をタスクトレイに表示します。")),
                E("Triggers", E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", userSid), E("Delay", "PT15S") })),
                E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    E("UserId", userSid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
                E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT0S") }),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"), E("Exec", new[] {
                    E("Command", executable), E("Arguments", "--tray"), E("WorkingDirectory", Path.GetDirectoryName(executable)!) }))))
            .ToString();
    }

    internal static async Task RegisterAsync(string executable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var path = Path.Combine(Path.GetTempPath(), "WhiteProxy-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(path, TaskXml(executable, identity.User!.Value), Encoding.Unicode);
            await RunAsync("/Create", "/TN", TaskName, "/XML", path, "/F");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    internal static async Task RemoveAsync()
    {
        // 旧版では未登録。存在しない場合の削除は不要。
        if (await RunAsync("/Query", "/TN", TaskName) == 0)
            await RunAsync("/Delete", "/TN", TaskName, "/F");
    }

    private static async Task<int> RunAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var detail = await output + await error;
        if (process.ExitCode != 0 && args[0] != "/Query")
            throw new InvalidOperationException("トレイのログオン時起動タスクを設定できませんでした。\n" + detail);
        return process.ExitCode;
    }
}
