using WhiteProxy.Admin;

namespace WhiteProxy.Windows.Tests;

public class MainFormLayoutTests
{
    [Fact]
    public async Task ServiceActionsShareOneRowAndGlobalSaveButtonsAreGone()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(openSettings: true);
                var controls = Descendants(form).ToList();
                var buttons = controls.OfType<Button>().ToList();
                var start = Assert.Single(buttons, b => b.Text == "▶ 開始");
                var stop = Assert.Single(buttons, b => b.Text == "■ 停止");
                var refresh = Assert.Single(buttons, b => b.Text == "↻ 更新");
                Assert.Same(start.Parent, stop.Parent); Assert.Same(start.Parent, refresh.Parent);
                Assert.False(Assert.IsType<FlowLayoutPanel>(start.Parent).WrapContents);
                var firewall = Assert.Single(buttons, b => b.Text == "Windows Firewallを開く");
                Assert.Same(start.Parent, firewall.Parent);
                Assert.DoesNotContain(buttons, b => b.Text is "再読込" or "設定を検証" or "設定を適用");
                var settings = Assert.Single(buttons, b => b.Text == "待受IP・ポート・運用設定");
                var registration = Assert.Single(buttons, b => b.Text == "初回登録して開始");
                Assert.Same(AncestorGroup(settings), AncestorGroup(registration));
                Assert.NotSame(AncestorGroup(settings), AncestorGroup(start));
                Assert.DoesNotContain(buttons, b => b.Text == "管理者として開く");
                var programUpdate = Assert.Single(buttons, b => b.Text == "サービスのプログラムを更新");
                Assert.Same(AncestorGroup(settings), AncestorGroup(programUpdate));
                Assert.Contains(controls.OfType<TabControl>(), t => t.SelectedTab?.Text == "設定・サービス");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    [Fact]
    public void FirewallButtonLaunchesSystemManagementConsoleWithFixedSnapin()
    {
        var info = FirewallConsole.StartInfo();
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "mmc.exe"), info.FileName);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "wf.msc"), Assert.Single(info.ArgumentList));
        Assert.True(info.UseShellExecute);
    }
    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static GroupBox? AncestorGroup(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is GroupBox group) return group;
        return null;
    }
}
