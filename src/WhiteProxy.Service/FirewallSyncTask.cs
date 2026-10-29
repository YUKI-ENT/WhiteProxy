using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace WhiteProxy.Service;

// NetworkServiceから、固定EXE・固定引数のFirewall再同期だけを要求できる。
public static class FirewallSyncTask
{
    public const string Name = "WhiteProxy Firewall Sync";
    internal const string Permissions = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;NS)";
    internal static string Xml(string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "保護済みWhiteProxy設定からアプリ固有のFirewall規則だけを再同期します。")),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "System"), E("UserId", "S-1-5-18"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("ExecutionTimeLimit", "PT1M") }),
            new XElement(ns + "Actions", new XAttribute("Context", "System"), E("Exec", new[] { E("Command", executable), E("Arguments", "--sync-firewall"), E("WorkingDirectory", Path.GetDirectoryName(executable)!) }))).ToString();
    }
    public static void Register(string executable)
    {
        object? scheduler = null, folder = null, task = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)scheduler!).Connect(); folder = ((dynamic)scheduler).GetFolder("\\");
            task = ((dynamic)folder!).RegisterTask(Name, Xml(executable), 6, "SYSTEM", null, 5, Permissions);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Firewall再同期タスクを登録できません。管理者権限とタスクスケジューラの状態を確認してください。", ex); }
        finally { Release(task, folder, scheduler); }
    }
    public static void Remove()
    {
        object? scheduler = null, folder = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)scheduler!).Connect(); folder = ((dynamic)scheduler).GetFolder("\\");
            try { ((dynamic)folder!).DeleteTask(Name, 0); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Firewall再同期タスクを削除できません。タスクスケジューラの状態を確認してください。", ex); }
        finally { Release(folder, scheduler); }
    }
    internal static async Task RunAsync(CancellationToken ct)
    {
        object? scheduler = null, folder = null, task = null, running = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)scheduler!).Connect(); folder = ((dynamic)scheduler).GetFolder("\\");
            task = ((dynamic)folder!).GetTask(Name);
            running = ((dynamic)task!).Run(null);
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(200, ct);
                if ((int)((dynamic)task).State != 4 && (int)((dynamic)task).State != 2)
                {
                    if ((int)((dynamic)task).LastTaskResult != 0) throw new InvalidOperationException("Firewall再同期タスクが失敗しました。");
                    return;
                }
                // RegisteredTask.State の Ready は3、Queuedは2、Runningは4。
            }
            throw new InvalidOperationException("Firewall再同期の応答がありません。管理GUIからサービスのプログラムを更新してください。");
        }
        catch (COMException ex) { throw new InvalidOperationException("Firewall再同期を開始できません。管理GUIからサービスのプログラムを更新してください。", ex); }
        finally { Release(running, task, folder, scheduler); }
    }
    private static void Release(params object?[] objects)
    {
        foreach (var instance in objects) if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
    }
}
