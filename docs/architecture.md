# WhiteProxy 構成

確認日: 2026-10-26。元仕様書の製品名・ソリューション名はユーザー指示によりWhiteProxyへ変更。インストーラー要件は単一EXE発行へ変更。

## 分割

WebコントロールはService内のASP.NET Core/Kestrelで提供します。プロキシーとは別のHTTPSポート（初期3129）を使用し、静的HTML/CSS/JSをEXEへ同梱します。GUIとWebはProxyWorkerの同じ検証・直列化・保存・適用経路を呼び出します。順序付きルールの表示・評価補助はCoreのDashboardRulesに共通化しています。Webの認証設定・パスワードハッシュ・TLS証明書は一般のConfigurationと分離し、読取りIPCへ秘密情報を公開しません。詳細は [Webコントロール](web-control.md) を参照してください。

| プロジェクト | 責務 |
|---|---|
| Core (net10.0) | 型付き設定、ホスト/IP正規化、入力検証、TimeProvider、予定表・許可判定、時計異常検知 |
| Proxy (net10.0) | Titaniumのラッパー、実際のPeer socket追跡、接続前判定、ストリーミング、切断 |
| Shared (net10.0) | IPC DTOとサイズ制限付きフレーミング、PipeClient |
| Service (net10.0-windows) | Generic Host/Windows Service、JSON Repository、ACL、IPC認可、永続化、監視、ログ |
| Admin (net10.0-windows) | 日本語WinForms画面、検証・適用、サービス登録・更新・削除。発行名WhiteProxy.exe |
| Tests / Windows.Tests | OS非依存ポリシー・通信テスト / Windows IPC・保存テスト |

`WhiteProxy.exe`（引数なし）はGUI。`--service` はWindowsサービス、`--console` は開発用の本体です。GUIが通信本体をホストすることはなく、閉じても本体の処理は続きます。IPCは公開プロキシーポートと別のローカルNamed Pipeです。

## Titanium API調査・採用判断

[公式Library](https://www.titaniumproxy.com/docs/library/) と [API](https://www.titaniumproxy.com/api/Titanium.Web.Proxy.ProxyServer.html)、[MITライセンス](https://github.com/justcoding121/titanium-web-proxy/blob/630f01804493a293fade9a48e8ee40387dbd7414/LICENSE) を確認。NuGet **7.0.26**、ソースコミット **630f01804493a293fade9a48e8ee40387dbd7414** に固定しました。現行Libraryは.NET 10です。

- `ExplicitProxyEndPoint(..., decryptSsl: false)` と `BeforeTunnelConnectRequest` / `DenyConnect` でCONNECT接続前に許可を評価。
- `BeforeRequest` でHTTP接続前判定。`Request.Url` はHostから再構成され得るため、判定には元の `RequestUriString` を使用。URI/CONNECT AuthorityとHostヘッダーの一致を検査し、重複Hostも拒否。
- `EstablishServerConnectionBeforeResponse = true` により接続先への接続成功前に200を返さない。
- `TerminateSession()` はキャンセル経路を持ちます。`OnClientConnectionCreate` で取得した実Peerのsocketも保持し、切断時には両方を使用します。Reflectionや非公開APIを製品コードで使用していません。
- `BlockPrivateNetworkDestinations = true` と `BlockLoopbackDestinations = true` を常時設定。固定版 `Network/TcpConnection/TcpConnectionFactory.cs` の `ConnectToAddressAsync` が解決済みIPを検査し、**その同じIP** の `IPEndPoint` へ接続することを確認。検査後にホスト名を再解決しません。
- 上流プロキシー、自動システムプロキシー検出、接続プール、事前接続、HTTP/2の終端を無効化。HTTP/3を有効化するコードはありません。HTTPSトンネル内でのクライアント/接続先のHTTP/2は不透明なまま通過します。
- Root CA生成・インストール・システムプロキシー変更APIを呼び出しません。テストでTLS証明書がオリジンのままであることを確認しています。

## Phase 0の結果

ローカル模擬オリジンを使った最小試験から開始し、`ProxyTests.ActiveConnectIsCutOffAtExpiry` で通信中のCONNECTが失効直後に閉じることを確認してからGUI/サービスを追加しました。追加のHTTPS試験は実時間の監視だけで失効切断します。HTTP Range/206、WebSocket Upgrade/フレーム、長いContent-Lengthのストリームも確認済みです。Titaniumを置き換える必要はありませんでした。

テスト用のinternalコンストラクターだけが模擬サーバーへのローカル接続を許可します。公開コンストラクター・設定・IPCには私設IP保護を無効化する経路がありません。製品では私設IPの例外登録も未対応です。

## 許可・期限

登録端末、ホスト/ポート、許可時間枠のすべてが必要です。全遮断と一致する無効サービスは優先して拒否します。IP/CIDRの同一・重複を許可し、Policies配列の上から送信元と接続先サービスの一致する行を評価します。端末の無効/停止、時間外、失効、開始予約は読み飛ばします。有効な許可または明示的なDenyで確定し、該当する有効な許可がなければ拒否します。

予定表は指定タイムゾーンで開始曜日を評価し、終了<=開始のうち「終了<開始」は翌日、「同時刻」はエラーです。区間は `[start,end)`。複数の現在有効な許可のうち最も遅い失効を返し、常時許可は期限なしです。未来の開始枠への移行も接続ごとの再評価で扱います。

250ms周期で既存セッションを再評価し、許可がなくなればソケットを破棄します。新規接続時にもその時点のUTCを使い、周期タイマーだけに失効判定を依存しません。許可が別の枠で継続する場合は切断しません。

UTCの逆行2秒超、壁時計と単調時計の差120秒超、保存済みの最終UTCより5秒超過去の起動を異常とし、再起動までfail-closedです。10秒ごとに最終UTCを保存します。再起動中の時刻巻戻しを完全に証明するものではなく、NTPとOS時刻管理は必要です。夏時間で開始/終了が欠落・重複する枠は開きません。祝日解釈はありません。

## 永続化と障害

JSONはスキーマ2と更新版番号を持ち、同一ディレクトリへの一時書込み・Flush・File.Replaceで原子的に置換、一世代 `.bak` を保持します。Repositoryを境界に置きました。破損した設定からバックアップへ自動復帰して古い許可を復活させません。外部ファイル変更も検知して遮断します。

編集内容は各編集ダイアログ内だけに保持し、「保存」でローカル検証・サービス側検証・保存・反映まで実行します。共通の再読込・検証・適用ボタンや画面全体の保留中の下書きはありません。エラー時はダイアログを閉じず入力を残します。検証後の適用に失敗したら旧設定を保存し直して通信を閉じ、修正と再保存を要求します。版番号により別GUIからの古い設定による上書きを拒否します。一時許可の作成/延長/取消は専用IPCのみで、操作者SIDとサービス側時刻を使用します。

待受・運用設定は未登録またはサービス停止中に限り、管理者GUIからRepository経由で保存できます。保存時に停止状態・設定版を再確認し、既存の端末・ポリシー・一時許可履歴・全遮断状態を保持します。稼働中の設定ボタンは無効です。サービス操作は開始・停止・状態更新を1行にまとめ、初回登録は未登録時、本体更新・登録削除は登録済みで停止中にだけ別グループへ表示します。

ログはUTF-8 JSONL、4,096件の非同期キュー、1ファイル10MiB、設定した日数で保持。キュー溢れ・書込み失敗時は欠損数を表示してfail-closedとなり、再起動が必要です。GUIは直近1,000件を保持します。ライブラリ由来のログは本文・例外メッセージ・パラメーターを捨て、イベント番号と例外型だけにします。

## 残る受入項目

サービスのSCM登録・OS再起動・実スリープ復帰・LAN/NIC/境界FW・各ベンダーの実利用・実WU/Defender・複数GB転送は対象環境で受入が必要です。端末の最終接続時刻とGUIの全接続詳細一覧、任意の私設IP例外、全期間ログのGUI読込み、OS時計の信頼を回復する専用操作は未実装です。上流プロキシーは第2段階です。

## サービスと管理GUIの接続修正

Named Pipeは応答を書いた直後には切断せず、クライアントが応答全体を読み終えて接続を閉じるまで待ちます。[DisconnectNamedPipeの公式仕様](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-disconnectnamedpipe)では、切断時に未読データが破棄されます。.NETのFlushAsyncだけで読取り完了を待つことはできないため、明示的なEOF待ちを追加しました。遅い読取りと接続再利用を実Named Pipeで回帰テストしています。

起動診断はservice-status.jsonへ、段階・状態・例外型・HResultだけを記録します。Usersにこのファイルだけの読取り権限を与え、設定・通信ログのACLは広げません。登録・更新時にはDOTNET_BUNDLE_EXTRACT_BASE_DIRを保護されたProgramDataのruntimeフォルダーへサービス単位で設定します。単一EXEの展開先の扱いは[公式資料](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)を参照しています。

## 登録管理の権限別表示と起動方法

管理GUIの起動時に一度だけUACで昇格し、画面内の操作権限を管理者に統一します。画面内の昇格ボタンや権限別メニューの切替はありません。すでに管理者で起動済みの場合は再昇格しません。サービス経路はGUIの昇格より先に識別してNetworkServiceのまま起動し、Windows起動時の対話を不要にします。初回登録と登録済みプログラムの入替を説明し、更新ボタンは「サービスのプログラムを更新」にしています。

起動方法は登録設定のStartを読み取って表示します。自動の場合、Windows起動時にログインやGUI起動なしで開始し、手動の停止は自動起動の設定を変更しないことを明記しています。手動・無効・取得できない場合には自動開始すると表示しません。GUIの表示変更に伴うサービス設定の変更は行っていません。

## Named PipeのEOF後の再利用

管理パイプはNamedPipeListenerが所有し、クライアントの切断・不正要求と待受自体の障害を分離します。PipeStreamはEOF後にIsConnected=false（Broken）となるため、IsConnectedを条件にDisconnectを省略してはいけません。正常応答後も、要求エラー後も、受け付けた接続は必ずリセットしてから再度待ち受けます。接続待ちの段階での途中切断はハンドルを再作成します。初回の名前競合・ACLエラーは起動時点で検出します。

失敗診断には、例外本文やソースパスではなく発生メソッド名だけを追加しました。これにより「管理GUI接続待受」という段階名だけでなく、実際の失敗箇所を確認できます。

## 許可ルールの統合画面

管理GUIのDashboardRulesは保存されたAccessPolicyと未失効のTemporaryGrantから端末×サービスの一覧を構成します。旧データにポリシーがなくても期限付き許可を見失いません。PolicyEngine.EvaluateRuleは実際の通信判定と共通の時間枠評価処理を使用します。画面の時間枠判定は通信先ホスト・重複定義・DNSの安全性まで許可する判定ではありません。サービス未接続・異常時は開放中を表示しません。

RuleEditorは候補Configurationのコピー内で端末とルールを編集し、単一applyで同時保存します。キャンセルは一切保存しません。全日許可は確認を要求し、初期モードは一時許可のみです。既存のDenyは手動遮断として使用し、親画面の保存や一時許可では解除しません。判定順序はPolicies配列を使用し、スキーマ2で旧プログラムの誤解釈を防ぎます。スキーマ1は読込時に拒否を先頭へ移行します。status応答のMessageにactive-ordered-rules-v3を返し、GUIは対応していない旧サービスへの設定変更を拒否します。


ダッシュボードの各行はDataGridViewButtonColumnで編集・一時許可・一時遮断・削除・上下移動を提供します。無効な操作は描画と実行側の両方で拒否します。列ソートは無効で、表示順と適用順を一致させます。GrantEditorは新規/延長/終了を一画面で行い、理由は任意（GUIは入力欄なし）とします。操作者・対象・操作・期限の監査は維持します。対話中に別GUIで対象が変更された場合、一時許可も元の版番号を使って競合を検出します。


## サービスセットとGUIの単一起動

ServicePacksは許可サービス専用DTOをUTF-8 JSONから読み込みます。構成全体のDTOとは分離し、未知項目・不正なホスト/ポート・サイズ/件数超過を拒否します。PrepareはConfigurationのコピーへ新しいサービスIDを追加するだけで、既存のID・ルール・停止状態を変更しません。追加前のServiceImportEditorで内容を確認し、通常の版番号検証付きapplyを一度だけ実行します。自動ダウンロードや監視は行いません。

GuiInstanceはGUIの起動経路だけでLocal名前空間のMutexを保持します。既存ウィンドウがあれば昇格前に復元・前面表示します。同時起動や画面生成前の競合もMutexで抑止し、異常終了による放棄Mutexを取得できます。サービス/コンソール/管理コマンドの起動を妨げません。

DashboardThemeとRuleActionCellが状態バッジと操作ボタンを描画します。DataGridViewCellBorderStyle.SingleHorizontalで縦罫線を廃止し、右端つまみをドラッグの対象とします。判定順・IPC・設定スキーマの変更はありません。


## 保存済み拒否ログの候補検索

LogCandidateSearchは管理GUI側で保護されたログディレクトリのJSONLを読取専用で走査します。LogStore.Snapshotの1,000件ではなく、指定UTC範囲の保存ファイルを使用します。SnapshotStreamがファイルの読取開始時点のサイズに制限し、追記中でも読み続けません。FileShare.ReadWrite/Deleteでサービスの書込み・ローテーションと共存します。候補はホスト正規化・送信元IP/CIDR・半開区間の日時・通信拒否に絞り、件数・ポート・時刻・理由を集計します。

UIは全候補を初期未選択とし、既存パターンでカバーするホストは追加不可にします。選択結果は親のTextBoxへ完全一致ホストとしてマージするだけで、設定は親保存まで変更しません。候補のポートやワイルドカードは自動追加しません。256MiB/2,000ホストで部分結果と表示し、30秒で中止、破損/書込途中/読取失敗は結果に明示します。IPCと設定スキーマは維持しています。


## Firewall、バージョン、サービス変更

FirewallManager.Planが配置先EXE・待受IP/ポート・登録送信元を決定し、Windows Firewall COM APIでアプリ専用受信ルールを登録/更新します。TCPプロトコルをポート指定より先に設定し、Domain/Private（3）に限定します。サービスSIDの追加は不要なプログラムルールです。ループバック待受・送信元なしは管理ルールの削除だけを行い、/0は送信元候補から除外します。プロキシー本体は引き続きNetworkServiceで動き、Firewall変更は管理GUI/管理コマンドの管理者権限で行います。

[MicrosoftのINetFwRule仕様](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nn-netfw-inetfwrule)に基づき、ルールオブジェクトのプロパティを確定してから、既存の管理ルールを削除し追加します。テストは未登録のCOMオブジェクトへの設定を確認し、実機のFirewallポリシーへ追加しません。

Directory.Build.propsはVersion.propsを読み込み、SDK生成のアセンブリ/ファイル/情報バージョンへ渡します。AppVersionは情報バージョンから表示値を取り、GuiInstanceは旧版・別バージョンの管理GUIタイトルにも対応します。

RuleEditorは変更先のサービス選択を許可し、元のポリシー配列位置を置換します。キー衝突と旧サービスの有効な一時許可/予約を拒否します。過去の履歴や一時許可を変更先へ移さず、キャンセル時は元の設定を変更しません。
