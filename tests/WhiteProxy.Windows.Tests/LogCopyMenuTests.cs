using System.ComponentModel;
using System.Reflection;
using WhiteProxy.Admin;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class LogCopyMenuTests
{
    [Fact]
    public async Task RightClickTargetsClickedRowAndEmptyHostsCannotBeCopied()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainForm).GetField("logEntries", flags)!.SetValue(form, new List<LogEntry>
                {
                    new(DateTimeOffset.UtcNow, "通信", "pc", "first.example", 443, true, ""),
                    new(DateTimeOffset.UtcNow, "通信", "pc", "second.example", 443, false, ""),
                    new(DateTimeOffset.UtcNow, "監査", "", "", 0, true, "")
                });
                typeof(MainForm).GetMethod("FilterLogs", flags)!.Invoke(form, null);
                var grid = (DataGridView)typeof(MainForm).GetField("logGrid", flags)!.GetValue(form)!;
                var menu = grid.ContextMenuStrip!;
                Assert.Equal(new[] { "宛先アドレスをコピー", "サービスに追加" }, menu.Items.Cast<ToolStripItem>().Select(item => item.Text));
                void RightClick(int row) => typeof(DataGridView).GetMethod("OnCellMouseDown", flags, [typeof(DataGridViewCellMouseEventArgs)])!.Invoke(grid,
                    [new DataGridViewCellMouseEventArgs(0, row, 1, 1, new MouseEventArgs(MouseButtons.Right, 1, 1, 1, 0))]);
                CancelEventArgs OpenMenu()
                {
                    var args = new CancelEventArgs();
                    typeof(ContextMenuStrip).GetMethod("OnOpening", flags)!.Invoke(menu, [args]);
                    return args;
                }
                RightClick(1);
                Assert.Equal("second.example", grid.CurrentRow!.Cells["宛先ホスト"].Value);
                Assert.True(grid.Rows[1].Selected); Assert.False(grid.Rows[0].Selected);
                Assert.False(OpenMenu().Cancel); Assert.True(menu.Items[0].Enabled);
                Assert.False(menu.Items[1].Enabled);
                typeof(MainForm).GetField("connected", flags)!.SetValue(form, true);
                typeof(MainForm).GetField("orderedRules", flags)!.SetValue(form, true);
                OpenMenu(); Assert.True(menu.Items[1].Enabled);
                RightClick(0); OpenMenu(); Assert.True(menu.Items[1].Enabled);
                RightClick(2);
                OpenMenu(); Assert.False(menu.Items[0].Enabled); Assert.False(menu.Items[1].Enabled);
                RightClick(-1);
                Assert.True(OpenMenu().Cancel);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
