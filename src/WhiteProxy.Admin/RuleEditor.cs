using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal static class RuleEditor
{
    // 候補をまとめて保存する。端末だけ先に登録する手順や、キャンセル時の部分保存は作らない。
    internal static EditorDialog Create(Configuration original, AccessPolicy? existing, Func<Configuration, Task<bool>> save, Func<string, bool>? confirm = null)
    {
        var config = JsonDefaults.Clone(original);
        var schedules = existing?.Schedules.Select(JsonDefaults.Clone).ToList() ?? [];
        var form = new EditorDialog(existing is null ? "許可ルールを追加" : "許可ルールを編集", 830);
        confirm ??= text => MessageBox.Show(form, text, "許可ルールの確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        var choices = new[] { new Choice("", "＋ IP / CIDRを直接指定して追加") }
            .Concat(config.Devices.Select(d => new Choice(d.Id, d.Name + " / " + d.Address)));
        var target = form.Add("対象端末", Editors.Choices(choices, existing?.DeviceId)); target.Name = "RuleTarget";
        target.Enabled = existing is null;
        var name = form.TextField("端末の表示名", ""); name.Name = "RuleDeviceName";
        var address = form.TextField("IPv4 / CIDR", ""); address.Name = "RuleAddress";
        address.PlaceholderText = "例：192.168.10.21 または 192.168.10.0/24";
        var targetHint = form.Add("対象の説明", new Label { AutoSize = true }, 48);
        void LoadTarget()
        {
            var d = config.Devices.FirstOrDefault(d => d.Id == (target.SelectedItem as Choice)?.Id);
            name.Text = d?.Name ?? ""; address.Text = d?.Address ?? "";
            var count = d is null ? 0 : config.Policies.Count(p => p.DeviceId == d.Id);
            targetHint.Text = d is null ? "このルールの対象IP/CIDRを指定します。範囲の重複は可能です。" :
                count > 1 ? $"対象情報は {count} 件のルールで共有しています。IPの変更はそれらにも反映します。" : "このルールの対象IP/CIDRです。範囲が重なる場合は上のルールを優先します。";
            if (d is not null && (!d.Enabled || d.Paused)) targetHint.Text += "\nこの対象は停止中です（通信を遮断）。";
        }
        target.SelectedIndexChanged += (_, _) => LoadTarget(); LoadTarget();
        var service = form.Add("許可サービス", Editors.Choices(config.Services.Select(s => new Choice(s.Id, s.Name)), existing?.ServiceId));
        service.Name = "RuleService";
        if (existing is null) service.SelectedIndex = -1;
        var serviceInfo = form.Add("許可先の確認", new Label { AutoSize = true }, 55);
        void LoadService()
        {
            var s = config.Services.FirstOrDefault(s => s.Id == (service.SelectedItem as Choice)?.Id);
            serviceInfo.Text = s is null ? "許可するサービスを選択してください。接続先は「許可サービス」で編集できます。" :
                $"{(s.Enabled ? "有効" : "無効")} / ホスト {s.Hosts.Count} 件 / TCP {string.Join(", ", s.AllowedPorts)}" + (s.Hosts.Count == 0 ? "\n許可先未設定のため通信は遮断されます。" : "");
        }
        service.SelectedIndexChanged += (_, _) => LoadService(); LoadService();
        var mode = form.Add("通常の許可時間", Editors.Choices([
            new("temporary", "一時許可のみ（通常は遮断）"),
            new("always", "全日・24時間（常時許可）"),
            new("schedule", "曜日・時間帯を指定")
        ], existing?.AlwaysAllow == true ? "always" : schedules.Count > 0 ? "schedule" : "temporary"));
        mode.Name = "RuleMode";
        var list = form.Add("曜日・時間帯", new ListBox { Name = "RuleSchedules" }, 110);
        var tools = form.Add("時間帯の操作", new FlowLayoutPanel(), 48);
        var preview = form.Add("次回の予定", new Label { AutoSize = true }, 70);
        void RefreshSchedule()
        {
            list.Items.Clear();
            foreach (var s in schedules) list.Items.Add($"{(s.Enabled ? "" : "無効 ")}{string.Join('・', s.Days.Select(Editors.DayName))} {s.Start:HH:mm}～{s.End:HH:mm}{(s.End < s.Start ? "（翌日）" : "")}");
            var scheduled = (mode.SelectedItem as Choice)?.Id == "schedule";
            list.Enabled = tools.Enabled = scheduled;
            form.SetRowVisible(list, scheduled, 110); form.SetRowVisible(tools, scheduled, 48);
            form.Height = Math.Min(scheduled ? 890 : 730, Screen.FromControl(form).WorkingArea.Height - 40);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Proxy.TimeZoneId);
            var now = DateTimeOffset.UtcNow;
            preview.Text = !scheduled ? (mode.SelectedItem as Choice)?.Id == "always" ? "毎日24時間許可します。" : "一時許可ボタンで期限を指定した間だけ許可します。" : !schedules.Any(s => s.Enabled) ? "「時間帯を追加」で許可する曜日と時間を指定してください。" :
                string.Join("\n", schedules.Where(s => s.Enabled).SelectMany(s => PolicyEngine.Intervals(s, zone, now)).Where(i => i.End > now).OrderBy(i => i.Start).Take(2)
                    .Select(i => $"{TimeZoneInfo.ConvertTime(i.Start, zone):MM/dd HH:mm} ～ {TimeZoneInfo.ConvertTime(i.End, zone):MM/dd HH:mm}"));
        }
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => { action(); RefreshSchedule(); }; tools.Controls.Add(button);
        }
        Button("時間帯を追加", () => { if (Editors.Schedule(form, null) is { } s) schedules.Add(s); });
        Button("編集", () => { var i = list.SelectedIndex; if (i >= 0 && Editors.Schedule(form, schedules[i]) is { } s) schedules[i] = s; });
        Button("削除", () => { if (list.SelectedIndex >= 0) schedules.RemoveAt(list.SelectedIndex); });
        mode.SelectedIndexChanged += (_, _) => RefreshSchedule(); RefreshSchedule();
        form.Add("保存後の動作", new Label { AutoSize = true, Text = "許可のない端末・サービス・時間帯は拒否します。終了時刻で再評価し、ほかに有効な許可がなければ既存接続も切断します。\n時間外は次の行へ進みます。明示的な一時遮断はその行で遮断します。時刻帯：" + config.Proxy.TimeZoneId +
            (existing?.Deny == true ? "\n現在は手動遮断中です。保存しても解除しません。ダッシュボードで解除できます。" : "") }, 90);

        Configuration? candidate = null;
        form.ValidateInput = () =>
        {
            if (service.SelectedItem is not Choice selectedService || target.SelectedItem is not Choice selectedTarget)
                return EditorDialog.Error("対象端末と許可サービスを指定してください。");
            var selectedMode = (mode.SelectedItem as Choice)?.Id;
            if (selectedMode == "schedule" && !schedules.Any(s => s.Enabled)) return EditorDialog.Error("有効な曜日・時間帯を追加してください。");
            candidate = JsonDefaults.Clone(config);
            Device? device = candidate.Devices.FirstOrDefault(d => d.Id == selectedTarget.Id);
            if (string.IsNullOrWhiteSpace(name.Text)) return EditorDialog.Error("端末の表示名を入力してください。");
            Ipv4Network network;
            try { network = Ipv4Network.Parse(address.Text.Trim()); } catch (ArgumentException) { return EditorDialog.Error("対象のIPv4 / CIDRを入力してください。"); }
            if ((device is null || device.Address != address.Text.Trim()) && network.Prefix < 32 && MessageBox.Show(form, $"{address.Text.Trim()} の範囲に属する複数端末が対象になります。この範囲で保存しますか。", "CIDR範囲の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            if (device is null)
            {
                device = new() { Name = name.Text.Trim(), Address = address.Text.Trim() }; candidate.Devices.Add(device);
            }
            else { device.Name = name.Text.Trim(); device.Address = address.Text.Trim(); }
            var changeError = ChangeError(candidate, existing, device.Id, selectedService.Id, DateTimeOffset.UtcNow);
            if (changeError is not null) return EditorDialog.Error(changeError);
            var rule = new AccessPolicy { DeviceId = device.Id, ServiceId = selectedService.Id, Deny = existing?.Deny ?? false,
                AlwaysAllow = selectedMode == "always", Schedules = selectedMode == "schedule" ? schedules.Select(JsonDefaults.Clone).ToList() : [] };
            var index = candidate.Policies.FindIndex(p => existing is not null && DashboardRules.Key(p) == DashboardRules.Key(existing));
            if (index >= 0) candidate.Policies[index] = rule;
            else candidate.Policies.Add(rule);
            var errors = ConfigurationValidator.Validate(candidate);
            if (errors.Count > 0) return EditorDialog.Error(string.Join("\n", errors));
            if (existing is not null && existing.ServiceId != selectedService.Id && !confirm("許可サービスを変更します。ルールの順序・許可時間・手動遮断設定を引き継ぎ、保存後は変更先の接続先で判定します。変更しますか。")) return false;
            if (rule.AlwaysAllow && existing?.AlwaysAllow != true && MessageBox.Show(form, "全日・24時間、登録済みの許可先へ通信できるようになります。このルールを常時許可として保存しますか。", "常時許可の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            return true;
        };
        form.SaveAsync = () => candidate is not null ? save(candidate) : Task.FromResult(false);
        return form;
    }

    internal static string? ChangeError(Configuration config, AccessPolicy? existing, string deviceId, string serviceId, DateTimeOffset now)
    {
        if (config.Policies.Any(p => p.DeviceId == deviceId && p.ServiceId == serviceId && (existing is null || DashboardRules.Key(p) != DashboardRules.Key(existing))))
            return "同じ端末・サービスのルールが登録済みです。既存ルールを編集してください。";
        if (existing is not null && existing.ServiceId != serviceId && config.TemporaryGrants.Any(g => g.DeviceId == existing.DeviceId && g.ServiceId == existing.ServiceId && !g.Revoked && g.ExpiresAtUtc > now))
            return "旧サービスに一時許可・開始予約が残っています。ダッシュボードの「一時許可」で終了してからサービスを変更してください。";
        return null;
    }
}
