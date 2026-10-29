using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using WhiteProxy.Core;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

public sealed partial class MainForm : Form
{
    private readonly PipeClient client = new();
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new(18, 10) };
    private readonly Label state = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.White, Font = new("Yu Gothic UI", 11), Padding = new(20, 12, 0, 0) };
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 75, Font = new("Yu Gothic UI", 12), Padding = new(10) };
    private readonly Label saveStatus = new() { Dock = DockStyle.Top, Height = 30, Padding = new(18, 5, 0, 0), Text = "変更は各編集画面の「保存」で反映されます。" };
    private readonly TextBox serviceInfo = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 960, Height = 165, BackColor = Color.FromArgb(246, 248, 252) };
    private readonly Label serviceOperation = new() { AutoSize = true, MaximumSize = new(950, 0), Padding = new(0, 8, 0, 8) };
    private readonly Label serviceBadge = new() { Dock = DockStyle.Top, Height = 34, Font = new("Yu Gothic UI", 12, FontStyle.Bold) };
    private readonly Label serviceStartup = new() { Dock = DockStyle.Top, Height = 48, ForeColor = Color.FromArgb(70, 82, 101) };
    private readonly Label registrationHint = new() { Width = 900, Height = 42, ForeColor = Color.FromArgb(70, 82, 101), Padding = new(3, 3, 0, 0) };
    private readonly ToolTip toolTips = new();
    private readonly Label firewallInfo = new() { AutoSize = true, MaximumSize = new(920, 0), Padding = new(0, 10, 0, 10), Text = "Windows Firewall：登録・開始時と端末IP/CIDRの変更保存時に、待受TCPポートを登録端末のIP/CIDRだけに自動許可します。\n対象はドメイン・プライベート接続。ループバック待受・端末未登録では外部向け許可を作りません。" };
    private readonly Label logStorageInfo = new() { AutoSize = true, MaximumSize = new(920, 0), Padding = new(0, 10, 0, 10) };
    private Button? serviceStart, serviceStop, serviceRefresh, settingsEdit, serviceRegister, serviceUpdate, serviceRemove;
    private bool serviceBusy;
    private string connectionHelp = "「設定・サービス」でサービスを登録・開始してください。";
    private readonly DataGridView services = Grid(), policies = Grid(), logGrid = Grid();
    private bool orderedRules, rowActionBusy;
    private readonly TextBox logSearch = new() { Width = 280, PlaceholderText = "端末・ホスト・パス・理由で検索" };
    private readonly ComboBox logKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly DateTimePicker logFrom = new() { Width = 140, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private readonly DateTimePicker logTo = new() { Width = 140, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private List<LogEntry> logEntries = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private Configuration current = Templates.CreateDefault();
    private ServiceStatus? status;
    private bool refreshing, connected;

    public MainForm() : this(false) { }

    public MainForm(bool openSettings, bool startInTray = false)
    {
        this.startInTray = startInTray;
        Text = GuiInstance.WindowTitle;
        InitializeTray();
        Size = new(1280, 860); MinimumSize = new(1050, 720); StartPosition = FormStartPosition.CenterScreen;
        Font = new("Yu Gothic UI", 10); BackColor = Color.FromArgb(246, 248, 252);
        state.Text = "サービスに接続しています…";
        summary.Text = "サービス状態を確認しています。初回は「設定・サービス」で登録・開始してください。";
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Color.FromArgb(28, 44, 70) };
        var title = new Label { Text = "WhiteProxy", Dock = DockStyle.Top, Height = 43, ForeColor = Color.White, Font = new("Yu Gothic UI", 20, FontStyle.Bold), Padding = new(18, 3, 0, 0) };
        header.Controls.Add(state); header.Controls.Add(title);
        Controls.Add(tabs); Controls.Add(saveStatus); Controls.Add(header);
        BuildDashboard(); BuildServices(); BuildLogs(); BuildSettings(); BuildHelp();
        if (openSettings) tabs.SelectedIndex = 3;
        using (var notices = typeof(MainForm).Assembly.GetManifestResourceStream("WhiteProxy.ThirdPartyNotices.txt"))
            if (notices is not null)
            {
                using var reader = new StreamReader(notices);
                Page("ライセンス").Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = reader.ReadToEnd() });
            }
        RefreshTables(); FilterLogs();
        timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); if (!IsDisposed) timer.Start(); };
    }

    private void BuildDashboard()
    {
        var page = Page("ダッシュボード");
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 52, ColumnCount = 2, RowCount = 1, Padding = new(0, 3, 0, 4) };
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        AddButton(bar, "＋ 許可ルールを追加", () => EditPolicy(false), tone: "edit");
        var note = new Label { Text = "基本は拒否。以下のルールにマッチするサービスのみ許可します。", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.FromArgb(70, 82, 101), Margin = new(10, 0, 0, 0) };
        toolTips.SetToolTip(note, note.Text + "\n右端のつまみをドラッグして順序を変更します。Cloneは直下へコピー。一時許可の期限は複製しません。");
        bar.Controls.Add(note, 1, 0);
        var overview = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new(0, 0, 0, 4) };
        var emergency = new GroupBox { Text = "緊急操作（全ルール共通）", Dock = DockStyle.Right, Width = 280, Padding = new(8, 4, 8, 6) };
        var emergencyButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        emergency.Controls.Add(emergencyButtons);
        AddButton(emergencyButtons, "■ すべて遮断", async () =>
        {
            if (Confirm("すべての通信を遮断します。解除までスケジュール・一時許可より優先されます。実行しますか。"))
                await SendMutation(new() { Operation = "block", Block = true });
        }, true);
        AddButton(emergencyButtons, "全遮断を解除", async () => { if (Confirm("全遮断を解除し、保存済みの許可ルールに戻しますか。")) await SendMutation(new() { Operation = "block", Block = false }); }, tone: "grant");
        summary.Dock = DockStyle.Fill;
        summary.Padding = new(6, 8, 10, 4);
        overview.Controls.Add(summary); overview.Controls.Add(emergency);
        policies.MultiSelect = false;
        page.Controls.Add(policies); page.Controls.Add(bar); page.Controls.Add(overview);
        foreach (var text in new[] { "状態", "対象端末 / IPv4・CIDR", "許可サービス", "通常の許可時間", "現在の理由" })
            policies.Columns.Add(new DataGridViewTextBoxColumn { Name = text, HeaderText = text, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var (key, text, width) in new[] { ("edit", "編集", 58), ("grant", "一時許可", 80), ("block", "一時遮断", 80), ("delete", "削除", 56), ("clone", "Clone", 64) })
            policies.Columns.Add(new DataGridViewButtonColumn { Name = key, HeaderText = "", Width = width,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, CellTemplate = new RuleActionCell(), SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new() { Padding = Padding.Empty, WrapMode = DataGridViewTriState.False } });
        policies.Columns.Add(new DataGridViewTextBoxColumn { Name = "drag", HeaderText = "", Width = 30,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new() { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = new(1), WrapMode = DataGridViewTriState.False }, ToolTipText = "つまみをドラッグして順序を変更" });
        policies.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        policies.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        policies.GridColor = DashboardTheme.Line;
        policies.DefaultCellStyle.ForeColor = DashboardTheme.Ink;
        policies.DefaultCellStyle.SelectionForeColor = DashboardTheme.Ink;
        policies.DefaultCellStyle.Padding = new(10, 15, 10, 15);
        policies.RowTemplate.MinimumHeight = 64;
        policies.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 247, 251);
        policies.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(245, 247, 251);
        policies.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(100, 116, 139);
        policies.ColumnHeadersHeight = 48;
        policies.Columns["状態"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        policies.Columns["状態"]!.Width = 108;
        policies.Columns["状態"]!.FillWeight = 70; policies.Columns["対象端末 / IPv4・CIDR"]!.FillWeight = 145;
        policies.Columns["許可サービス"]!.FillWeight = 90; policies.Columns["通常の許可時間"]!.FillWeight = 145; policies.Columns["現在の理由"]!.FillWeight = 140;
        policies.CellPainting += DashboardTheme.PaintDecoration;
        EnableRuleDragging();
        policies.CellContentClick += async (_, e) => await RunRuleAction(e.RowIndex, e.ColumnIndex);
        policies.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex is >= 0 and < 5 && !rowActionBusy) await EditPolicy(true); };
    }

    private AccessPolicy? SelectedRule() => DashboardRules.List(current, status?.TimeUtc ?? DateTimeOffset.UtcNow).FirstOrDefault(p => PairKey(p) == Selected(policies));

    private void UpdateRuleControls()
    {
        var rows = policies.Rows.Cast<DataGridViewRow>().ToList();
        var rules = DashboardRules.List(current, status?.TimeUtc ?? DateTimeOffset.UtcNow);
        var now = status?.TimeUtc ?? DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var rule = rules.FirstOrDefault(p => PairKey(p) == row.Tag as string);
            if (rule is null) continue;
            var editable = connected && orderedRules && !rowActionBusy && !ruleDragging;
            var grants = current.TemporaryGrants.Any(g => g.DeviceId == rule.DeviceId && g.ServiceId == rule.ServiceId && !g.Revoked && g.ExpiresAtUtc > now);
            foreach (var key in new[] { "edit", "grant", "block", "delete", "clone" })
            {
                var cell = (RuleActionCell)row.Cells[key];
                var enabled = editable && (key switch
                {
                    "grant" => grants || !current.Proxy.GlobalBlock && !rule.Deny && current.Devices.Any(d => d.Id == rule.DeviceId && d.Enabled && !d.Paused) && current.Services.Any(x => x.Id == rule.ServiceId && x.Enabled && x.Hosts.Count > 0),
                    _ => true
                });
                if (cell.ActionEnabled != enabled)
                {
                    cell.ActionEnabled = enabled;
                    policies.InvalidateCell(cell);
                }
            }
        }
    }

    private async Task RunRuleAction(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || rowActionBusy || policies.Rows[rowIndex].Cells[columnIndex] is not RuleActionCell { ActionEnabled: true }) return;
        var key = policies.Columns[columnIndex].Name;
        policies.ClearSelection(); policies.Rows[rowIndex].Selected = true;
        rowActionBusy = true; UpdateRuleControls();
        try
        {
            switch (key)
            {
                case "edit": await EditPolicy(true); break;
                case "clone": await CloneRule(); break;
                case "grant": await OpenGrant(); break;
                case "block": await OpenRuleBlock(); break;
                case "delete": await DeleteRule(); break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        { Info("操作を完了できませんでした。\n" + ex.Message); }
        finally { rowActionBusy = false; RefreshTables(); }
    }

    private async Task CloneRule()
    {
        if (!CanEdit() || SelectedRule() is not { } rule) return;
        var c = JsonDefaults.Clone(current);
        c.Policies = DashboardRules.List(c, status?.TimeUtc ?? DateTimeOffset.UtcNow);
        var copy = DashboardRules.CloneAfter(c, PairKey(rule));
        if (copy is null || !await SaveConfigurationAsync(c)) return;
        policies.ClearSelection();
        foreach (DataGridViewRow row in policies.Rows) if (row.Tag as string == PairKey(copy)) row.Selected = true;
    }

    private Task OpenRuleBlock()
    {
        if (!CanEdit() || SelectedRule() is not { } rule) return Task.CompletedTask;
        using var form = BlockEditor.Create(current, rule, SaveConfigurationAsync, Confirm);
        form.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task DeleteRule()
    {
        if (!CanEdit() || SelectedRule() is not { } rule) return;
        var c = JsonDefaults.Clone(current);
        var now = status?.TimeUtc ?? DateTimeOffset.UtcNow;
        if (current.TemporaryGrants.Any(g => g.DeviceId == rule.DeviceId && g.ServiceId == rule.ServiceId && !g.Revoked && g.ExpiresAtUtc > now))
        { Info("先に「一時許可」フォームで終了してください。一時許可が残った状態ではルールを削除できません。"); return; }
        if (!Confirm(rule.Deny ? "手動遮断中のルールを削除します。遮断も解除され、別サービスの重複する許可先へ通信できる可能性があります。削除しますか。" : "選択ルールを削除しますか。削除後は下位の一致ルールで判定されるため、通信が開通する場合があります。")) return;
        c.Policies.RemoveAll(p => PairKey(p) == PairKey(rule));
        await SaveConfigurationAsync(c);
    }
    private void BuildServices()
    {
        var page = Page("許可サービス"); var bar = Toolbar();
        AddButton(bar, "サービスを追加", () => EditService(false), tone: "grant");
        AddButton(bar, "選択サービスを編集", () => EditService(true), tone: "edit");
        AddButton(bar, "選択サービスをコピー", async () =>
        {
            if (!CanEdit()) return;
            var s = current.Services.FirstOrDefault(s => s.Id == Selected(services));
            if (s is not null)
            {
                var c = JsonDefaults.Clone(current);
                s = JsonDefaults.Clone(s); s.Id = Guid.NewGuid().ToString("N"); s.Name += " のコピー"; s.BuiltIn = false;
                c.Services.Add(s); await SaveConfigurationAsync(c);
            }
        }, tone: "clone");
        var transfer = Toolbar(50);
        AddButton(transfer, "JSONからインポート", ImportServices, tone: "grant");
        AddButton(transfer, "選択サービスをJSONにエクスポート", ExportServices, tone: "export");
        page.Controls.Add(services); page.Controls.Add(Note("初期候補はWindows Updateのみです。必要なサービスは追加・JSON取込で登録できます。ホストの空リストは必ず拒否します。無効化は既存接続にも適用します。")); page.Controls.Add(transfer); page.Controls.Add(bar);
        DashboardTheme.StyleTable(services); services.CellPainting += DashboardTheme.PaintDecoration;
        services.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditService(true); };
    }
    private async Task ImportServices()
    {
        if (!CanEdit()) return;
        using var file = new OpenFileDialog { Title = "許可サービスのJSONを選択", Filter = "サービスセット (*.json)|*.json", CheckFileExists = true };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            byte[] bytes;
            await using (var stream = new FileStream(file.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > ServicePacks.MaximumBytes) throw new InvalidDataException("サービスセットは1MB以内にしてください。");
                bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes);
            }
            var pack = ServicePacks.Parse(bytes);
            var plan = ServicePacks.Prepare(current, pack);
            if (plan.Added.Count == 0) { Info("すべて登録済みのため、追加するサービスはありません。"); return; }
            using var form = ServiceImportEditor.Create(pack.Name, plan, SaveConfigurationAsync);
            form.ShowDialog(this);
        }
        catch (InvalidDataException ex) { Info("インポートできませんでした。\n" + ex.Message); }
    }

    private async Task ExportServices()
    {
        if (!connected) { Info("サービスに接続して最新の許可サービスを取得してからエクスポートしてください。"); return; }
        var ids = services.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag as string).OfType<string>().ToHashSet();
        var selected = current.Services.Where(service => ids.Contains(service.Id)).Select(JsonDefaults.Clone).ToList();
        if (selected.Count == 0) { Info("エクスポートするサービスを選択してください。Ctrl/Shiftキーで複数選択もできます。"); return; }
        try
        {
            var bytes = ServicePacks.Export(selected);
            using var file = new SaveFileDialog { Title = "選択サービスをJSONにエクスポート", Filter = "サービスセット (*.json)|*.json",
                DefaultExt = "json", AddExtension = true, OverwritePrompt = true, FileName = "WhiteProxy-services.json" };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            await ServicePacks.WriteExportAsync(file.FileName, bytes);
            saveStatus.Text = $"{selected.Count} 件の許可サービスをJSONにエクスポートしました。";
        }
        catch (InvalidDataException ex) { Info("エクスポートできませんでした。\n" + ex.Message); }
    }

    private void BuildLogs()
    {
        LogTableTheme.Configure(logGrid);
        var page = Page("通信・監査ログ"); var bar = Toolbar(98);
        AddButton(bar, "ログを取得", async () => { var r = await client.SendAsync(new() { Operation = "logs" }); logEntries = r.Logs; FilterLogs(); });
        AddButton(bar, "表示結果をCSV出力", ExportLogs);
        AddButton(bar, "拒否ホストをサービスへ追加", AddDeniedHost);
        logKind.Items.AddRange(["すべて", "許可", "拒否", "監査"]); logKind.SelectedIndex = 0;
        bar.Controls.AddRange([logSearch, logKind, new Label { Text = "開始", AutoSize = true }, logFrom, new Label { Text = "終了", AutoSize = true }, logTo]);
        logSearch.TextChanged += (_, _) => FilterLogs(); logKind.SelectedIndexChanged += (_, _) => FilterLogs();
        logFrom.ValueChanged += (_, _) => FilterLogs(); logTo.ValueChanged += (_, _) => FilterLogs();
        page.Controls.Add(logGrid); page.Controls.Add(Note("直近1,000件。HTTPはパスを表示。HTTPS等のCONNECTはTLS非復号のためパス取得不可です。\nクエリ・本文・Cookie・認証ヘッダーは記録しません。JSONLはlogs/に保存。CSVは現在のフィルタ結果を出力します。")); page.Controls.Add(bar);
    }
    private void BuildSettings()
    {
        var page = Page("設定・サービス"); var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(18) };
        var operationGroup = new GroupBox { Text = "サービスの操作", Width = 950, Height = 178, Padding = new(14, 24, 14, 12) };
        var operationRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, WrapContents = false };
        serviceStart = AddButton(operationRow, "▶ 開始", () => RunServiceActionAsync("start"));
        serviceStop = AddButton(operationRow, "■ 停止", () => RunServiceActionAsync("stop"));
        serviceRefresh = AddButton(operationRow, "↻ 更新", RefreshAsync);
        toolTips.SetToolTip(serviceRefresh, "サービス状態と画面の表示を更新します。通常は2秒ごとに自動更新します。");
        var openFirewall = AddButton(operationRow, "Windows Firewallを開く", () => { FirewallConsole.Open(); return Task.CompletedTask; }, tone: "export");
        toolTips.SetToolTip(openFirewall, "詳細設定付きWindows Defenderファイアウォールを開きます。受信の規則 → WhiteProxy-Managed-Inbound → スコープで送信元範囲を確認できます。");
        operationGroup.Controls.Add(serviceStartup); operationGroup.Controls.Add(operationRow); operationGroup.Controls.Add(serviceBadge);
        body.Controls.Add(operationGroup);
        BuildWebControl(body);

        var setupGroup = new GroupBox { Text = "初期設定・登録管理（停止中のみ）", Width = 950, Height = 210, Padding = new(14, 24, 14, 12) };
        var setupRows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var settingsRow = new FlowLayoutPanel { Width = 910, Height = 48, WrapContents = false };
        settingsEdit = AddButton(settingsRow, "待受IP・ポート・運用設定", EditSettings);
        settingsRow.Controls.Add(new Label { Text = "起動中は変更できません。変更は次回開始時に反映します。", AutoSize = true, Padding = new(8, 10, 0, 0) });
        var registrationRow = new FlowLayoutPanel { Width = 910, Height = 48, WrapContents = false };
        serviceRegister = AddButton(registrationRow, "初回登録して開始", () => RunServiceActionAsync("setup"));
        serviceUpdate = AddButton(registrationRow, "サービスのプログラムを更新", () => RunServiceActionAsync("setup"));
        serviceRemove = AddButton(registrationRow, "サービス登録を削除", () => RunServiceActionAsync("remove"));
        toolTips.SetToolTip(serviceUpdate, "新しいWhiteProxy.exeへ入れ替える操作です。このEXEを登録先へコピーし、設定とログを保持してサービスを開始します。");
        setupRows.Controls.Add(settingsRow); setupRows.Controls.Add(registrationRow); setupRows.Controls.Add(registrationHint); setupGroup.Controls.Add(setupRows);
        body.Controls.Add(setupGroup);
        body.Controls.Add(serviceOperation);
        var showDetails = new CheckBox { Text = "詳細な診断情報を表示", AutoSize = true, Padding = new(0, 8, 0, 4) };
        serviceInfo.Visible = false;
        showDetails.CheckedChanged += (_, _) => serviceInfo.Visible = showDetails.Checked;
        body.Controls.Add(showDetails); body.Controls.Add(serviceInfo);
        body.Controls.Add(new Label { Text = "データ保存先: " + ServiceHost.DataDirectory, AutoSize = true });
        body.Controls.Add(logStorageInfo); body.Controls.Add(firewallInfo);
        page.Controls.Add(body);
        UpdateServiceControls();
    }

    private async Task RunServiceActionAsync(string operation)
    {
        if (serviceBusy) return;
        if ((operation is "stop" or "remove" || operation == "setup" && ServiceDiagnostics.Inspect().State == "Running") &&
            !Confirm("サービスの停止・更新は通信を切断します。実行しますか。")) return;
        serviceBusy = true; UpdateServiceControls();
        serviceOperation.Text = "操作中… 管理者確認（UAC）が表示された場合は許可してください。";
        try { serviceOperation.Text = await Program.RunServiceOperationAsync(operation); }
        catch { serviceOperation.Text = "操作に失敗しました。「詳細な診断情報を表示」で原因を確認できます。"; throw; }
        finally { serviceBusy = false; await RefreshAsync(); UpdateServiceControls(); }
    }

    private void UpdateServiceControls(ServiceInstallation? installation = null)
    {
        if (serviceStart is null || IsDisposed || Disposing) return;
        installation ??= ServiceDiagnostics.Inspect();
        UpdateTray(installation);
        var model = ServiceUiState.From(installation, connected, serviceBusy);
        serviceBadge.Text = "● " + model.StatusText; serviceBadge.ForeColor = model.StatusColor;
        serviceStartup.Text = installation.StartupDescription;
        registrationHint.Text = !installation.Installed
            ? "初回登録：サービス・Firewall・ログオン時のトレイ起動を設定して開始します。\nこのEXEはProgram Files\\WhiteProxyへ配置されます。"
            : installation.State != "Stopped"
                ? "登録済みです。待受設定・プログラム更新・登録削除は、上の「停止」で停止してから操作できます。"
                : "プログラム更新：このEXEで登録済みサービスのプログラムを入れ替えて開始します。\n設定とログは保持します。毎回の起動で行う必要はありません。";
        SetActionStyle(serviceStart, model.CanStart, Color.FromArgb(28, 117, 69), true);
        SetActionStyle(serviceStop!, model.CanStop, Color.FromArgb(175, 45, 45), true);
        SetActionStyle(serviceRefresh!, model.CanRefresh, Color.FromArgb(36, 91, 157));
        SetActionStyle(settingsEdit!, model.CanConfigure, Color.FromArgb(28, 44, 70));
        serviceRegister!.Visible = model.ShowRegister;
        SetActionStyle(serviceRegister, model.CanConfigure && model.ShowRegister, Color.FromArgb(36, 91, 157));
        serviceUpdate!.Visible = serviceRemove!.Visible = model.ShowMaintenance;
        SetActionStyle(serviceUpdate, model.CanConfigure && model.ShowMaintenance, Color.FromArgb(36, 91, 157));
        SetActionStyle(serviceRemove, model.CanConfigure && model.ShowMaintenance, Color.FromArgb(175, 45, 45));
    }

    private static void SetActionStyle(Button button, bool enabled, Color color, bool solid = false)
    {
        button.Enabled = enabled;
        button.BackColor = !enabled ? Color.FromArgb(232, 235, 240) : solid ? color : Color.White;
        button.ForeColor = !enabled ? Color.FromArgb(140, 147, 159) : solid ? Color.White : color;
        button.FlatAppearance.BorderColor = enabled ? color : Color.FromArgb(211, 216, 223);
    }
    private void BuildHelp()
    {
        var page = Page("ヘルプ");
        page.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Color.White, Text = "WhiteProxy の使い方\r\n\r\n1. WhiteProxy.exeを起動してUACを許可します。管理GUI内のすべての操作を管理者権限で行います。\r\n2. 初期設定・登録管理で待受設定を保存し、「初回登録して開始」を押します。許可サービスで接続先を確認します。\r\n3. ダッシュボードの「許可ルールを追加」で端末IP/CIDR・許可サービス・許可時間をまとめて保存します。初期値の「一時許可のみ」は保存だけでは開通しません。\r\n4. ダッシュボードでルールを選び、各行の「一時許可」で新規・延長・終了、「一時遮断」で遮断・解除を行います。右端のつまみをドラッグして優先順位を変えます。時間外の行は読み飛ばし、最初に一致する有効な許可または明示的な一時遮断を適用します。各編集画面の「保存」で反映します。\r\n5. LANの待受IP等を変える場合は、サービスを停止して初期設定・登録管理で保存します。\r\n\r\n初期設定は127.0.0.1:3128、端末未登録です。未知端末・未知ホストは拒否します。\r\n\r\nサービス未接続の場合\r\n「設定・サービス」で開始してください。既に登録されている場合は別のコンソールモードのプロセスを停止します。設定破損時はバックアップを確認し、GUIで検証・再適用してください。\r\n\r\n時間制御\r\n終了時刻は含みません。時間切れはダウンロードやWebSocketも強制切断します。別の有効な許可がある場合は継続します。時計異常・ログ保存失敗は遮断し、原因解消後にサービス再起動が必要です。\r\n\r\n制約\r\n・登録済みでは×・最小化でトレイへ格納します。ダブルクリックで戻り、右クリックの「GUIを終了」で終了します。サービスは継続します。自動起動設定ではWindows再起動後もログイン前に開始します。「停止」は今回だけの停止です。\r\n・TLSを復号しません。同一ホスト内のURLの違いを判別できません。\r\n・プロキシーを使わない通信は本アプリでは遮断できません。VLAN境界のFirewallで出口を制限してください。\r\n・QUIC/UDP、直接通信、DoHなどの経路は別途制限が必要です。\r\n・Windows UpdateはWinHTTP/BITS/Delivery Optimizationなどの設定と一部の直接通信が関係します。一時開放だけで更新成功を保証しません。\r\n・「更新を1回だけ」は更新完了検出ではなく、指定した時間枠だけの許可です。\r\n・外部AIへの患者情報や機密情報の送信を安全化するものではありません。\r\n\r\n詳細は同梱のREADMEおよびdocsの運用・セキュリティ・クライアント設定手順を参照してください。" });
    }

    private async Task RefreshAsync()
    {
        if (refreshing || IsDisposed || ruleDragging) return; refreshing = true;
        try
        {
            var r = await client.SendAsync(new() { Operation = "status" });
            if (IsDisposed || Disposing) return;
            if (!r.Success || r.Configuration is null || r.Status is null) throw new IOException();
            connected = true; orderedRules = r.Message == Protocol.OrderedRulesCapability; current = r.Configuration; status = r.Status;
            UpdateWebControl(r.WebControl);
            state.Text = $"{(status.Healthy && status.Running ? "● 稼働中" : "■ エラー・遮断中")}   {status.Listen}    |    管理者    |    {(current.Proxy.GlobalBlock ? "管理者による全遮断" : "ポリシー制御中")}";
            summary.Text = $"接続中 {status.Connections} セッション / {status.Devices} 端末    拒否 {status.Denied:N0} 件（起動後）\n" +
                (status.Healthy ? "×・最小化でトレイへ格納します。右クリックでGUIを終了してもサービスは継続します。" : status.Error + $" ログ欠損: {status.DroppedLogs}");
            if (!orderedRules) summary.Text = "サービス本体が旧版です。「設定・サービス」で停止し、このEXEから「サービスのプログラムを更新」を実行してください。";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (IsDisposed || Disposing) return;
            connected = false;
            UpdateWebControl(null);
            var installation = ServiceDiagnostics.Inspect();
            var report = StartupReport.Read(ServiceHost.DataDirectory);
            connectionHelp = installation.Description;
            if (installation.Installed && installation.State == "Running")
                connectionHelp += "。管理GUIへの接続に失敗しています。停止後、管理者の初期設定・登録管理で「サービスのプログラムを更新」を実行してください。";
            if (report is not null && installation.Installed && report.State is "Failed" or "Degraded")
                connectionHelp += "\n直近の起動情報: " + report.Stage + " / " + report.Message;
            state.Text = "■ " + installation.Description;
            summary.Text = connectionHelp;
        }
        finally
        {
            refreshing = false;
            if (!IsDisposed && !Disposing)
            {
                var installation = ServiceDiagnostics.Inspect();
                var report = StartupReport.Read(ServiceHost.DataDirectory);
                serviceInfo.Text = $"Windowsサービス: {installation.Description}\r\nGUIとの接続: {(connected ? "接続済み" : "未接続")}\r\n実行ファイル: {installation.Executable}\r\n実行アカウント: {installation.Account}";
                if (report is not null) serviceInfo.Text += $"\r\n直近の起動情報 ({report.TimeUtc.ToLocalTime():MM/dd HH:mm:ss}): {report.Stage} / {report.State}\r\n{report.Message} {report.ErrorType} {(report.HResult != 0 ? $"0x{report.HResult:X8}" : "")}";
                if (installation.Error.Length > 0) serviceInfo.Text += "\r\n" + installation.Error;
                RefreshTables(); UpdateServiceControls(installation);
            }
        }
    }

    private void RefreshTables()
    {
        if (ruleDragging) return;
        var c = current;
        logStorageInfo.Text = (connected ? $"ログ保持：{c.Proxy.LogRetentionDays} 日" : "ログ保持：未接続のため現在値未確認（初期値30日）") +
            $" / 設定範囲1～365日\n1ファイル約10MiBで分割 / 総容量の上限なし / 保存先：{Path.Combine(ServiceHost.DataDirectory, "logs")}\n保持日数の変更：サービス停止後、「待受IP・ポート・運用設定」→「ログ保持（日）」";
        Fill(services, ["サービス", "状態", "ホストパターン", "TCPポート", "定義", "注意・出典"], c.Services.Select(s =>
        {
            var preview = string.Join("\n", s.Hosts.Take(3)) + (s.Hosts.Count > 3 ? $"\n… ほか {s.Hosts.Count - 3} 件" : "");
            var notes = (s.Notes + " " + s.Source).Trim();
            return (s.Id, new object[] { s.Name, s.Enabled ? "有効" : "無効", preview, string.Join(", ", s.AllowedPorts), (s.BuiltIn ? "組込候補 " : "ユーザー ") + s.DefinitionVersion, notes.Length > 160 ? notes[..160] + "…" : notes });
        }));
        foreach (DataGridViewRow row in services.Rows)
        {
            row.DefaultCellStyle.BackColor = row.Index % 2 == 0 ? Color.White : Color.FromArgb(248, 250, 253);
            row.DefaultCellStyle.ForeColor = DashboardTheme.Ink;
            row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 240, 253);
            row.DefaultCellStyle.SelectionForeColor = DashboardTheme.Ink;
        }
        if (services.Columns["状態"]!.AutoSizeMode != DataGridViewAutoSizeColumnMode.None)
        {
            services.Columns["状態"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; services.Columns["状態"]!.Width = 100;
            services.Columns["サービス"]!.FillWeight = 130; services.Columns["ホストパターン"]!.FillWeight = 200;
            services.Columns["TCPポート"]!.FillWeight = 65; services.Columns["定義"]!.FillWeight = 85; services.Columns["注意・出典"]!.FillWeight = 180;
        }
        var now = status?.TimeUtc ?? DateTimeOffset.UtcNow;
        var rules = DashboardRules.List(c, now);
        var operational = connected && orderedRules && status is { Healthy: true, Running: true };
        Fill(policies, [], rules.Select(p =>
        {
            var decision = DashboardRules.Evaluate(c, p, now);
            var reason = DashboardRules.Reason(c, p, now);
            return (PairKey(p), new object[] { !operational ? "■ 状態未確認" : decision.Allowed ? "● 開放中" : "■ 遮断中", DeviceName(p.DeviceId, c), ServiceName(p.ServiceId, c), DashboardRules.ScheduleText(p), !orderedRules && connected ? "サービス更新が必要" : !operational ? "サービス停止・未接続・異常" : reason, "編集", "一時許可", "一時遮断", "削除", "Clone", "≡" });
        }));
        foreach (DataGridViewRow row in policies.Rows)
        {
            row.Cells["drag"].ToolTipText = "このつまみを上下にドラッグして順番を変更します。";
            var allowed = Convert.ToString(row.Cells["状態"].Value) == "● 開放中";
            row.DefaultCellStyle.BackColor = !operational ? Color.FromArgb(248, 250, 253) : allowed ? Color.FromArgb(246, 252, 249) : Color.FromArgb(255, 249, 249);
            row.Cells["状態"].Style.ForeColor = !operational ? Color.DimGray : allowed ? Color.FromArgb(22, 105, 57) : Color.FromArgb(170, 42, 42);
            row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 240, 253);
            row.DefaultCellStyle.SelectionForeColor = DashboardTheme.Ink;
        }
        UpdateRuleControls();
    }
    private string Local(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(current.Proxy.TimeZoneId)).ToString("MM/dd HH:mm:ss"); }
        catch { return utc.ToString("u"); }
    }
    private static string DeviceName(string id, Configuration c) => c.Devices.FirstOrDefault(d => d.Id == id) is { } d ? d.Name + " / " + d.Address : id;
    private static string ServiceName(string id, Configuration c) => c.Services.FirstOrDefault(s => s.Id == id)?.Name ?? id;
    private static string PairKey(AccessPolicy p) => p.DeviceId + "/" + p.ServiceId;
    private bool CanEdit()
    {
        if (!connected)
        {
            tabs.SelectedIndex = 3;
            Info("設定を編集するにはサービスの起動が必要です。\n\n" + connectionHelp);
            return false;
        }
        if (!orderedRules)
        {
            tabs.SelectedIndex = 3;
            Info("上から順のルール判定にはサービス本体の更新が必要です。停止後、このEXEから「サービスのプログラムを更新」を実行してください。");
            return false;
        }
        if (!Program.IsAdmin())
        {
            Info("管理GUIを閉じて起動し直し、起動時の管理者確認を許可してください。");
            return false;
        }
        return true;
    }
    private Task EditService(bool edit)
    {
        if (!CanEdit()) return Task.CompletedTask;
        var c = JsonDefaults.Clone(current); var source = edit ? c.Services.FirstOrDefault(s => s.Id == Selected(services)) : new ServiceDefinition();
        if (source is null) return Task.CompletedTask;
        var item = JsonDefaults.Clone(source);
        Editors.Service(this, item, () =>
        {
            var candidate = JsonDefaults.Clone(c);
            candidate.Services.RemoveAll(s => s.Id == item.Id); candidate.Services.Add(JsonDefaults.Clone(item));
            return SaveConfigurationAsync(candidate);
        }, (parent, existing) =>
        {
            IReadOnlyList<string>? selected = null;
            using var picker = LogCandidateDialog.Create(ServiceHost.DataDirectory, c.Proxy.TimeZoneId, c.Devices, existing, hosts => selected = hosts);
            return picker.ShowDialog(parent) == DialogResult.OK ? selected : null;
        });
        return Task.CompletedTask;
    }
    private Task EditPolicy(bool edit)
    {
        if (!CanEdit()) return Task.CompletedTask;
        var c = JsonDefaults.Clone(current);
        var old = edit ? SelectedRule() : null;
        if (edit && old is null) return Task.CompletedTask;
        using var form = RuleEditor.Create(c, old, SaveConfigurationAsync);
        form.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task<bool> SaveConfigurationAsync(Configuration configuration)
    {
        if (!CanEdit()) return false;
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0) { Info(string.Join(Environment.NewLine, errors)); return false; }
        var previous = current;
        var r = await client.SendAsync(new() { Operation = "apply", Configuration = configuration, ExpectedRevision = configuration.Revision }, true);
        if (!r.Success) { Info(r.Message.Replace("再読込してください。", "編集画面を開き直してください。")); return false; }
        if (r.Configuration is not null) current = r.Configuration;
        RefreshTables();
        if (ServiceDiagnostics.Inspect() is { Installed: true, Error.Length: 0 })
        {
            try { if (FirewallManager.ScopeChanged(previous, current)) firewallInfo.Text = await FirewallManager.SyncAsync(current); }
            catch (InvalidOperationException ex)
            {
                firewallInfo.Text = "設定保存済み / Firewall反映失敗：" + ex.Message;
                Info("許可ルール・設定は保存されましたが、Windows Firewallの反映に失敗しました。\nサービスを停止して開始すると再設定します。\n" + ex.Message);
            }
        }
        saveStatus.Text = $"保存しました（{DateTime.Now:HH:mm:ss}）。変更はサービスへ反映されています。";
        await RefreshAsync();
        return true;
    }
    private async Task<bool> SendMutation(IpcRequest request, long? expectedRevision = null)
    {
        if (!CanEdit()) return false;
        request.ExpectedRevision = expectedRevision ?? current.Revision;
        var r = await client.SendAsync(request, true);
        if (!r.Success) Info(r.Message);
        await RefreshAsync();
        return r.Success;
    }
    private Task OpenGrant()
    {
        if (!CanEdit() || SelectedRule() is not { } rule) return Task.CompletedTask;
        var snapshot = JsonDefaults.Clone(current);
        var revision = snapshot.Revision;
        using var form = GrantEditor.Create(snapshot, rule, status?.TimeUtc ?? DateTimeOffset.UtcNow, async request =>
        {
            var saved = await SendMutation(request, revision);
            if (saved) revision++;
            return saved;
        });
        form.ShowDialog(this);
        return Task.CompletedTask;
    }
    private DateTime ToLocalValue(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(current.Proxy.TimeZoneId)).DateTime;
    private static DateTimePicker DateTimeField(DateTime value) => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy/MM/dd HH:mm:ss", Value = value };

    private async Task EditSettings()
    {
        if (!Program.IsAdmin()) { Info("管理GUIを閉じて起動し直し、起動時の管理者確認を許可してください。"); return; }
        if (connected || !ServiceUiState.From(ServiceDiagnostics.Inspect(), false, serviceBusy).CanConfigure)
        { Info("待受・運用設定はサービスとコンソール本体を停止してから変更してください。"); return; }
        var store = new StoppedSettingsStore();
        var original = await store.ReadAsync();
        var p = JsonDefaults.Clone(original.Proxy);
        using var form = new EditorDialog("待受・運用設定", 680);
        var addresses = ListenAddressPicker.CurrentOptions();
        var ip = form.Add("待受先（このPC）", ListenAddressPicker.Create(p.ListenAddress, addresses));
        var listenHint = form.Add("選択した待受先", new Label { AutoSize = true }, 65);
        void UpdateListenHint() => listenHint.Text = ListenAddressPicker.Hint((ip.SelectedItem as Choice)?.Id, addresses);
        ip.SelectedIndexChanged += (_, _) => UpdateListenHint(); UpdateListenHint();
        var port = form.Add("ポート", Number(p.Port, 1, 65535));
        var zone = form.Add("タイムゾーン", Editors.Choices(TimeZoneInfo.GetSystemTimeZones().Select(z => new Choice(z.Id, z.DisplayName)), p.TimeZoneId));
        var maximum = form.Add("一時許可上限（時）", Number(p.MaximumGrantHours, 1, 24));
        var retention = form.Add("ログ保持（日）", Number(p.LogRetentionDays, 1, 365));
        var connections = form.Add("最大同時接続", Number(p.MaxConnections, 1, 4096));
        form.Add("待受IPの意味", new Label { Text = "このPCのIPを選択します。端末側のIPやネットワーク範囲を入力する欄ではありません。0.0.0.0は全IPv4 NICで待受します。接続元の範囲は許可ルールのIPv4/CIDRで指定してください。", AutoSize = true }, 100);
        form.ValidateInput = () =>
        {
            if (ip.SelectedItem is not Choice selected || !IPAddress.TryParse(selected.Id, out var address)) return EditorDialog.Error("一覧からこのPCの待受先を選んでください。");
            try
            {
                var error = ListenAddresses.Validate(selected.Id, ListenAddresses.CurrentIpv4());
                if (error is not null) return EditorDialog.Error(error);
            }
            catch (NetworkInformationException) { return EditorDialog.Error("このPCのIPを取得できません。ネットワーク接続状態を確認してください。"); }
            if (selected.Id != p.ListenAddress && !IPAddress.IsLoopback(address) && !Confirm("LANにプロキシーを公開します。Firewallの送信元範囲を確認済みですか。")) return false;
            if (address.Equals(IPAddress.Any) && !Confirm("0.0.0.0は全NICで待受します。続けますか。")) return false;
            p.ListenAddress = selected.Id; p.Port = (int)port.Value; p.TimeZoneId = ((Choice)zone.SelectedItem!).Id;
            p.MaximumGrantHours = (int)maximum.Value; p.LogRetentionDays = (int)retention.Value; p.MaxConnections = (int)connections.Value; return true;
        };
        form.SaveAsync = async () =>
        {
            if (connected) throw new InvalidOperationException("本体が開始されました。停止してから保存してください。");
            current = await store.SaveAsync(p, original.Revision);
            saveStatus.Text = $"運用設定を保存しました（{DateTime.Now:HH:mm:ss}）。次にサービスを開始すると反映されます。";
            RefreshTables();
            return true;
        };
        form.ShowDialog(this);
    }
    private static NumericUpDown Number(int value, int min, int max) => new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };

    private IEnumerable<LogEntry> FilteredLogs() => logEntries.Where(e =>
        (string.IsNullOrWhiteSpace(logSearch.Text) || $"{e.Peer} {e.Host} {e.RequestPath} {e.Reason} {e.ServiceId} {e.Actor}".Contains(logSearch.Text, StringComparison.OrdinalIgnoreCase)) &&
        (logKind.SelectedIndex == 0 || logKind.SelectedIndex == 1 && e.Allowed && e.Kind == "通信" || logKind.SelectedIndex == 2 && !e.Allowed || logKind.SelectedIndex == 3 && e.Kind == "監査") &&
        (!logFrom.Checked || ToLocalValue(e.TimeUtc).Date >= logFrom.Value.Date) && (!logTo.Checked || ToLocalValue(e.TimeUtc).Date <= logTo.Value.Date));
    private void FilterLogs()
    {
        Fill(logGrid, ["時刻", "種別", "端末IP", "宛先ホスト", "パス", "ポート", "結果", "理由", "サービス", "バイト", "秒", "操作者SID"],
            FilteredLogs().Select((e, i) => (i.ToString(CultureInfo.InvariantCulture), new object[] { Local(e.TimeUtc), e.Kind, e.Peer, e.Host,
                LogPath(e),
                e.Kind == "通信" ? e.Port : "—", e.Kind == "監査" ? "—" : e.Allowed ? "許可" : "拒否", e.Reason, e.ServiceId ?? "",
                e.Kind == "通信" ? e.Bytes : "—", e.Kind == "通信" ? e.Seconds.ToString("F1") : "—", e.Actor })));
        LogTableTheme.ApplyRows(logGrid);
    }
    private static string LogPath(LogEntry entry) => entry.Kind != "通信" ? "—" : entry.IsTunnel ? "取得不可（CONNECT）" :
        string.IsNullOrEmpty(entry.RequestPath) ? "未記録" : entry.RequestPath;
    private Task ExportLogs()
    {
        using var dialog = new SaveFileDialog { Filter = "CSVファイル|*.csv", FileName = "WhiteProxy-logs.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
        static string Csv(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (text.TrimStart().StartsWithAny('=', '+', '-', '@')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "UTC,種別,端末IP,ホスト,パス,ポート,許可,理由,サービス,バイト,秒,操作者SID" };
        lines.AddRange(FilteredLogs().Select(e => string.Join(',', new object?[] { e.TimeUtc.ToString("O"), e.Kind, e.Peer, e.Host, LogPath(e), e.Port, e.Allowed, e.Reason, e.ServiceId, e.Bytes, e.Seconds, e.Actor }.Select(Csv))));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)); return Task.CompletedTask;
    }
    private Task AddDeniedHost()
    {
        if (!CanEdit() || logGrid.CurrentRow is null) return Task.CompletedTask;
        var host = Convert.ToString(logGrid.CurrentRow.Cells["宛先ホスト"].Value);
        var result = Convert.ToString(logGrid.CurrentRow.Cells["結果"].Value);
        if (result != "拒否" || string.IsNullOrWhiteSpace(host)) { Info("宛先ホストがある拒否行を選択してください。"); return Task.CompletedTask; }
        using var form = new EditorDialog("拒否ログのホストを追加", 380);
        var c = JsonDefaults.Clone(current);
        var choice = form.Add("追加するサービス", Editors.Choices(c.Services.Select(s => new Choice(s.Id, s.Name))));
        form.Add("ホスト", new Label { Text = host, AutoSize = true });
        form.Add("管理者の確認", new Label { Text = "ベンダー公式の宛先であることを確認してください。登録したホスト内の他のURLも許可対象になります。プライベートIP宛の制限は解除されません。", AutoSize = true }, 100);
        form.ValidateInput = () => choice.SelectedItem is Choice || EditorDialog.Error("サービスを選択してください。");
        form.SaveAsync = () =>
        {
            var candidate = JsonDefaults.Clone(c);
            var service = candidate.Services.First(x => x.Id == ((Choice)choice.SelectedItem!).Id);
            if (!service.Hosts.Contains(host)) service.Hosts.Add(host);
            return SaveConfigurationAsync(candidate);
        };
        form.ShowDialog(this);
        return Task.CompletedTask;
    }

    private TabPage Page(string text) { var page = new TabPage(text) { Padding = new(12), BackColor = Color.White }; tabs.TabPages.Add(page); return page; }
    private static FlowLayoutPanel Toolbar(int height = 55) => new() { Dock = DockStyle.Top, Height = height, Padding = new(0, 6, 0, 4), AutoScroll = true };
    private static Label Note(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 66, Padding = new(4, 7, 4, 4), ForeColor = Color.FromArgb(70, 82, 101) };
    private Button AddButton(Control parent, string text, Func<Task> action, bool danger = false, string? tone = null)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 36, MinimumSize = new(100, 34), Padding = new(8, 3, 8, 3), FlatStyle = FlatStyle.Flat, BackColor = danger ? Color.FromArgb(175, 45, 45) : Color.White, ForeColor = danger ? Color.White : Color.FromArgb(28, 44, 70), Margin = new(3, 3, 6, 3) };
        button.FlatAppearance.BorderColor = Color.FromArgb(195, 205, 220);
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { await action(); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { Info("操作を完了できませんでした。サービス状態・権限・保存先を確認してください。\n" + ex.Message); }
            finally { if (!button.IsDisposed) { button.Enabled = true; UpdateServiceControls(); UpdateRuleControls(); } }
        };
        if (tone is not null || danger) DashboardTheme.StyleButton(button, tone ?? "danger");
        parent.Controls.Add(button);
        return button;
    }
    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() => DoubleBuffered = true;
    }
    private static DataGridView Grid() => new BufferedGrid()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, AllowUserToResizeRows = false,
        DefaultCellStyle = new() { Padding = new(6), WrapMode = DataGridViewTriState.True },
        ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(232, 238, 246), ForeColor = Color.FromArgb(28, 44, 70), Padding = new(5), Font = new("Yu Gothic UI", 10, FontStyle.Bold) },
        EnableHeadersVisualStyles = false
    };
    private static void Fill(DataGridView grid, string[] columns, IEnumerable<(string Key, object[] Values)> rows)
    {
        if (grid.Columns.Count == 0) foreach (var column in columns) grid.Columns.Add(column, column);
        var next = rows.ToList();
        // 行構成が同じなら選択・スクロール・現在セルを保ったまま値だけを更新する。
        if (grid.Rows.Count == next.Count && grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag as string).SequenceEqual(next.Select(r => r.Key)))
        {
            for (var i = 0; i < next.Count; i++)
                for (var j = 0; j < next[i].Values.Length; j++)
                    if (!Equals(grid.Rows[i].Cells[j].Value, next[i].Values[j]))
                        grid.Rows[i].Cells[j].Value = next[i].Values[j];
            return;
        }
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as string).ToHashSet();
        var first = grid.FirstDisplayedScrollingRowIndex;
        grid.SuspendLayout();
        grid.Rows.Clear();
        foreach (var row in next) { var index = grid.Rows.Add(row.Values); grid.Rows[index].Tag = row.Key; }
        grid.ClearSelection(); foreach (DataGridViewRow row in grid.Rows) if (selected.Contains(row.Tag as string)) row.Selected = true;
        if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
        grid.ResumeLayout();
    }
    private static string? Selected(DataGridView grid) => grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as string;
    private static bool Confirm(string text) => MessageBox.Show(text, "WhiteProxy — 確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    private static void Info(string text) => MessageBox.Show(text, "WhiteProxy", MessageBoxButtons.OK, MessageBoxIcon.Information);
}

internal static class StringExtensions
{
    public static bool StartsWithAny(this string value, params char[] chars) => value.Length > 0 && chars.Contains(value[0]);
}
