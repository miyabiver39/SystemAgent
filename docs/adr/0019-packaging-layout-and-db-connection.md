# ADR-019: パッケージ構成とDB接続情報の保存

## ステータス
決定（ADR-006 の具体化）

## 決定

### パッケージ（nfpm、`build/package/`）
| パス | 内容 |
|---|---|
| `/usr/lib/systemagent/` | WebUI/WebAPI（self-contained。.NETランタイムの導入不要）。実行ファイルは `SystemAgent.Web` |
| `/usr/bin/systemagent` | CLI（単一ファイル） |
| `/etc/systemagent/systemagent.json` | 設定（ポート・ノード名等）。`config|noreplace`でアップグレード時に上書きしない |
| `/etc/systemagent/templates/` | 現地で追加するコマンドテンプレート（ADR-016） |
| `/var/lib/systemagent/` (0700) | ローカル秘密情報。初回起動時に生成 |

- 依存: RPMは`libicu`、DEBは`libicu78 | libicu76 | … | libicu63`（Ubuntu/Debianのバージョンごとにパッケージ名が異なるため）。
- systemdのunitファイルは含めない（利用者側で用意。ADR-017）。起動は`ExecStart=/usr/lib/systemagent/SystemAgent.Web`（カレントディレクトリは任意。appsettings.jsonが無ければ配置先をコンテンツルートにする）。
- 設定の優先順位: `appsettings*.json`（同梱）< `/etc/systemagent/systemagent.json` < 環境変数。
- 作成: `./build/package/package.ps1 -Version x.y.z` → `artifacts/package/*.rpm, *.deb`。
- 検証: AlmaLinux 9にRPM、Debian 13にDEBを導入し、権限・依存関係・新規導入手順（setup → login → db set）・アンインストールを確認。

### DB接続情報
- DB接続文字列（パスワードを含む）は`/etc`の設定ファイルには置かず、**ローカル秘密情報に暗号化して保存**する（ADR-003）。WebUIの「設定」またはCLIの`systemagent db set`で設定する（基本設計書6章「DB接続情報の変更機能」）。
- 保存前に接続を試し、成功した場合のみ保存し、未適用のマイグレーション（テーブル作成・変更）を適用する。操作は監査ログに残す。
- 接続文字列はDbContext生成のたびに読むため、変更は再起動なしで反映される。
- 参照順: ローカル秘密情報 → 設定ファイル（`ConnectionStrings:Default`。開発用）。
