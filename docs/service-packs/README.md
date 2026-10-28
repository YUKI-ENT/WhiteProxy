# 許可サービスのJSONインポート

「許可サービス」タブの「JSONからインポート」でファイルを選びます。追加されるサービス名・ホスト・TCPポート・出典を確認して「追加する」を押します。追加するのはサービス定義だけです。既存の端末・許可ルール・スケジュール・一時許可は変更しません。追加後にダッシュボードのルールでサービスを選択すると、そのルールの条件で通信を許可できます。

WhiteProxy.exeの単一ファイル発行は継続しています。JSONは任意の外部データとして読み込み、内容を通常の設定に保存します。取り込んだJSONファイルを常駐させる必要はありません。

## ファイル形式

UTF-8（BOM付きも可）のJSONです。サービスセットの形式番号は、アプリ設定JSONのスキーマ番号とは別です。

```json
{
  "format": "WhiteProxy.ServicePack",
  "version": 1,
  "name": "業務アプリの更新先",
  "services": [
    {
      "name": "業務アプリ更新",
      "hosts": ["updates.example.com", "*.download.example.com"],
      "allowedPorts": [443],
      "source": "https://example.com/network-requirements",
      "definitionVersion": "2026-10-28",
      "notes": "ベンダーの公式資料を確認して指定してください。"
    }
  ]
}
```

必須項目はformat、version、name、services、および各サービスのname・hosts・allowedPortsです。source・definitionVersion・notesは省略できます。ID・端末・ルール・許可時間・有効/無効設定など、上記にない項目は受け付けません。新しいサービスにはアプリ側で新しいIDを発行します。

ホストは完全一致または `*.example.com` の形式です。ワイルドカードは親ドメイン自体を含みません。URLのパスやIP範囲は指定できません。空のホスト一覧、全体に一致する `*`、不正なポートを拒否します。80/443以外のポートを含む場合は追加時に確認します。

上限は1ファイル1MB、1～128サービス、各サービス1～1,024ホスト、1～128ポートです。アプリ全体の設定件数とサイズ上限も検証します。いずれかに不備がある場合、途中まで追加しません。

同じ名前（または以前のインポート時に付けた別名）・ホスト一覧・ポート一覧の定義はスキップします。同じ名前で接続先が異なる場合は「（インポート 2）」などの別名で追加します。既存サービスの接続先やメタデータを上書きしません。登録済みサービスの変更は通常の編集画面で行います。

## Microsoft 365 Appsの例

[microsoft365-licensing.json](microsoft365-licensing.json) は、Microsoft 365 Appsのライセンスサービスの主要2ホストとTCP 443を登録する例です。

- ols.officeapps.live.com
- activation.sls.microsoft.com

出典: [Microsoft公式のライセンス認証データ・接続先の説明](https://learn.microsoft.com/en-us/microsoft-365-apps/licensing-activation/licensing-activation-data-sent-microsoft-365-apps)（2026-10-28確認）。

この例はサインイン先、証明書確認先、更新配信先を含みません。Officeの製品・契約・認証方式によって必要な接続先が異なるため、対象環境で確認して追加します。Microsoft 365全体やすべてのOffice製品の接続先一覧ではありません。


## エクスポート

「許可サービス」で保存済みのサービスを選び、「選択サービスをJSONにエクスポート」を押します。Ctrl/Shiftキーで複数選択もできます（1～128件、1MB以内）。保存するのは選択したサービスだけで、UTF-8のWhiteProxy.ServicePack形式です。同じインポート機能で読み込めます。

サービス名・ホスト・許可TCPポート・出典・定義バージョン・注意事項を出力します。内部ID、組込候補フラグ、有効/無効、端末・許可ルール・一時許可は含みません。ホスト未登録などインポートできない内容は、書き出す前に拒否します。エクスポート自体で現在の設定を変更しません。
