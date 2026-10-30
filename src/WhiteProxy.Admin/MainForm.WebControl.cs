using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

public sealed partial class MainForm
{
    private readonly CheckBox webEnabled = new() { Text = "Webコントロールを有効", AutoSize = true, Margin = new(3, 10, 14, 0) };
    private readonly NumericUpDown webPort = new() { Minimum = 1, Maximum = 65535, Value = 3129, Width = 85, Margin = new(3, 7, 12, 3) };
    private readonly TextBox webPassword = new() { UseSystemPasswordChar = true, Width = 340, MaxLength = 256, PlaceholderText = "初回は5文字以上。空欄なら現在のパスワードを維持" };
    private readonly TextBox webInfo = new() { ReadOnly = true, Multiline = true, Width = 900, Height = 88, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(246, 248, 252) };
    private WebControlStatus? webStatus;
    private bool webDirty, webLoading, webSaving;
    private long webEditRevision;

    private void BuildWebControl(Control parent)
    {
        var group = new GroupBox { Text = "Webコントロール（HTTPS）", Width = 950, Height = 320, Padding = new(14, 24, 14, 12) };
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var row = new FlowLayoutPanel { Width = 910, Height = 48, WrapContents = false };
        row.Controls.Add(webEnabled);
        row.Controls.Add(new Label { Text = "ポート", AutoSize = true, Padding = new(0, 10, 0, 0) }); row.Controls.Add(webPort);
        AddButton(row, "Web設定を保存", SaveWebControlAsync, tone: "edit");
        AddButton(row, "管理Webを開く", () =>
        {
            if (webStatus is not { Enabled: true, Running: true }) { Info("Webコントロールを有効にして、サービスを開始してください。"); return Task.CompletedTask; }
            Process.Start(new ProcessStartInfo($"https://localhost:{webStatus.Port}/") { UseShellExecute = true });
            return Task.CompletedTask;
        }, tone: "export");
        var passwordRow = new FlowLayoutPanel { Width = 910, Height = 48, WrapContents = false };
        passwordRow.Controls.Add(new Label { Text = "管理パスワード", AutoSize = true, Padding = new(0, 6, 8, 0) }); passwordRow.Controls.Add(webPassword);
        AddButton(passwordRow, "公開証明書を書き出す", () =>
        {
            var path = Path.Combine(ServiceHost.DataDirectory, "web-control.pfx");
            if (!File.Exists(path)) { Info("Web管理を有効にしてサービスを開始すると、証明書が生成されます。"); return Task.CompletedTask; }
            using var file = new SaveFileDialog { Filter = "公開証明書|*.cer", FileName = "WhiteProxy-Web.cer" };
            if (file.ShowDialog(this) == DialogResult.OK)
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.EphemeralKeySet);
                File.WriteAllBytes(file.FileName, certificate.Export(X509ContentType.Cert));
                saveStatus.Text = "公開証明書を書き出しました。クライアントPCの「ローカルコンピューター」→「信頼されたルート証明機関」へインポートしてください。";
                Info("公開証明書を書き出しました。\n\nブラウザーを使うクライアントPCで、次の手順でインポートしてください。\n\n" +
                    "1. CERファイルを開き、「証明書のインストール」を選択\n" +
                    "2. 保存場所は「ローカルコンピューター」を選択（管理者権限が必要）\n" +
                    "3. 「証明書をすべて次のストアに配置する」を選び、\n   「信頼されたルート証明機関」を指定\n" +
                    "4. 完了後、ブラウザーをすべて終了して起動し直す\n\n" +
                    "インポート前に、設定画面のSHA-256指紋と照合してください。");
            }
            return Task.CompletedTask;
        });
        body.Controls.Add(row); body.Controls.Add(passwordRow);
        body.Controls.Add(new Label { Text = "パスワードは5～256文字。変更時は全Webセッションをログアウトします。\n有効時はローカルサブネットの受信許可を自動設定（Domain/Privateのみ）。自己署名証明書を自動生成します。", AutoSize = true, MaximumSize = new(900, 0), ForeColor = Color.FromArgb(70, 82, 101), Padding = new(0, 2, 0, 8) });
        body.Controls.Add(webInfo); group.Controls.Add(body); parent.Controls.Add(group);
        webEnabled.CheckedChanged += (_, _) => { if (!webLoading) webDirty = true; };
        webPort.ValueChanged += (_, _) => { if (!webLoading) webDirty = true; };
        webPassword.TextChanged += (_, _) => { if (!webLoading) webDirty = true; };
    }

    private void UpdateWebControl(WebControlStatus? state)
    {
        if (webSaving) return;
        try
        {
            if (state is null && !connected) state = new WebControlStore(ServiceHost.DataDirectory).ReadStatus();
            webStatus = state;
            if (state is null) { webInfo.Text = "Web管理対応のサービスへ更新してください。"; return; }
            if (!webDirty)
            {
                webLoading = true; webEnabled.Checked = state.Enabled; webPort.Value = state.Port; webLoading = false;
                webEditRevision = state.Revision;
            }
            webInfo.Text = (!state.Enabled ? "無効" : state.Running ? "HTTPS待受中" : "有効（サービス停止中、またはWeb待受未開始）") +
                $" / パスワード：{(state.PasswordConfigured ? "設定済み" : "未設定")} / ポート：{state.Port}\r\n" +
                (state.Error.Length == 0 ? string.Join("  ", state.Urls ?? [$"https://localhost:{state.Port}/"]) : state.Error) +
                (state.CertificateFingerprint.Length == 0 ? "" : "\r\n証明書 SHA-256：" + state.CertificateFingerprint);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException or UnauthorizedAccessException)
        { webInfo.Text = "Web設定を読み取れません。設定ファイルとアクセス権を確認してください。"; }
        finally { webLoading = false; }
    }

    private async Task SaveWebControlAsync()
    {
        if (webSaving || serviceBusy) return;
        if (connected && webStatus is null) { Info("Web管理対応のサービスへ更新してください。停止後、「サービスのプログラムを更新」を実行してください。"); return; }
        var change = new WebControlChange { Enabled = webEnabled.Checked, Port = (int)webPort.Value, Password = webPassword.Text, ExpectedRevision = webEditRevision };
        if (change.Port == current.Proxy.Port) { Info("Webポートはプロキシーポートと別にしてください。"); return; }
        webSaving = true;
        try
        {
            WebControlStatus saved;
            if (connected)
            {
                var response = await client.SendAsync(new() { Operation = "web-settings", WebControl = change }, true);
                if (!response.Success || response.WebControl is null) { Info(response.Message); return; }
                saved = response.WebControl;
            }
            else
                saved = await Task.Run(() => new WebControlStore(ServiceHost.DataDirectory).SaveStopped(change, current.Proxy.Port));
            webLoading = true; webPassword.Clear(); webLoading = false; webDirty = false;
            webStatus = saved;
            webEditRevision = saved.Revision;
            try { saveStatus.Text = await FirewallManager.SyncWebAsync(saved.Enabled, saved.Port); }
            catch (InvalidOperationException ex) { Info("Web設定は保存済みですが、Firewall反映に失敗しました。\n" + ex.Message); }
            webSaving = false; UpdateWebControl(saved);
        }
        finally { webSaving = false; }
    }
}
