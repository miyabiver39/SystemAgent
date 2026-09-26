# ADR-016: コマンドテンプレートとCapability Providerの実装方式

## ステータス
決定（ADR-005 の具体化。コンテナ管理の実装時に決定）

## 決定

### 構成
- **インターフェース（固定）**: `SystemAgent.Core/CapabilityProviders`（例: `IContainerRuntimeProvider`）。
- **テンプレート（データ）**: `src/SystemAgent.Infrastructure/CommandTemplates/<capability>/*.json`。スキーマは同ディレクトリの`command-template.schema.json`。
- **実装（汎用）**: Capabilityごとに1クラス（例: `TemplateContainerRuntimeProvider`）。テンプレートのコマンドを実行するだけで、Podman/Docker等の分岐をコードに持たない。
- **パーサー（コード）**: 出力形式の差分は名前付きパーサー（例: `podman-containers-json`, `docker-containers-jsonl`）に隔離し、テンプレートの`parser`で参照する。
- **要件定義**: Capabilityごとの必須コマンド・使用可能なプレースホルダ・パーサーは`TemplateRequirements.cs`で定義し、読み込み時に検証する。

### テンプレートの選択
環境検出（`/etc/os-release`と各ツールの`--version`）の結果に対し、`match`の条件（ツール・OS ID/ID_LIKE・最小/上限バージョン）を満たすものから、**OS指定が具体的なもの → minToolVersionが高いもの**を選ぶ。

### 現地での拡張
`/etc/systemagent/templates/`（`CommandTemplates:ExtraPath`）に置いたテンプレートは同梱分に追加され、同じIdなら上書きする。同梱テンプレートの不正は起動失敗、追加テンプレートの不正はエラーログを出して無視する（現地で1ファイル壊れてもサービスは止めない）。

### 安全性
- コマンドはシェルを介さず引数配列で実行する（`ProcessStartInfo.ArgumentList`）。
- コンテナ/イメージ指定は`^[A-Za-z0-9][A-Za-z0-9_.:/@+-]*$`で検証し、`-`始まり（オプション注入）を拒否する。

### APIのエラー表現
| 状況 | HTTP |
|---|---|
| OSコマンドの失敗（コマンドのエラー出力をそのまま返す） | 422 |
| この環境で提供できない（ツール未導入・対応テンプレートなし） | 501 |
| 引数の不正 | 400 |
| コマンドのタイムアウト | 504 |

### コンテナランタイムの選択
設定`Container:Runtime`（`auto`/`podman`/`docker`）。`auto`はPodman優先（[QA 0004](../qa/questions/0004-execution-user.md)で確認中）。

## パッケージングへの申し送り
- .NETの実行に`libicu`が必要。最小構成のRHEL系（WSLのAlmaLinux 9イメージ等）には入っていないため、RPM/DEBの依存関係に含める（DEBはUbuntuのバージョンごとにパッケージ名が異なる: `libicu74`等）。
- イメージ取り込みの一時ファイルは既定で`/var/tmp`（`/tmp`はtmpfsで容量が小さい場合があるため）。
