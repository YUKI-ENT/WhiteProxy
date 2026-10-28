using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal static class BlockEditor
{
    internal static EditorDialog Create(Configuration config, AccessPolicy rule, Func<Configuration, Task<bool>> save, Func<string, bool>? confirm = null)
    {
        var candidate = JsonDefaults.Clone(config);
        var form = new EditorDialog("一時遮断・解除", 480, "反映する");
        confirm ??= text => MessageBox.Show(form, text, "一時遮断", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        var device = config.Devices.First(d => d.Id == rule.DeviceId);
        var service = config.Services.First(s => s.Id == rule.ServiceId);
        form.Add("対象", new Label { AutoSize = true, Text = device.Name + " / " + device.Address + "\n" + service.Name }, 65);
        form.Add("現在の設定", new Label { AutoSize = true, Text = rule.Deny ? "手動遮断中（解除まで継続）" : "通常の許可時間に従う" });
        var action = form.Add("操作", new ToggleChoices([new("block", "遮断する"), new("normal", "遮断を解除")], rule.Deny ? "block" : "normal"), 52);
        action.Name = "BlockAction";
        form.Add("動作", new Label { AutoSize = true, Text = "この端末・サービスに一致した通信を、この行で明示的に遮断します。下の許可へは進みません。\n解除すると、元のスケジュールと有効期限内の一時許可へ戻ります。時間外は下のルールも確認し、有効な許可がなければ遮断します。" }, 95);
        form.SaveAsync = async () =>
        {
            var blocked = action.SelectedId == "block";
            if (rule.Deny && !blocked && !confirm("遮断を解除します。現在の予定または一時許可によって通信が開通する場合があります。続けますか。")) return false;
            var policy = candidate.Policies.FirstOrDefault(p => DashboardRules.Key(p) == DashboardRules.Key(rule));
            if (policy is null) { policy = JsonDefaults.Clone(rule); candidate.Policies.Add(policy); }
            policy.Deny = blocked;
            return await save(candidate);
        };
        return form;
    }
}
