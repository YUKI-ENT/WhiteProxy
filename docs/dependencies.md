# 固定依存関係とライセンス

確認日: 2026-10-26。各プロジェクトの `packages.lock.json` で推移依存の解決結果も固定しています。再現時は `dotnet restore WhiteProxy.sln --locked-mode` を使用します。単一EXE用は別の `packages.portable-win-x64.lock.json` を使用し、通常ビルドとRID依存の違いが競合しないようにしています。実際の製品依存一覧は `dependencies.json` にあります。

| 依存 | 固定版 | ライセンス・根拠 |
|---|---|---|
| Titanium.Web.Proxy / Abstractions | 7.0.26 | MIT。[公式](https://www.titaniumproxy.com/docs/editions)、固定コミット630f01804493a293fade9a48e8ee40387dbd7414のLICENSE |
| BouncyCastle.Cryptography | 2.6.2 | MIT。NuGetメタデータと[公式ライセンス](https://github.com/bcgit/bc-csharp/blob/release-2.6.2/LICENSE.md) |
| Microsoft.Extensions.Hosting.WindowsServices等 | 10.0.0 | MIT。各NuGetのlicense式 |
| System.ServiceProcess.ServiceController | 10.0.0 | MIT。NuGetメタデータ |
| 同梱.NET/WindowsDesktop runtime | 発行SDKが選択（今回10.0.12） | .NETのLICENSE/THIRD-PARTY-NOTICESを同梱 |
| xUnit | 2.9.3 | テスト専用、Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | テスト専用、Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | テスト専用、MIT |
| coverlet.collector | 6.0.4 | テスト専用、MIT |

Titanium本体7.0.26のnupkgはlicense式メタデータが空のため、公式固定ソースのMITを別途取得しました。Inspector/Plus製品は採用していません。ライセンス本文を `licenses/` と `THIRD-PARTY-NOTICES.txt` に保存し、単一EXEにもリソースとして埋め込み、GUIの「ライセンス」で表示します。

SDKや依存パッケージを更新するときは、固定版、lock、公式API、ライセンス、セキュリティテスト、発行ランタイムと同梱通知を合わせて確認してください。
