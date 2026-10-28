using System.Drawing.Drawing2D;

namespace WhiteProxy.Admin;

internal static class DashboardTheme
{
    internal static readonly Color Ink = Color.FromArgb(40, 55, 76);
    internal static readonly Color Line = Color.FromArgb(231, 236, 243);

    internal static (Color Background, Color Text) Action(string name) => name switch
    {
        "edit" => (Color.FromArgb(37, 99, 235), Color.White),
        "grant" => (Color.FromArgb(218, 244, 234), Color.FromArgb(13, 114, 81)),
        "block" => (Color.FromArgb(255, 238, 210), Color.FromArgb(166, 92, 16)),
        "delete" => (Color.FromArgb(254, 226, 230), Color.FromArgb(185, 45, 66)),
        "clone" => (Color.FromArgb(236, 229, 252), Color.FromArgb(112, 76, 175)),
        "export" => (Color.FromArgb(223, 237, 255), Color.FromArgb(38, 90, 167)),
        "danger" => (Color.FromArgb(175, 45, 55), Color.White),
        _ => (Color.FromArgb(239, 242, 247), Ink)
    };

    internal static void StyleButton(Button button, string action)
    {
        var colors = Action(action);
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
        button.Font = new Font(button.Font.FontFamily, 9.5f, FontStyle.Bold);
        button.Padding = new(12, 5, 12, 5);
        void SetColors() { button.BackColor = button.Enabled ? colors.Background : Color.FromArgb(235, 239, 245); button.ForeColor = button.Enabled ? colors.Text : Color.FromArgb(162, 172, 188); }
        button.EnabledChanged += (_, _) => SetColors(); SetColors();
        void SetShape()
        {
            if (button.Width < 12 || button.Height < 12) return;
            using var path = new GraphicsPath(); var bounds = new Rectangle(0, 0, button.Width, button.Height); const int diameter = 12;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
            var previous = button.Region; button.Region = new Region(path); previous?.Dispose();
        }
        button.SizeChanged += (_, _) => SetShape(); SetShape();
    }

    internal static void StyleTable(DataGridView grid)
    {
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.GridColor = Line; grid.DefaultCellStyle.ForeColor = Ink; grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 240, 253); grid.DefaultCellStyle.Padding = new(10, 12, 10, 12);
        grid.RowTemplate.MinimumHeight = 64;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 247, 251);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(245, 247, 251);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(100, 116, 139);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
    }

    internal static void Rounded(Graphics graphics, Rectangle bounds, Color color, int radius = 6)
    {
        if (bounds.Width < radius * 2 || bounds.Height < radius * 2) return;
        var smoothing = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath(); var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        using var brush = new SolidBrush(color); graphics.FillPath(brush, path);
        graphics.SmoothingMode = smoothing;
    }

    internal static void PaintDecoration(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name is not ("状態" or "drag")) return;
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
        if (grid.Columns[e.ColumnIndex].Name == "drag")
        {
            using var brush = new SolidBrush(Color.FromArgb(145, 158, 179));
            var x = e.CellBounds.Left + e.CellBounds.Width / 2 - 4; var y = e.CellBounds.Top + e.CellBounds.Height / 2 - 7;
            for (var row = 0; row < 3; row++) for (var column = 0; column < 2; column++) e.Graphics!.FillEllipse(brush, x + column * 6, y + row * 6, 3, 3);
        }
        else
        {
            var value = Convert.ToString(e.Value) ?? "";
            var allowed = value is "● 開放中" or "有効"; var unknown = value == "■ 状態未確認";
            var color = unknown ? Color.FromArgb(102, 116, 138) : allowed ? Color.FromArgb(20, 123, 86) : Color.FromArgb(181, 72, 84);
            var background = unknown ? Color.FromArgb(233, 238, 245) : allowed ? Color.FromArgb(217, 242, 231) : Color.FromArgb(252, 228, 232);
            var bounds = new Rectangle(e.CellBounds.Left + 9, e.CellBounds.Top + (e.CellBounds.Height - 30) / 2, e.CellBounds.Width - 18, 30);
            Rounded(e.Graphics!, bounds, background, 12);
            using var font = new Font(grid.Font.FontFamily, 9, FontStyle.Bold);
            var text = value switch { "有効" => "● 有効", "無効" => "● 無効", _ => unknown ? "未確認" : allowed ? "● 開放中" : "● 遮断中" };
            TextRenderer.DrawText(e.Graphics!, text, font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        e.Handled = true;
    }
}
