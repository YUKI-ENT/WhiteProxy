using WhiteProxy.Service;

namespace WhiteProxy.Admin;

public sealed partial class MainForm
{
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip trayMenu = new();
    private bool exitGui;
    private bool startInTray;

    protected override void SetVisibleCore(bool value)
    {
        // ログオン起動時はウィンドウを一瞬表示することなくトレイで開始する。
        if (value && startInTray)
        {
            startInTray = false;
            if (ServiceDiagnostics.Inspect().Installed)
            {
                if (!IsHandleCreated) CreateHandle();
                tray.Visible = true;
                timer.Start();
                _ = RefreshAsync();
                value = false;
            }
        }
        base.SetVisibleCore(value);
    }

    private void InitializeTray()
    {
        Icon = AppIcons.Application;
        tray.Icon = AppIcons.Attention;
        tray.Text = "WhiteProxy — 状態確認中";
        trayMenu.Items.Add("管理画面を開く", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("設定・サービス", null, (_, _) => { RestoreFromTray(); tabs.SelectedIndex = 3; });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("GUIを終了（サービスは継続）", null, (_, _) => ExitGui());
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void UpdateTray(ServiceInstallation installation)
    {
        if (IsDisposed || Disposing) return;
        var model = TrayStatus.From(installation, connected, orderedRules, status, current.Proxy.GlobalBlock);
        tray.Icon = model.Appearance switch
        {
            TrayAppearance.Running => AppIcons.Running,
            TrayAppearance.Stopped => AppIcons.Stopped,
            _ => AppIcons.Attention
        };
        tray.Text = model.Text;
        tray.Visible = model.Visible;
        // Unregistering must not leave an invisible window without a tray entry.
        if (!model.Visible && !Visible && IsHandleCreated && !exitGui) RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitGui()
    {
        exitGui = true;
        Close();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && tray is { Visible: true }) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel && e.CloseReason == CloseReason.UserClosing && !exitGui && tray.Visible)
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Visible = false;
            tray.Dispose(); trayMenu.Dispose(); timer.Dispose(); toolTips.Dispose();
        }
        base.Dispose(disposing);
    }
}
