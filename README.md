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
dotnet run --project src/SystemAgent.Web
```

`GET /api/health`でAPI疎通確認、`/`でWebUIを確認できる。MariaDBが未起動でも起動は可能（DB接続は実際のクエリ実行時まで遅延する）。

## DBマイグレーション

```bash
dotnet ef migrations add <Name> --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web -o Persistence/Migrations
dotnet ef database update --project src/SystemAgent.Infrastructure --startup-project src/SystemAgent.Web
```

接続文字列は`src/SystemAgent.Web/appsettings.json`の`ConnectionStrings:Default`（テンプレート値。実際の秘密情報はホスト側で管理する。[ADR-003](docs/adr/0003-secret-protection.md)）。

## ステータス

初期スケルトン + 永続化基盤(EF Core/MariaDB)まで構築済み。実装方針のTBDは[ADR一覧](docs/adr/)に反映済み（0001スレッド[回答](docs/qa/answers/0001-kickoff.md)ベース）。開発・検証環境の方針について[0002スレッド](docs/qa/questions/0002-dev-test-environment.md)への回答待ち。
