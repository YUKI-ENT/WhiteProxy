# クライアント設定・Windows Update

確認日: 2026-10-26。これは導入ガイドであり、実端末のGPOや更新方式に合わせた受入が必要です。実行例のアドレスは実環境へ置き換えてください。

## ブラウザー・一般アプリ

Windowsの「設定」→「ネットワークとインターネット」→「プロキシ」の手動設定で、サーバーのVLAN側IPv4と待受ポートを指定します。組織では対象PCに合わせてGPOで一括設定します。個別プロキシー設定を持つブラウザーや業務アプリも、そのアプリの設定を確認してください。

GUIでその端末の実IPと必要ホストを登録し、時間枠または常時許可を適用します。HTTP/HTTPSとも同じ明示プロキシーへ接続します。TLS復号用CAのインストールは不要です。自動構成/PAC配布は本アプリに含めていません。

## WinHTTP・BITS・Delivery Optimization

[Microsoftの説明](https://learn.microsoft.com/en-us/troubleshoot/windows-server/installing-updates-features-roles/windows-update-client-determines-proxy-server-connect) によると、更新の検索にはWinHTTP、ダウンロードにはBITSまたはDOが関わり、それぞれの設定・実行アカウントを考慮する必要があります。WinINETのユーザー向け設定だけで全通信がそろうとは限りません。

管理者がWinHTTP設定を確認・設定する例:

```powershell
netsh winhttp show proxy
netsh winhttp set proxy proxy-server="http=192.168.10.10:3128;https=192.168.10.10:3128"
# ユーザー設定を取り込む場合は、その内容と適用範囲を先に確認する
netsh winhttp import proxy source=ie
```

これだけを唯一の手段とはしません。対象OSのGPO、BITSの実行アカウント、DOのDownload Mode・プロキシー検出、ユーザー設定との差を検証してください。変更前の設定を記録し、組織の既存GPOを無断で置換しないでください。

[DOの公式プロキシー要件](https://learn.microsoft.com/en-us/windows/deployment/do/delivery-optimization-proxy) では、DOクラウドサービスへの直接接続や、クライアントの公開IPを隠すプロキシーなどに対するバイパス推奨、証明書ピンニングの制約が説明されています。WhiteProxyはTLSを復号しませんが、ネットワーク構成による制約がなくなるわけではありません。直接通信を禁止する設計では、必要な例外と時間制御をVLAN境界FWも含めて決めてください。

## WU/Defenderテンプレート

初期のWindows UpdateとDefenderのホスト一覧は空で、通信は拒否します。実際のMicrosoft公式資料、対象OS・更新方式・拒否ログを確認したうえで管理者がホストを登録してください。`*.microsoft.com` などをまとめて推測登録しません。

[Defenderネットワーク資料](https://learn.microsoft.com/en-us/defender-endpoint/configure-network-connections-microsoft-defender-antivirus) はクラウド保護と更新方式の違いも説明しています。Defender for Endpoint導入環境は同資料から該当する接続要件を参照してください。WUとDefenderが同じホストを使う場合、ホスト単位プロキシーでは機能を分離できません。

検証は、更新検索、更新ダウンロード、Defender定義更新、期限切れでの中断、GUI終了中の失効、再起動後の再拒否を順に確認します。HTTP Range/206は維持します。「一度だけ」は指定時間枠の意味であり、更新完了を検出して自動終了する機能ではありません。

## 境界FW

クライアントVLANからはプロキシーの指定TCPポートだけへ到達させ、直接外部接続・UDP/443・不必要なDNS/DoH・P2P等の経路はルーター/FWで制限します。サーバー自身には必要なDNS解決と許可サービスへの外向きTCP到達性を用意します。管理用IPCはLANへ公開しません。Windows Firewallはサーバーの特定LAN側IP・ポート・送信元端末/サブネット・Domain/Privateプロファイルに絞ります。
