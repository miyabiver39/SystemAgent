# SystemAgent

RHEL系・Debian/Ubuntu系サーバーの運用管理（コンテナ/ネットワーク/NTP/Keepalived/レジストリ/デプロイ）を統合的に行うツール。マルチノード対応、自社ソフトウェアのデプロイ機能を含む。

詳細な設計は[基本設計書](docs/design/SystemAgent_基本設計書_draft.md)を参照。

## 構成

```
SystemAgent.slnx
src/
  SystemAgent.Core            ドメインモデル・APIの入出力型(Contracts)・各種インターフェース
  SystemAgent.Infrastructure  MariaDB永続化（EF Core/Pomelo）・ローカル秘密情報
  SystemAgent.Web             Blazor Server WebUI + ASP.NET Core WebAPI（同一プロセス。ADR-007）
  SystemAgent.Client          WebAPIクライアント（WebUIとCLIが共用。操作の同一性をここで担保）
  SystemAgent.Cli             CLI（コマンド名 systemagent）
tests/
  SystemAgent.Core.Tests
docs/
  design/  基本設計書
  adr/     Architecture Decision Record
  qa/      AIエージェントとのQ&A掲示板（questions/ と answers/ を分離）
```

WebUIとWebAPIは`SystemAgent.Web`に同居し、同一プロセス・同一ポートでホストする（[ADR-007](docs/adr/0007-webui-api-single-process.md)）。OpenAPI定義（`/openapi/v1.json`、開発環境のみ）を機能一覧の正とし、CLIはこのAPIをHTTP経由で呼び出すクライアントとする。

## ドキュメント

- [基本設計書](docs/design/SystemAgent_基本設計書_draft.md)
- [ADR一覧](docs/adr/)
- [QA掲示板](docs/qa/README.md) — 未決事項はここで質問・回答をやり取りする

## ビルド

```bash
dotnet build
```

## 開発・検証環境（WSL2）

RHEL系/Ubuntu系の検証と開発用MariaDBはWSL2上に構築する（[ADR-014](docs/adr/0014-dev-test-environment.md)）。

```powershell
./scripts/dev/setup-wsl.ps1   # 初回のみ（冪等）
./scripts/dev/start-wsl.ps1   # 開発開始時（ディストリビューションの自動停止を防ぐ）
```

## 実行（開発時）

```bash
dotnet run --project src/SystemAgent.Web --launch-profile http
```

http://localhost:5246/ でWebUI、`GET /api/health`でAPI疎通とDB接続状態を確認できる。MariaDBが未起動でも起動は可能。

本番（環境変数未設定時）は`http://0.0.0.0:5000`で待ち受ける。

### 初期セットアップ（[ADR-015](docs/adr/0015-auth-policy-and-initial-setup.md)）

初回起動時に秘密情報ディレクトリ（開発時は`src/SystemAgent.Web/.secrets/`、本番既定は`/var/lib/systemagent/secrets/`）にワンタイムのセットアップトークン`setup-token`が生成される。次のどちらかで緊急認証の管理者を作成する。

- CLI: サーバー上で`sudo systemagent setup`（トークンを自動で読み、ユーザー名・パスワードを対話入力）
- WebUI: ログイン画面が初期セットアップ画面に切り替わるので、`setup-token`の内容と管理者のユーザー名・パスワードを入力

その後、緊急ログインで入り「ユーザー」から通常認証用のDBユーザーを作成して、以降はそのユーザーでログインする。DB停止中は通常ログインが使えず、ログイン画面が自動的に緊急ログインに切り替わる。

## CLI

WebUIと同じ操作を同じWebAPI経由で行える。

```bash
systemagent setup                    # 初期セットアップ
systemagent login [-u USER] [--emergency]
systemagent status                   # 接続先・DB状態・ログイン状態
systemagent node list|register|delete
systemagent user list|create|delete
systemagent passwd                   # 自分のパスワード変更
systemagent logout
systemagent env                      # OS情報・検出されたツール
systemagent container list|start|stop|restart|rm|logs
systemagent pod list                 # Podmanのみ
systemagent image list|pull|rm|import <tar>
systemagent ntp status|set <server>...|sync
```

接続先は`--url` → ログイン時のURL → 環境変数`SYSTEMAGENT_URL` → `http://localhost:5000`の順に決まる。`--json`で結果をJSON出力する。ログイン状態は`~/.config/systemagent/session.json`（600）に保存される。開発時は`dotnet run --project src/SystemAgent.Cli -- --url http://localhost:5246 status`のように実行する。

### Linuxノード上での動作確認（WSL）

コンテナ管理等のOS操作はLinux上でしか動かないため、linux-x64向けにpublishしてWSLのディストリビューション内でrootとして起動する。WSLのディストリビューションはネットワークを共有するため、`sa-alma9`のMariaDBを全ノードから`localhost:3306`で使える（中央DB構成の検証にもなる）。

```bash
dotnet publish src/SystemAgent.Web -c Release -r linux-x64 --self-contained -o artifacts/linux/web
dotnet publish src/SystemAgent.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o artifacts/linux/cli
```

```bash
wsl -d sa-alma9 -u root -- sh -c 'cd /mnt/c/<リポジトリ>/artifacts/linux/web && ConnectionStrings__Default="Server=localhost;Port=3306;Database=systemagent;User=systemagent;Password=systemagent_dev;" ./SystemAgent.Web'
```

Windowsからは http://localhost:5000 で接続できる。2台目以降は`Urls=http://0.0.0.0:5001`等でポートを変えて`sa-ubuntu2404`（`Container__Runtime=docker`でDocker）や`sa-debian`（timesyncd）で起動する。

注意: `wsl -- <コマンド>`はディストリビューションのログインシェルを経由するため、`sh -c '...'`内の`$VAR`や`$?`が先に展開されてしまう。変数を使う場合は`wsl --exec sh -c '...'`とする。

## OS/バージョン対応の追加（コマンドテンプレート）

OSコマンドの差分は`src/SystemAgent.Infrastructure/CommandTemplates/`のJSONで定義する（[ADR-005](docs/adr/0005-command-abstraction.md)、[ADR-016](docs/adr/0016-command-template-implementation.md)）。新しいOS/バージョンへの対応は、原則としてテンプレートの追加だけで行う。現地では`/etc/systemagent/templates/`に置けば再ビルドなしで追加・上書きできる。スキーマは`command-template.schema.json`、必須コマンドは`TemplateRequirements.cs`。

## DBマイグレーション

```bash
dotnet ef migrations add <Name> --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web -o Persistence/Migrations
dotnet ef database update --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web
```

接続文字列は`src/SystemAgent.Web/appsettings.json`の`ConnectionStrings:Default`（テンプレート値。実際の秘密情報はホスト側で管理する。[ADR-003](docs/adr/0003-secret-protection.md)）。

## ステータス

[ADR-013](docs/adr/0013-mvp-implementation-order.md)の①「基盤」を実装済み: 永続化、認証（通常JWT + ローカル緊急認証 + 初期セットアップ）、ユーザー管理、ノード管理（手動登録）、監査ログ、WebUI、CLI。
②コンテナ管理を実装済み: コンテナ一覧/起動/停止/再起動/削除/ログ、Pod一覧、イメージ一覧/pull/削除/アーカイブ取り込み、環境検出（Podman 5.8・4.9、Docker 29.1で検証）。
③のうちNTP設定を実装済み: 同期状態・時刻ソースの表示、NTPサーバーの設定（失敗時は自動で元に戻す）、即時同期（chrony: RHEL系/Debian系、systemd-timesyncdに対応）。次は③ネットワーク設定。
