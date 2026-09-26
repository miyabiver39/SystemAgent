# SystemAgent

RHEL系・Debian/Ubuntu系サーバーの運用管理（コンテナ/ネットワーク/NTP/Keepalived/レジストリ/デプロイ）を統合的に行うツール。マルチノード対応、自社ソフトウェアのデプロイ機能を含む。

詳細な設計は[基本設計書](docs/design/SystemAgent_基本設計書_draft.md)を参照。

## 構成

```
SystemAgent.sln
src/
  SystemAgent.Core            共有ドメインモデル・Capability Providerインターフェース
  SystemAgent.Infrastructure  MariaDB永続化（EF Core/Pomelo）
  SystemAgent.Web             Blazor Server WebUI + ASP.NET Core WebAPI（同一プロセス。ADR-007）
  SystemAgent.Cli             CLI
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

### 初回ログイン

1. 初回起動時に秘密情報ディレクトリ（開発時は`src/SystemAgent.Web/.secrets/`、本番既定は`/var/lib/systemagent/secrets/`）が生成され、緊急ユーザー`admin`の初期パスワードが`initial-admin-password`に書き出される（ログにはファイルパスのみ出力）。
2. WebUIで「緊急ログイン」にチェックして`admin`でログインし、「アカウント」からパスワードを変更する（変更すると初期パスワードファイルは削除される）。
3. 「ユーザー」から通常認証用のDBユーザーを作成し、以降はそのユーザーでログインする。

DB停止中は通常ログインが使えず、ログイン画面が自動的に緊急ログインに切り替わる。

## DBマイグレーション

```bash
dotnet ef migrations add <Name> --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web -o Persistence/Migrations
dotnet ef database update --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web
```

接続文字列は`src/SystemAgent.Web/appsettings.json`の`ConnectionStrings:Default`（テンプレート値。実際の秘密情報はホスト側で管理する。[ADR-003](docs/adr/0003-secret-protection.md)）。

## ステータス

[ADR-013](docs/adr/0013-mvp-implementation-order.md)の①「基盤」を実装済み: 永続化、認証（通常JWT + ローカル緊急認証）、ユーザー管理、ノード管理（手動登録）、監査ログ、WebUI。認証の暫定方針は[QA 0003](docs/qa/questions/0003-auth-policy.md)で確認中。次は②コンテナ管理（Podman）。
