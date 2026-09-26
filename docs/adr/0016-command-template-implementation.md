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

### settings とコマンド単位の実行ファイル
- コマンド以外の差分（設定ファイルのパス、サービス名、設定の書き換え方式など）は`settings`に置く。`settings`の値はコマンドのプレースホルダとしても使える（例: `systemctl restart {service}`）。
- コマンドごとに`executable`を上書きできる（例: NTPテンプレートの実行ファイルは`chronyc`だが、再起動だけ`systemctl`）。

### 設定ファイルを書き換える機能の共通方針（NTPで確立）
1. 初回のみ元ファイルを`<ファイル>.systemagent.orig`に退避する。
2. 一時ファイルに書いてから置き換える（原子的な更新）。
3. サービスを再起動し、**失敗したら元の内容に戻して再起動し直す**。
4. 既存の設定行は削除せずコメントアウトする（`#SystemAgent# `）。SystemAgentが書く部分はマーカーで囲んだ管理ブロックにまとめ、何度適用しても同じ結果になるようにする。
5. 書き換え方式はテンプレートの`settings.configStyle`で選ぶ名前付きの純粋関数（入力テキスト→出力テキスト）とし、実機の設定ファイルを使ってテストする。

| capability | テンプレート | 検証環境 |
|---|---|---|
| container-runtime | podman, docker | Podman 5.8 (AlmaLinux 9)、Podman 4.9 / Docker 29.1 (Ubuntu 24.04) |
| ntp | chrony-rhel, chrony-debian, timesyncd | chrony 4.8 (AlmaLinux 9)、chrony 4.5 (Ubuntu 24.04)、systemd-timesyncd 257 (Debian 13) |

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
