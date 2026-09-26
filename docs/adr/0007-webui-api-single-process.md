# ADR-007: WebUIとWebAPIの同一プロセス・同一ポートホスト

## ステータス
決定（[docs/qa/answers/0001-kickoff.md](../qa/answers/0001-kickoff.md) A1）

## コンテキスト
基本設計書3.2節ではBlazor Server WebUIとASP.NET Core WebAPIを別コンポーネントとして描いていたが、実装時に別プロセス（別ポート）とするか同一プロセスとするかは未確定だった。

## 決定
`SystemAgent.Web`プロジェクト1つにBlazor ServerのRazorコンポーネントとASP.NET Core WebAPI（Controllers）を同居させ、同一プロセス・同一ポートでホストする。独立した`SystemAgent.Api`プロジェクトは作らない。

## 理由
- 配布・サービス管理（systemd unit数）がシンプルになる。
- 開発初期段階であり、プロセスを分ける運用上の利点（独立スケーリング等）は不要（非機能要件2.2「スケーラビリティ想定不要」）。

## 影響
- CLIは引き続きHTTP経由でWebAPIエンドポイントを呼び出す（初期はlocalhost直叩き、A1）。
- WebUI(Blazor Server)のコンポーネントも「業務ロジックを持たずAPIを叩くクライアント」という設計原則（9章）は維持する。同一プロセス内であっても、コンポーネントからアプリケーションサービスを直接DI注入せず、HttpClient経由でAPIエンドポイントを呼ぶことで、CLIとの機能同一性を保証する。
