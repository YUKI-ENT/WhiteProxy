using System.Reflection;
using WhiteProxy.Admin;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class TrayTests
{
    private static ServiceInstallation Installation(string state, bool installed = true, string error = "")
        => new(installed, state, "", "", error);
    private static ServiceStatus Status(bool healthy = true)
        => new(true, "127.0.0.1:3128", 0, 0, 0, healthy, "", DateTimeOffset.UtcNow, 0);

    [Theory]
    [InlineData("Running", true, true, true, false, "Running", "稼働中")]
    [InlineData("Stopped", true, true, true, false, "Stopped", "停止中")]
    [InlineData("Running", false, true, true, false, "Attention", "未接続")]
    [InlineData("Running", true, false, true, false, "Attention", "更新")]
    [InlineData("Running", true, true, false, false, "Attention", "異常")]
    [InlineData("Running", true, true, true, true, "Attention", "全遮断")]
    [InlineData("StartPending", true, true, true, false, "Attention", "起動中")]
    [InlineData("StopPending", true, true, true, false, "Attention", "停止処理中")]
    public void TrayDistinguishesRunningStoppedAndUncertainStates(string state, bool connected, bool compatible,
        bool healthy, bool globalBlock, string appearance, string text)
    {
        var model = TrayStatus.From(Installation(state), connected, compatible, Status(healthy), globalBlock);
        Assert.True(model.Visible); Assert.Equal(appearance, model.Appearance.ToString()); Assert.Contains(text, model.Text);
        Assert.True(model.Text.Length <= 63);
    }

    [Fact]
    public void MissingRegistrationHidesTrayAndReadFailureNeverLooksRunning()
    {
        Assert.False(TrayStatus.From(Installation("NotInstalled", false), false, false, null, false).Visible);
        var failed = TrayStatus.From(Installation("Running", error: "unreadable"), true, true, Status(), false);
        Assert.True(failed.Visible); Assert.Equal(TrayAppearance.Attention, failed.Appearance);
    }

    [Fact]
    public async Task FormIconsTrayLifecycleAndExplicitExitWorkWithoutServiceMutation()
    {
        await Sta(() =>
        {
            using var form = new MainForm();
            // Keep this test independent of a real service/pipe while showing our own window.
            typeof(MainForm).GetField("refreshing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
            form.Show();
            Assert.Same(AppIcons.Application, form.Icon);
            using var editor = new EditorDialog("アイコン確認");
            Assert.Same(AppIcons.Application, editor.Icon);
            var update = typeof(MainForm).GetMethod("UpdateTray", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var closing = typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var tray = (NotifyIcon)typeof(MainForm).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            update.Invoke(form, [Installation("Stopped")]);
            Assert.True(tray.Visible); Assert.Same(AppIcons.Stopped, tray.Icon);
            form.WindowState = FormWindowState.Minimized;
            Assert.False(form.Visible);
            Assert.IsType<ToolStripMenuItem>(tray.ContextMenuStrip!.Items[0]).PerformClick();
            Assert.True(form.Visible); Assert.Equal(FormWindowState.Normal, form.WindowState);
            var close = new FormClosingEventArgs(CloseReason.UserClosing, false);
            closing.Invoke(form, [close]); Assert.True(close.Cancel); Assert.False(form.IsDisposed);
            var shutdown = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
            closing.Invoke(form, [shutdown]); Assert.False(shutdown.Cancel);
            update.Invoke(form, [Installation("NotInstalled", false)]); Assert.False(tray.Visible);
            var unregisteredClose = new FormClosingEventArgs(CloseReason.UserClosing, false);
            closing.Invoke(form, [unregisteredClose]); Assert.False(unregisteredClose.Cancel);
            update.Invoke(form, [Installation("Stopped")]);
            Assert.Equal("管理画面を開く", tray.ContextMenuStrip!.Items[0].Text);
            var exit = Assert.IsType<ToolStripMenuItem>(tray.ContextMenuStrip.Items[^1]);
            Assert.Contains("サービスは継続", exit.Text);
            exit.PerformClick(); Assert.True(form.IsDisposed); Assert.False(tray.Visible);
        });
    }

    [Fact]
    public async Task ExistingInstanceRestoresHiddenWindow()
    {
        await Sta(() =>
        {
            using var form = new Form { Text = "WhiteProxy tray restoration test" };
            form.Show(); form.Hide(); Assert.False(form.Visible);
            GuiInstance.RestoreWindow(form.Handle);
            Application.DoEvents();
            Assert.True(form.Visible); Assert.NotEqual(FormWindowState.Minimized, form.WindowState);
        });
    }

    [Fact]
    public void EmbeddedMultiResolutionIconsArePresentAndDistinct()
    {
        var icons = new[] { AppIcons.Application, AppIcons.Running, AppIcons.Stopped, AppIcons.Attention };
        Assert.All(icons, icon => Assert.NotEqual(IntPtr.Zero, icon.Handle));
        Assert.Equal(4, icons.Select(icon => icon.Handle).Distinct().Count());
        foreach (var name in new[] { "WhiteProxy", "Tray-running", "Tray-stopped", "Tray-attention" })
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream($"WhiteProxy.Icons.{name}.ico");
            Assert.NotNull(stream);
            foreach (var size in new[] { 16, 24, 32, 48, 128 })
            {
                stream.Position = 0;
                using var icon = new Icon(stream, new Size(size, size));
                Assert.Equal(new Size(size, size), icon.Size);
            }
            // ICO uses zero width/height bytes for 256 px; GDI+ itself chooses 128 px.
            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            Assert.Equal(0, reader.ReadUInt16()); Assert.Equal(1, reader.ReadUInt16());
            var count = reader.ReadUInt16();
            var has256 = false;
            for (var i = 0; i < count; i++)
            {
                var width = reader.ReadByte(); var height = reader.ReadByte();
                has256 |= width == 0 && height == 0;
                reader.ReadBytes(14);
            }
            Assert.True(has256);
        }
    }

    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
