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
```

接続先は`--url` → ログイン時のURL → 環境変数`SYSTEMAGENT_URL` → `http://localhost:5000`の順に決まる。`--json`で結果をJSON出力する。ログイン状態は`~/.config/systemagent/session.json`（600）に保存される。開発時は`dotnet run --project src/SystemAgent.Cli -- --url http://localhost:5246 status`のように実行する。

## DBマイグレーション

```bash
dotnet ef migrations add <Name> --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web -o Persistence/Migrations
dotnet ef database update --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web
```

接続文字列は`src/SystemAgent.Web/appsettings.json`の`ConnectionStrings:Default`（テンプレート値。実際の秘密情報はホスト側で管理する。[ADR-003](docs/adr/0003-secret-protection.md)）。

## ステータス

[ADR-013](docs/adr/0013-mvp-implementation-order.md)の①「基盤」を実装済み: 永続化、認証（通常JWT + ローカル緊急認証 + 初期セットアップ）、ユーザー管理、ノード管理（手動登録）、監査ログ、WebUI、CLI。次は②コンテナ管理（Podman）。
