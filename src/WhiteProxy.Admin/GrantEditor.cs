using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

internal static class GrantEditor
{
    internal static EditorDialog Create(Configuration config, AccessPolicy rule, DateTimeOffset now, Func<IpcRequest, Task<bool>> send, Func<string, bool>? confirm = null)
    {
        var form = new EditorDialog("一時許可 — 新規・延長・終了", 740, "実行する");
        confirm ??= text => MessageBox.Show(form, text, "一時許可", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Proxy.TimeZoneId);
        DateTime Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).DateTime;
        DateTimePicker Field(DateTime value) => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy/MM/dd HH:mm:ss", Value = value };
        var device = config.Devices.First(d => d.Id == rule.DeviceId);
        var service = config.Services.First(s => s.Id == rule.ServiceId);
        form.Add("対象端末", new Label { Text = device.Name + " / " + device.Address, AutoSize = true }, 55);
        form.Add("許可サービス", new Label { Text = service.Name, AutoSize = true });
        var grants = config.TemporaryGrants.Where(g => g.DeviceId == rule.DeviceId && g.ServiceId == rule.ServiceId && !g.Revoked && g.ExpiresAtUtc > now).ToList();
        var existing = grants.OrderByDescending(g => g.ExpiresAtUtc).FirstOrDefault();
        var canGrant = !rule.Deny && !config.Proxy.GlobalBlock && device.Enabled && !device.Paused && service.Enabled && service.Hosts.Count > 0;
        var choices = new List<Choice>();
        if (canGrant) choices.Add(new("grant", existing is null ? "新規許可" : "変更・延長"));
        if (existing is not null) choices.Add(new("end", "終了する"));
        var action = form.Add("操作", new ToggleChoices(choices), 52); action.Name = "GrantAction";
        form.Add("現在の一時許可", new Label { AutoSize = true, Text = existing is null ? "なし" : $"{Local(existing.StartsAtUtc):MM/dd HH:mm} ～ {Local(existing.ExpiresAtUtc):MM/dd HH:mm}" + (!canGrant ? "（遮断が優先）" : "") }, 55);
        var reserved = form.Add("開始方法", new CheckBox { Name = "GrantReserved", Text = "開始を予約する", Checked = existing?.StartsAtUtc > now });
        var start = form.Add("開始", Field(Local(reserved.Checked ? existing!.StartsAtUtc : now))); start.Name = "GrantStart";
        var end = form.Add("終了", Field(Local(existing?.ExpiresAtUtc ?? now.AddHours(config.Proxy.MaximumGrantHours)))); end.Name = "GrantEnd";
        var durations = form.Add("許可する時間", new FlowLayoutPanel(), 55);
        foreach (var minutes in new[] { 30, 60, 120, 240 }.Where(m => m <= config.Proxy.MaximumGrantHours * 60))
        {
            var button = new Button { Text = minutes < 60 ? "30分" : (minutes / 60) + "時間", AutoSize = true };
            button.Click += (_, _) => end.Value = (reserved.Checked ? start.Value : Local(DateTimeOffset.UtcNow)).AddMinutes(minutes);
            durations.Controls.Add(button);
        }
        void UpdateInputs()
        {
            var grant = action.SelectedId == "grant";
            reserved.Enabled = end.Enabled = durations.Enabled = grant; start.Enabled = grant && reserved.Checked;
        }
        reserved.CheckedChanged += (_, _) => UpdateInputs(); action.SelectionChanged += (_, _) => UpdateInputs(); UpdateInputs();
        form.Add("動作", new Label { AutoSize = true, Text = $"今から最大 {config.Proxy.MaximumGrantHours} 時間。期限後は通常の許可時間へ戻ります。\n上位の一致ルールがある場合は、そちらの判定が優先されます。終了操作だけでは通常の予定による許可は取り消しません。" }, 100);
        IpcRequest? request = null;
        form.ValidateInput = () =>
        {
            if (action.SelectedId == "end") return true;
            if (action.SelectedId != "grant") return EditorDialog.Error("実行できる操作がありません。");
            var a = DateTime.SpecifyKind(start.Value, DateTimeKind.Unspecified); var b = DateTime.SpecifyKind(end.Value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(a) || zone.IsInvalidTime(b) || zone.IsAmbiguousTime(a) || zone.IsAmbiguousTime(b)) return EditorDialog.Error("夏時間境界の曖昧な日時は指定できません。");
            request = new() { Operation = "grant", DeviceIds = [rule.DeviceId], ServiceId = rule.ServiceId,
                StartsAtUtc = reserved.Checked ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(a, zone)) : null, ExpiresAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(b, zone)) };
            return true;
        };
        form.SaveAsync = async () =>
        {
            if (action.SelectedId != "end") return request is not null && await send(request);
            if (!confirm("このルールの一時許可・予約を終了します。通常の許可時間内なら通信は継続します。終了しますか。")) return false;
            foreach (var grant in grants) if (!await send(new() { Operation = "revoke", GrantId = grant.Id })) return false;
            return true;
        };
        return form;
    }
}
