namespace WhiteProxy.Admin;

internal static class AppIcons
{
    // Process-lifetime shared icons; dialogs and NotifyIcon borrow these handles.
    internal static Icon Application { get; } = Load("WhiteProxy");
    internal static Icon Running { get; } = Load("Tray-running");
    internal static Icon Stopped { get; } = Load("Tray-stopped");
    internal static Icon Attention { get; } = Load("Tray-attention");

    private static Icon Load(string name)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream($"WhiteProxy.Icons.{name}.ico")
            ?? throw new InvalidOperationException("アプリケーションアイコンが見つかりません。");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }
}
