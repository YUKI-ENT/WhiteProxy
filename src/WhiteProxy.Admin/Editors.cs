using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal sealed class EditorDialog : Form
{
    private readonly TableLayoutPanel table = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18), AutoScroll = true };
    private int row;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? ValidateInput { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Task<bool>>? SaveAsync { get; set; }
    private bool saving;
    public EditorDialog(string title, int height = 500, string confirmText = "保存")
    {
        Icon = AppIcons.Application;
        Text = title; Size = new(680, height); MinimumSize = new(600, 400);
        StartPosition = FormStartPosition.CenterParent; Font = new("Yu Gothic UI", 10);
        table.ColumnStyles.Add(new(SizeType.Absolute, 150)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new(10) };
        var ok = new Button { Text = confirmText, AutoSize = true, Height = 34 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 34 };
        ok.Click += async (_, _) =>
        {
            if (saving || ValidateInput?.Invoke() == false) return;
            saving = true; ok.Enabled = cancel.Enabled = false; ok.Text = "保存中…";
            try
            {
                if (SaveAsync is not null && !await SaveAsync()) return;
                saving = false; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            { Error("保存できませんでした。入力内容はこの画面に残っています。\n" + ex.Message); }
            finally
            {
                saving = false;
                if (!IsDisposed) { ok.Enabled = cancel.Enabled = true; ok.Text = confirmText; }
            }
        };
        FormClosing += (_, e) => { if (saving) e.Cancel = true; };
        buttons.Controls.AddRange([ok, cancel]); AcceptButton = ok; CancelButton = cancel;
        Controls.Add(table); Controls.Add(buttons);
    }
    public T Add<T>(string label, T control, int height = 40) where T : Control
    {
        table.RowStyles.Add(new(SizeType.Absolute, height));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new(145, 0) }, 0, row);
        control.Dock = DockStyle.Fill; control.Margin = new(4, 5, 4, 5); table.Controls.Add(control, 1, row++);
        return control;
    }
    public TextBox TextField(string label, string text, bool multiline = false, int height = 40)
        => Add(label, new TextBox { Text = text, Multiline = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None }, height);
    internal void SetRowVisible(Control control, bool visible, int height)
    {
        var index = table.GetRow(control);
        control.Visible = visible;
        table.GetControlFromPosition(0, index)!.Visible = visible;
        table.RowStyles[index].Height = visible ? height : 0;
    }
    public CheckBox Check(string label, bool value) => Add(label, new CheckBox { Checked = value, Text = "有効" });
    public static bool Error(string message) { MessageBox.Show(message, "入力を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
}

internal sealed record Choice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class Editors
{
    public static bool Device(IWin32Window parent, Device device, Func<Task<bool>>? save = null)
    {
        using var form = new EditorDialog("端末管理 — IPv4 / CIDR", 450);
        form.SaveAsync = save;
        var name = form.TextField("表示名", device.Name);
        var address = form.TextField("IPv4 / CIDR", device.Address);
        var group = form.TextField("グループ", device.Group);
        var enabled = form.Check("端末を有効", device.Enabled);
        var paused = form.Check("一時停止", device.Paused);
        form.Add("注意", new Label { Text = "IPv6は未対応。端末は自動許可されません。CIDRの全端末が対象です。", AutoSize = true }, 65);
        form.ValidateInput = () =>
        {
            try { _ = Ipv4Network.Parse(address.Text); } catch { return EditorDialog.Error("IPv4/CIDRを確認してください。"); }
            if (string.IsNullOrWhiteSpace(name.Text)) return EditorDialog.Error("表示名を入力してください。");
            if (address.Text != device.Address && address.Text.Contains('/') && !address.Text.EndsWith("/32") &&
                MessageBox.Show("CIDR範囲に属する複数の端末が対象になります。この範囲を登録しますか。", "範囲の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            device.Name = name.Text; device.Address = address.Text.Trim(); device.Group = group.Text;
            device.Enabled = enabled.Checked; device.Paused = paused.Checked; return true;
        };
        return form.ShowDialog(parent) == DialogResult.OK;
    }

    public static bool Service(IWin32Window parent, ServiceDefinition service, Func<Task<bool>>? save = null,
        Func<IWin32Window, IReadOnlyList<string>, IReadOnlyList<string>?>? chooseHosts = null)
    {
        using var form = CreateServiceDialog(service, save, chooseHosts);
        return form.ShowDialog(parent) == DialogResult.OK;
    }

    internal static EditorDialog CreateServiceDialog(ServiceDefinition service, Func<Task<bool>>? save = null,
        Func<IWin32Window, IReadOnlyList<string>, IReadOnlyList<string>?>? chooseHosts = null)
    {
        var form = new EditorDialog("許可サービス — ホスト名とTCPポート", 800);
        form.SaveAsync = save;
        var name = form.TextField("サービス名", service.Name);
        var hosts = form.TextField("ホスト（1行1件）", string.Join(Environment.NewLine, service.Hosts), true, 165);
        hosts.Name = "ServiceHosts";
        var candidateTools = form.Add("接続先の追加", new FlowLayoutPanel(), 48);
        var extract = new Button { Name = "ExtractLogHosts", Text = "ログから候補抽出", AutoSize = true, MinimumSize = new(150, 32), Enabled = chooseHosts is not null };
        candidateTools.Controls.Add(extract);
        extract.Click += (_, _) =>
        {
            var selected = chooseHosts?.Invoke(form, hosts.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList());
            if (selected is not null) hosts.Lines = LogCandidateSearch.Merge(hosts.Lines, selected).ToArray();
        };
        var ports = form.TextField("許可TCPポート", string.Join(",", service.AllowedPorts));
        var enabled = form.Check("サービスを有効", service.Enabled);
        var source = form.TextField("公式出典URL", service.Source);
        var notes = form.TextField("注意・確認内容", service.Notes, true, 85);
        form.Add("パターンの意味", new Label { Text = "example.com は完全一致。*.example.com はサブドメインのみ（親ホストを含みません）。IPは完全一致のみ。空欄はすべて拒否。テンプレートは初期候補です。", AutoSize = true }, 100);
        form.ValidateInput = () =>
        {
            var patterns = hosts.Lines.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct().ToList();
            List<int> numbers;
            try
            {
                foreach (var h in patterns) _ = HostRules.Normalize(h.StartsWith("*.") ? h[2..] : h);
                numbers = ports.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).Distinct().ToList();
                if (numbers.Count == 0 || numbers.Any(p => p is < 1 or > 65535) || numbers.Count > 128) throw new ArgumentException();
            }
            catch { return EditorDialog.Error("ホスト名・ポートを確認してください。"); }
            if (numbers.Any(p => p is not 80 and not 443) && MessageBox.Show("80/443以外のトンネル接続も可能になります。追加ポートを許可しますか。", "ポート追加の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            service.Name = name.Text; service.Hosts = patterns; service.AllowedPorts = numbers;
            service.Enabled = enabled.Checked; service.Source = source.Text; service.Notes = notes.Text; return true;
        };
        return form;
    }

    internal static ComboBox Choices(IEnumerable<Choice> choices, string? selected = null)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(choices.Cast<object>().ToArray());
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        foreach (Choice c in combo.Items) if (c.Id == selected) combo.SelectedItem = c;
        return combo;
    }

    internal static RecurringSchedule? Schedule(IWin32Window parent, RecurringSchedule? existing)
    {
        RecurringSchedule? result = null;
        using var form = ScheduleEditor.Create(existing, value => result = value);
        return form.ShowDialog(parent) == DialogResult.OK ? result : null;
    }
    public static string DayName(DayOfWeek day) => "日月火水木金土"[(int)day].ToString();
}
