using WhiteProxy.Core;

namespace WhiteProxy.Admin;

public sealed partial class MainForm
{
    private const string RuleDragFormat = "WhiteProxy.LocalRuleOrder";
    private bool ruleDragging;
    private Point dragStart;
    private string? pendingDragKey, dragToken;
    private Configuration? dragConfiguration;
    private int dragInsertion = -1;

    private void EnableRuleDragging()
    {
        policies.AllowDrop = true;
        policies.MouseDown += (_, e) =>
        {
            pendingDragKey = null;
            var hit = policies.HitTest(e.X, e.Y);
            if (e.Button != MouseButtons.Left || hit.RowIndex < 0 || hit.ColumnIndex != policies.Columns["drag"]!.Index ||
                rowActionBusy || !connected || !orderedRules) return;
            dragStart = e.Location; pendingDragKey = policies.Rows[hit.RowIndex].Tag as string;
            dragConfiguration = JsonDefaults.Clone(current);
            dragConfiguration.Policies = DashboardRules.List(dragConfiguration, status?.TimeUtc ?? DateTimeOffset.UtcNow);
        };
        policies.MouseMove += (_, e) =>
        {
            if (pendingDragKey is null || dragConfiguration is null || ruleDragging || e.Button != MouseButtons.Left) return;
            var threshold = new Rectangle(dragStart.X - SystemInformation.DragSize.Width / 2,
                dragStart.Y - SystemInformation.DragSize.Height / 2, SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
            if (threshold.Contains(e.Location)) return;
            ruleDragging = true; dragToken = Guid.NewGuid().ToString("N"); UpdateRuleControls();
            var data = new DataObject(); data.SetData(RuleDragFormat, dragToken);
            try { policies.DoDragDrop(data, DragDropEffects.Move); }
            finally
            {
                ruleDragging = false; pendingDragKey = dragToken = null; dragConfiguration = null; dragInsertion = -1;
                RefreshTables(); policies.Invalidate();
            }
        };
        policies.MouseUp += (_, _) => { if (!ruleDragging) { pendingDragKey = null; dragConfiguration = null; } };
        policies.CellMouseEnter += (_, e) => policies.Cursor = e.ColumnIndex == policies.Columns["drag"]!.Index && connected && orderedRules && !rowActionBusy ? Cursors.SizeNS : Cursors.Default;
        policies.DragEnter += (_, e) => e.Effect = IsLocalRuleDrag(e.Data) ? DragDropEffects.Move : DragDropEffects.None;
        policies.DragOver += (_, e) =>
        {
            if (!IsLocalRuleDrag(e.Data)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Move;
            var point = policies.PointToClient(new(e.X, e.Y));
            var first = policies.FirstDisplayedScrollingRowIndex;
            if (first >= 0)
            {
                if (point.Y < policies.ColumnHeadersHeight + 18 && first > 0) policies.FirstDisplayedScrollingRowIndex = first - 1;
                else if (point.Y > policies.ClientSize.Height - 24 && first < policies.Rows.Count - 1) policies.FirstDisplayedScrollingRowIndex = first + 1;
            }
            var hit = policies.HitTest(point.X, point.Y);
            dragInsertion = hit.RowIndex >= 0 ? hit.RowIndex + (point.Y > policies.GetRowDisplayRectangle(hit.RowIndex, false).Top + policies.Rows[hit.RowIndex].Height / 2 ? 1 : 0)
                : point.Y <= policies.ColumnHeadersHeight ? 0 : policies.Rows.Count;
            policies.Invalidate();
        };
        policies.DragLeave += (_, _) => { dragInsertion = -1; policies.Invalidate(); };
        policies.DragDrop += async (_, e) =>
        {
            if (!IsLocalRuleDrag(e.Data) || dragConfiguration is null || pendingDragKey is null || dragInsertion < 0) return;
            var candidate = dragConfiguration; var key = pendingDragKey; var insertion = dragInsertion;
            ruleDragging = false; dragInsertion = -1; policies.Invalidate();
            if (!DashboardRules.MoveTo(candidate, key, insertion)) return;
            rowActionBusy = true; UpdateRuleControls();
            try { await SaveConfigurationAsync(candidate); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
            { Info("順序を保存できませんでした。\n" + ex.Message); }
            finally { rowActionBusy = false; RefreshTables(); }
        };
        policies.Paint += (_, e) =>
        {
            if (dragInsertion < 0 || policies.Rows.Count == 0) return;
            var before = dragInsertion < policies.Rows.Count;
            var bounds = policies.GetRowDisplayRectangle(before ? dragInsertion : policies.Rows.Count - 1, false);
            var y = before ? bounds.Top : bounds.Bottom;
            using var pen = new Pen(Color.FromArgb(36, 91, 157), 3);
            e.Graphics.DrawLine(pen, 0, y, policies.ClientSize.Width, y);
        };
    }

    private bool IsLocalRuleDrag(IDataObject? data) => ruleDragging && !rowActionBusy && connected && orderedRules && dragToken is not null &&
        data?.GetDataPresent(RuleDragFormat) == true && data.GetData(RuleDragFormat) as string == dragToken;
}
