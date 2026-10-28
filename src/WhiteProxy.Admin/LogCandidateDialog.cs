using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal static class LogCandidateDialog
{
    internal static EditorDialog Create(string root, string timeZoneId, IEnumerable<Device> devices, IReadOnlyList<string> existing,
        Action<IReadOnlyList<string>> accept)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var form = new EditorDialog("ログから候補抽出 — 拒否されたホスト", 850, "選択したホストを追加") { Width = 1100, MinimumSize = new(900, 650) };
        var ip = form.Add("端末IPv4 / CIDR", new ComboBox { Name = "CandidatePeer", DropDownStyle = ComboBoxStyle.DropDown });
        ip.Items.AddRange(devices.Select(d => d.Address).Distinct().Cast<object>().ToArray());
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime;
        DateTimePicker Field(DateTime value) => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy/MM/dd HH:mm:ss",
            Value = new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second) };
        var from = form.Add("開始日時", Field(now.AddHours(-1))); from.Name = "CandidateFrom";
        var until = form.Add("終了日時", Field(now.AddSeconds(1))); until.Name = "CandidateUntil";
        var tools = form.Add("検索", new FlowLayoutPanel { WrapContents = false }, 48);
        var search = new Button { Text = "候補を検索", AutoSize = true, MinimumSize = new(115, 32) };
        var cancel = new Button { Text = "検索を中止", AutoSize = true, MinimumSize = new(100, 32), Enabled = false };
        var selectAll = new Button { Text = "すべて選択", AutoSize = true, MinimumSize = new(100, 32) };
        var clear = new Button { Text = "選択を解除", AutoSize = true, MinimumSize = new(100, 32) };
        tools.Controls.AddRange([search, cancel, selectAll, clear]);
        var message = form.Add("検索状況", new Label { AutoSize = true, Text = "端末IPは空欄で全端末。開始以上・終了未満で保存済みの拒否通信ログを検索します。\n日時の表示と入力：" + zone.DisplayName }, 75);
        var grid = form.Add("候補（初期は未選択）", new DataGridView { Name = "HostCandidates", AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            EnableHeadersVisualStyles = false, GridColor = DashboardTheme.Line,
            ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(245, 247, 251), ForeColor = DashboardTheme.Ink, Padding = new(4) },
            DefaultCellStyle = new() { Padding = new(4), WrapMode = DataGridViewTriState.True, ForeColor = DashboardTheme.Ink,
                SelectionBackColor = Color.FromArgb(232, 240, 253), SelectionForeColor = DashboardTheme.Ink }, SelectionMode = DataGridViewSelectionMode.FullRowSelect }, 300);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "add", HeaderText = "追加", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 56, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var name in new[] { "ホスト", "TCPポート", "拒否回数", "最初", "最後", "拒否理由", "登録状況" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns["ホスト"]!.FillWeight = 170; grid.Columns["拒否理由"]!.FillWeight = 170;
        foreach (var (name, width) in new[] { ("TCPポート", 70), ("拒否回数", 65), ("最初", 110), ("最後", 110), ("登録状況", 75) })
        { grid.Columns[name]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; grid.Columns[name]!.Width = width; }
        grid.Columns["ホスト"]!.FillWeight = 160; grid.Columns["拒否理由"]!.FillWeight = 110;
        grid.Columns["TCPポート"]!.HeaderText = "ポート"; grid.Columns["拒否回数"]!.HeaderText = "回数"; grid.Columns["登録状況"]!.HeaderText = "登録";
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        form.Add("追加の扱い", new Label { AutoSize = true, Text = "選んだ完全一致ホストだけを編集欄へ戻します。ワイルドカード化やTCPポートの追加はしません。\nログには別アプリの通信も混ざります。必要な接続先を選び、親の「保存」で反映してください。" }, 80);
        CancellationTokenSource? searching = null;
        cancel.Click += (_, _) => searching?.Cancel();
        form.FormClosing += (_, _) => searching?.Cancel();
        foreach (var (button, value) in new[] { (selectAll, true), (clear, false) })
            button.Click += (_, _) => { foreach (DataGridViewRow row in grid.Rows) if (!row.Cells["add"].ReadOnly) row.Cells["add"].Value = value; };
        search.Click += async (_, _) =>
        {
            if (searching is not null) return;
            LogCandidateQuery query;
            try
            {
                var a = DateTime.SpecifyKind(from.Value, DateTimeKind.Unspecified); var b = DateTime.SpecifyKind(until.Value, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(a) || zone.IsInvalidTime(b) || zone.IsAmbiguousTime(a) || zone.IsAmbiguousTime(b)) throw new ArgumentException("曖昧な日時は指定できません。");
                query = new(ip.Text, new(TimeZoneInfo.ConvertTimeToUtc(a, zone)), new(TimeZoneInfo.ConvertTimeToUtc(b, zone)));
                LogCandidateSearch.Validate(query);
            }
            catch (ArgumentException ex) { EditorDialog.Error("端末IPと期間を確認してください。\n" + ex.Message); return; }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); searching = cancellation;
            search.Enabled = ip.Enabled = from.Enabled = until.Enabled = selectAll.Enabled = clear.Enabled = false; cancel.Enabled = true;
            grid.Rows.Clear(); message.Text = "保存済みログを検索しています…";
            try
            {
                var progress = new Progress<int>(count => { if (!form.IsDisposed && searching == cancellation) message.Text = $"検索中：{count} ファイル…"; });
                var result = await Task.Run(() => LogCandidateSearch.SearchAsync(root, query, cancellation.Token, progress), cancellation.Token);
                if (form.IsDisposed) return;
                grid.SuspendLayout(); grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                try
                {
                    foreach (var candidate in result.Candidates)
                    {
                        var registered = LogCandidateSearch.IsRegistered(candidate.Host, existing);
                        var ports = string.Join(", ", candidate.Ports.Take(8)) + (candidate.Ports.Count > 8 ? $" …ほか{candidate.Ports.Count - 8}" : "");
                        var index = grid.Rows.Add(false, candidate.Host, ports, candidate.Count,
                            TimeZoneInfo.ConvertTime(candidate.FirstUtc, zone).ToString("MM/dd HH:mm:ss"), TimeZoneInfo.ConvertTime(candidate.LastUtc, zone).ToString("MM/dd HH:mm:ss"), candidate.Reasons, registered ? "登録済み" : "未登録");
                        grid.Rows[index].Tag = candidate.Host; grid.Rows[index].Cells["add"].ReadOnly = registered;
                        if (registered) grid.Rows[index].DefaultCellStyle.ForeColor = Color.Gray;
                    }
                }
                finally { grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; grid.ResumeLayout(); }
                message.Text = $"{result.Files} ファイル / {result.Candidates.Count} ホスト。追加する候補をチェックしてください。" +
                    (result.Truncated ? "\n上限（256MiB / 2,000ホスト）で打ち切りました。期間を狭めて再検索してください。" : "") +
                    (result.UnreadableFiles > 0 || result.InvalidLines > 0 ? $"\n読めなかったファイル {result.UnreadableFiles} / 不正・書込途中の行 {result.InvalidLines}。結果は完全でない場合があります。" : "");
            }
            catch (OperationCanceledException) { if (!form.IsDisposed) message.Text = "検索を中止しました（手動中止または30秒経過）。期間を狭めて再検索してください。"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { if (!form.IsDisposed) message.Text = "検索できませんでした。保存先へのアクセスを確認してください。\n" + ex.Message; }
            finally
            {
                searching = null;
                if (!form.IsDisposed) { search.Enabled = ip.Enabled = from.Enabled = until.Enabled = selectAll.Enabled = clear.Enabled = true; cancel.Enabled = false; }
            }
        };
        form.ValidateInput = () =>
        {
            if (searching is not null) return EditorDialog.Error("検索が完了してから選択してください。");
            var selected = grid.Rows.Cast<DataGridViewRow>().Where(row => !row.Cells["add"].ReadOnly && row.Cells["add"].Value is true).Select(row => (string)row.Tag!).ToList();
            if (selected.Count == 0) return EditorDialog.Error("追加するホストを選択してください。");
            accept(selected); return true;
        };
        return form;
    }
}
