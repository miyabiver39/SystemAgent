# ADR-014: 開発・検証環境（WSL2）とKeepalivedのモック検証

## ステータス
決定（[docs/qa/answers/0002-dev-test-environment.md](../qa/answers/0002-dev-test-environment.md) A1）

## コンテキスト
Capability Provider実装をRHEL系・Debian/Ubuntu系の実環境で検証する必要がある。開発機はWindowsのみで、AIエージェント(Claude Code)が人手を介さずに検証を回せることが望まれた。

## 決定
- WSL2上に検証専用ディストリビューションを作成し、AIエージェントは`wsl -d <name> -u root -- ...`で直接コマンドを実行して検証する。
  - `sa-alma9`: AlmaLinux 9（RHEL9互換。Rocky LinuxはWSL公式配布が無いため代替）。開発用MariaDB(10.5)もここに置く。
  - `sa-ubuntu2404`: Ubuntu 24.04（Podman/Docker両方導入）
  - いずれもsystemd有効。利用者個人の既存ディストリビューションには手を加えない。
- 構築は[scripts/dev/setup-wsl.ps1](../../scripts/dev/setup-wsl.ps1)（冪等）、起動維持は[scripts/dev/start-wsl.ps1](../../scripts/dev/start-wsl.ps1)。
- WindowsからWSL上のMariaDBへは`localhost:3306`（WSLのlocalhost転送）で接続する。
- **Keepalived（VIP切替・フェイルオーバー）はWSL上で検証しない。** `IKeepalivedProvider`の背後にフェイク実装を用意し、VIP取得/喪失イベントを擬似的に発生させて昇格API・自動/手動復帰フローを検証する（発注者提案）。実機での検証は将来別途行う。

## 理由
- WSL2は既に有効化済みで、追加の仮想化基盤が不要。
- AIエージェントがSSH等を介さずに直接コマンドを実行でき、検証サイクルが短い。
- Keepalived/VIPはWSL2のNATネットワーク上では再現が難しく、ロジック（昇格・復帰判定）の検証はモックで十分。

## 既知の注意点
- WSLはプロセスが無いとディストリビューションを自動停止する（MariaDBも停止する）。開発中は`start-wsl.ps1`を実行しておく。
- 開発用DBの資格情報（`systemagent_dev`）は`appsettings.Development.json`にのみ記載する、WSL内ローカル専用のテスト値である。
- EOL版OS（CentOS 7等）の検証は、WSL公式イメージがあるもの（Oracle Linux 7.9等）で必要時に追加する。
