using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal static class ServiceImportEditor
{
    internal static EditorDialog Create(string name, ServiceImportPlan plan, Func<Configuration, Task<bool>> save)
    {
        var form = new EditorDialog("許可サービスをインポート", 760, "追加する");
        form.Add("サービスセット", new Label { AutoSize = true, Text = name }, 50);
        form.Add("追加内容", new Label { AutoSize = true, Text = $"追加 {plan.Added.Count} 件 / 同じ定義をスキップ {plan.Skipped.Count} 件\n同名で内容が異なるサービスは、別名で追加します。既存定義は上書きしません。" }, 65);
        var list = form.Add("追加するサービス", new ListBox { Name = "ImportServices" }, 145);
        foreach (var service in plan.Added) list.Items.Add(new Choice(service.Id, service.Name));
        var details = form.Add("接続先・ポート", new TextBox { Name = "ImportDetails", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }, 225);
        void ShowDetails()
        {
            var service = plan.Added.FirstOrDefault(s => s.Id == (list.SelectedItem as Choice)?.Id);
            details.Text = service is null ? "" : $"{service.Name}\r\nTCP: {string.Join(", ", service.AllowedPorts)}\r\n\r\n{string.Join("\r\n", service.Hosts)}\r\n\r\n出典: {service.Source}\r\n{service.Notes}";
        }
        list.SelectedIndexChanged += (_, _) => ShowDetails();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        form.Add("反映する内容", new Label { AutoSize = true, Text = "サービス定義だけを追加します。端末・許可ルール・スケジュールは変更しません。追加したサービスを許可ルールで選ぶまで、そのサービスの通信許可は作成されません。" }, 85);
        form.ValidateInput = () => !plan.Added.Any(s => s.AllowedPorts.Any(p => p is not 80 and not 443)) ||
            MessageBox.Show(form, "80/443以外のTCPポートも含まれます。このサービスをルールで許可すると、そのポートへのトンネル接続も可能になります。追加しますか。", "許可ポートの確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        form.SaveAsync = () => save(plan.Candidate);
        return form;
    }
}
