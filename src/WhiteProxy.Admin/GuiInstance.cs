using System.Runtime.InteropServices;
using System.Text;

namespace WhiteProxy.Admin;

internal sealed class GuiInstance : IDisposable
{
    internal static readonly uint ExitForUpgradeMessage = RegisterWindowMessage("WhiteProxy.Admin.ExitForUpgrade.v1");
    internal static string WindowTitle => $"WhiteProxy v{AppVersion.Current} — 管理コンソール（管理者）";
    internal static bool IsGuiTitle(string title) => title == "WhiteProxy — 管理コンソール（管理者）" ||
        title.StartsWith("WhiteProxy v", StringComparison.Ordinal) && title.EndsWith(" — 管理コンソール（管理者）", StringComparison.Ordinal);
    private readonly Mutex mutex;
    private bool disposed;
    internal bool Acquired { get; }

    internal GuiInstance(string name = @"Local\WhiteProxy.Admin.Gui", int waitMilliseconds = 0)
    {
        mutex = new Mutex(false, name);
        try { Acquired = mutex.WaitOne(waitMilliseconds); }
        catch (AbandonedMutexException) { Acquired = true; }
    }

    internal static bool ActivateExisting()
    {
        var (window, _) = FindExisting();
        if (window == IntPtr.Zero) return false;
        RestoreWindow(window);
        return true;
    }

    private static (IntPtr Window, string Title) FindExisting()
    {
        var window = IntPtr.Zero; var title = "";
        EnumWindows((candidate, _) =>
        {
            var text = new StringBuilder(512); GetWindowText(candidate, text, text.Capacity);
            if (!IsGuiTitle(text.ToString())) return true;
            window = candidate; title = text.ToString(); return false;
        }, IntPtr.Zero);
        return (window, title);
    }

    internal static bool SupportsUpgradeExit(string title) => IsGuiTitle(title) && title.StartsWith("WhiteProxy v", StringComparison.Ordinal) &&
        Version.TryParse(title[12..].Split(' ')[0], out var version) && version >= new Version(2026, 11, 21, 4);

    internal static async Task CloseForUpgradeAsync()
    {
        var (window, title) = FindExisting();
        if (window == IntPtr.Zero) return;
        if (!SupportsUpgradeExit(title))
            throw new InvalidOperationException("新しいEXEによるサービス自動更新を行います。現在の旧GUIをトレイの「GUIを終了」で終了し、新しいEXEをもう一度起動してください。サービスはそのままで構いません。");
        if (!PostMessage(window, ExitForUpgradeMessage, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("起動済みGUIを終了できませんでした。トレイからGUIを終了し、新しいEXEをもう一度起動してください。");
        for (var i = 0; i < 50 && IsWindow(window); i++) await Task.Delay(100);
        if (IsWindow(window)) throw new InvalidOperationException("起動済みGUIの終了を待っています。編集画面を閉じてGUIを終了し、新しいEXEをもう一度起動してください。");
    }

    internal static void RestoreWindow(IntPtr window)
    {
        // SW_RESTORE also shows a GUI hidden in the notification area.
        ShowWindowAsync(window, 9);
        SetForegroundWindow(window);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Acquired) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}
