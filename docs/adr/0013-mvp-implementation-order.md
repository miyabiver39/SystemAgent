# ADR-013: MVP実装順序

## ステータス
決定（[docs/qa/answers/0001-kickoff.md](../qa/answers/0001-kickoff.md) A7: 「最終的に全て実装するので、順番は任せる」）

## コンテキスト
基本設計書2.1節の機能要件（コンテナ管理／イメージ管理／ネットワーク設定／NTP設定／Keepalived／レジストリ管理／マルチノード操作／デプロイ機能）を並行実装するのは非効率なため、実装順序を決める必要があった。発注者からは優先順位の決定を委任された。

## 決定
以下の順序で実装を進める。

1. **基盤（永続化・ノード管理・認証）**
   - MariaDB永続化基盤（EF Core / Pomelo、[SystemAgent.Infrastructure](../../src/SystemAgent.Infrastructure)）
   - ノード登録・一覧管理（4.1節）
   - 認証（6章: 通常JWT + ローカル緊急認証）
2. **コンテナ管理（Podman優先）**
   - Capability Providerパターンの実装第1弾として`IContainerRuntimeProvider`（7章）
   - イメージ管理（エアギャップ向けインポート含む）
3. **ネットワーク設定・NTP設定**
   - `INetworkProvider` / `INtpProvider`
4. **マルチノード通信**
   - 自己CA、mTLS、ノード登録シーケンス（4.1節）
5. **Keepalived + フェイルオーバー**
   - `notify_master`フック連携、昇格API（4.2節）
6. **コンテナレジストリ管理**
7. **デプロイ機能**（自社ソフトウェアのイメージ更新・デプロイ）

## 理由
- 認証・永続化・ノード管理は他の全機能が依存する基盤のため最優先とする。
- コンテナ管理はPoCツールの主要機能であり、Capability Providerパターンの実証（他機能への横展開の型を作る）を兼ねて2番目に着手する。
- Keepalived・マルチノードはノード基盤ができてから着手する方が手戻りが少ない。
