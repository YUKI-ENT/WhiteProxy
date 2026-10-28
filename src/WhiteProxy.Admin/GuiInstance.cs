using System.Runtime.InteropServices;
using System.Text;

namespace WhiteProxy.Admin;

internal sealed class GuiInstance : IDisposable
{
    internal static string WindowTitle => $"WhiteProxy v{AppVersion.Current} — 管理コンソール（管理者）";
    internal static bool IsGuiTitle(string title) => title == "WhiteProxy — 管理コンソール（管理者）" ||
        title.StartsWith("WhiteProxy v", StringComparison.Ordinal) && title.EndsWith(" — 管理コンソール（管理者）", StringComparison.Ordinal);
    private readonly Mutex mutex;
    private bool disposed;
    internal bool Acquired { get; }

    internal GuiInstance(string name = @"Local\WhiteProxy.Admin.Gui")
    {
        mutex = new Mutex(false, name);
        try { Acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
    }

    internal static bool ActivateExisting()
    {
        var window = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            var text = new StringBuilder(512); GetWindowText(candidate, text, text.Capacity);
            if (!IsGuiTitle(text.ToString())) return true;
            window = candidate; return false;
        }, IntPtr.Zero);
        if (window == IntPtr.Zero) return false;
        RestoreWindow(window);
        return true;
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
}
