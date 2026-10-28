using WhiteProxy.Core;

namespace WhiteProxy.Service;

public static class Templates
{
    public static Configuration CreateDefault() => new()
    {
        Services =
        [
            Template("google-search", "Google検索", ["www.google.com", "www.google.co.jp"], "https://support.google.com/websearch/", "ホスト内の検索以外のURLも区別できません。関連認証・画像等は個別確認してください。"),
            Template("chatgpt", "ChatGPT", ["chatgpt.com", "*.chatgpt.com"], "https://help.openai.com/en/articles/9247338", "公式ネットワーク推奨を参照。認証・アップロードなど全機能は保証しません。"),
            Template("claude", "Claude", ["claude.ai"], "https://support.claude.com/", "最小の候補。認証・関連CDNは管理者が公式資料と拒否ログで確認してください。"),
            Template("google-translate", "Google翻訳", ["translate.google.com"], "https://support.google.com/translate/", "Google共通ホストの追加許可は他機能にも影響します。"),
            Template("windows-update", "Windows Update", [], "https://learn.microsoft.com/en-us/windows/deployment/do/delivery-optimization-proxy", "未設定・必ず拒否。実環境のMicrosoft公開資料を確認して許可先を追加してください。更新完了検出ではなく指定時間枠のみ開放。Defenderと重複する場合があります。"),
            Template("defender", "Microsoft Defender", [], "https://learn.microsoft.com/en-us/defender-endpoint/configure-network-connections-microsoft-defender-antivirus", "未設定・必ず拒否。更新方式に合わせて管理者が許可先を登録してください。"),
            new() { Id = "medical-update", Name = "電子カルテ・業務アプリ更新", Notes = "ベンダー公式の更新ホストを登録してください。空リストは拒否です。" }
        ]
    };
    private static ServiceDefinition Template(string id, string name, List<string> hosts, string source, string notes)
        => new() { Id = id, Name = name, BuiltIn = true, Hosts = hosts, Source = source, Notes = "初期候補・完全な接続先を保証しません。 " + notes };
}
