namespace WhiteProxy.Admin;

internal static class LogTableTheme
{
    internal static void Configure(DataGridView grid)
    {
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.GridColor = Color.FromArgb(228, 234, 241);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(236, 241, 248);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = grid.ColumnHeadersDefaultCellStyle.ForeColor;
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(42, 55, 73);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 232, 249);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(25, 49, 80);
        grid.DefaultCellStyle.Padding = new(10, 8, 10, 8);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
        // 宛先と理由の幅を確保し、狭い画面では横スクロールする。
        string[] names = ["時刻", "種別", "端末IP", "宛先ホスト", "パス", "ポート", "結果", "理由", "サービス", "バイト", "秒", "操作者SID"];
        int[] widths = [145, 70, 140, 210, 260, 75, 70, 320, 120, 100, 70, 150];
        for (var i = 0; i < names.Length; i++)
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = names[i], HeaderText = names[i], MinimumWidth = widths[i], FillWeight = widths[i] });
        foreach (var name in new[] { "ポート", "バイト", "秒" })
            grid.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Columns["結果"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.Columns["バイト"]!.DefaultCellStyle.Format = "N0";
    }

    internal static void ApplyRows(DataGridView grid)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            var result = Convert.ToString(row.Cells["結果"].Value);
            if (result is not ("許可" or "拒否"))
            {
                // 行を再利用するため、通信行から監査行へ変わった場合は色を戻す。
                row.DefaultCellStyle.BackColor = Color.Empty;
                row.DefaultCellStyle.SelectionBackColor = Color.Empty;
                row.DefaultCellStyle.SelectionForeColor = Color.Empty;
                var neutral = row.Cells["結果"].Style;
                neutral.BackColor = neutral.ForeColor = neutral.SelectionBackColor = neutral.SelectionForeColor = Color.Empty;
                continue;
            }
            bool allowed = result == "許可";
            row.DefaultCellStyle.BackColor = allowed ? Color.FromArgb(239, 249, 244) : Color.FromArgb(255, 242, 242);
            row.DefaultCellStyle.SelectionBackColor = allowed ? Color.FromArgb(215, 235, 227) : Color.FromArgb(247, 218, 223);
            row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(25, 49, 80);
            var style = row.Cells["結果"].Style;
            style.BackColor = allowed ? Color.FromArgb(211, 239, 224) : Color.FromArgb(252, 218, 220);
            style.ForeColor = allowed ? Color.FromArgb(25, 108, 70) : Color.FromArgb(158, 42, 53);
            style.SelectionBackColor = allowed ? Color.FromArgb(187, 223, 205) : Color.FromArgb(242, 192, 198);
            style.SelectionForeColor = style.ForeColor;
        }
    }
}
