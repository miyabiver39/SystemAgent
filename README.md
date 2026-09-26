# SystemAgent

RHEL系・Debian/Ubuntu系サーバーの運用管理（コンテナ/ネットワーク/NTP/Keepalived/レジストリ/デプロイ）を統合的に行うツール。マルチノード対応、自社ソフトウェアのデプロイ機能を含む。

詳細な設計は[基本設計書](docs/design/SystemAgent_基本設計書_draft.md)を参照。

## 構成

```
SystemAgent.sln
src/
  SystemAgent.Core   共有ドメインモデル・Capability Providerインターフェース
  SystemAgent.Api    ASP.NET Core WebAPI（OpenAPI定義が正。WebUI/CLIはこのAPIのクライアント）
  SystemAgent.Web    Blazor Server WebUI
  SystemAgent.Cli    CLI
tests/
  SystemAgent.Core.Tests
docs/
  design/  基本設計書
  adr/     Architecture Decision Record
  qa/      AIエージェントとのQ&A掲示板（questions/ と answers/ を分離）
```

## ドキュメント

- [基本設計書](docs/design/SystemAgent_基本設計書_draft.md)
- [ADR一覧](docs/adr/)
- [QA掲示板](docs/qa/README.md) — 未決事項はここで質問・回答をやり取りする

## ビルド

```bash
dotnet build
```

## 実行（開発時）

```bash
dotnet run --project src/SystemAgent.Api
dotnet run --project src/SystemAgent.Web
```

## ステータス

初期スケルトン作成段階。実装方針の詳細は[docs/qa/questions/0001-kickoff.md](docs/qa/questions/0001-kickoff.md)への回答待ち。
