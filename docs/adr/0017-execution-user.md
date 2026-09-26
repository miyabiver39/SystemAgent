# ADR-017: 実行ユーザーとコンテナの実行形態

## ステータス
決定（[docs/qa/answers/0004-execution-user.md](../qa/answers/0004-execution-user.md)）

## 決定
- SystemAgentは**root**で実行する（systemdのunitファイルでroot起動。unitファイルは発注者側で用意する）。
- 管理対象のコンテナは**rootful**（rootで起動したもの）とする。
- PodmanとDockerの両方がある場合は**Podman優先**（`Container:Runtime=auto`）。Dockerを使う場合は`Container:Runtime=docker`を設定する。

## 影響
- OSコマンドは`sudo`を介さず直接実行する（`CommandExecution:UseSudo`は既定のfalseのまま。将来専用ユーザー化する場合の切り替え口として残す）。
- 秘密情報ファイル・CLIのセッションファイルはroot所有・600。
