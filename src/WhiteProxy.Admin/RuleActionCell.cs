namespace WhiteProxy.Admin;

internal sealed class RuleActionCell : DataGridViewButtonCell
{
    internal bool ActionEnabled { get; set; }
    public override object Clone()
    {
        var cell = (RuleActionCell)base.Clone(); cell.ActionEnabled = ActionEnabled; return cell;
    }
    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates elementState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, elementState, value, formattedValue, errorText, cellStyle, advancedBorderStyle,
            paintParts & (DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border));
        var colors = ActionEnabled ? DashboardTheme.Action(OwningColumn!.Name) : (Color.FromArgb(235, 239, 245), Color.FromArgb(162, 172, 188));
        var height = Math.Min(32, cellBounds.Height - 10);
        var bounds = new Rectangle(cellBounds.Left + 3, cellBounds.Top + (cellBounds.Height - height) / 2, cellBounds.Width - 6, height);
        DashboardTheme.Rounded(graphics, bounds, colors.Item1);
        using var font = new Font(DataGridView!.Font.FontFamily, 9, FontStyle.Bold);
        TextRenderer.DrawText(graphics, Convert.ToString(formattedValue) ?? "", font, bounds, colors.Item2,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
