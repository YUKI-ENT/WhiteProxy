namespace WhiteProxy.Admin;

internal sealed class ServiceUpdateDialog : Form
{
    private bool updating = true;
    internal string Notice { get; private set; } = "";
    internal Exception? Failure { get; private set; }

    internal ServiceUpdateDialog(ServiceUpdatePlan plan, Func<Task<string>> update)
    {
        Text = "WhiteProxy — サービス自動更新";
        ClientSize = new(550, 145);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        Font = new("Yu Gothic UI", 10);
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Padding = new(20),
            Text = $"サービスを {plan.PreviousVersion} → {plan.NewVersion} に更新しています…\n\n" +
                (plan.Restart ? "接続中の通信を切断し、更新後にサービスを再開します。" : "サービスは更新後も停止状態を維持します。")
        });
        Shown += async (_, _) =>
        {
            try { Notice = await update(); }
            catch (Exception ex) { Failure = ex; }
            finally { updating = false; Close(); }
        };
        FormClosing += (_, e) => { if (updating) e.Cancel = true; };
    }
}
