#!/bin/sh
# RPM/DEB 共通のインストール後処理。非対話で実行される。
# 秘密情報（ホスト固有鍵・JWT署名鍵・初期セットアップトークン）は初回起動時にSystemAgentが生成する（ADR-003, ADR-015）。
set -e

chmod 700 /var/lib/systemagent
chmod 640 /etc/systemagent/systemagent.json

cat <<'EOF'
SystemAgent をインストールしました。
  1. systemd の unit ファイルで起動してください（root で実行）。例:
       sudo cp /usr/share/doc/systemagent/systemagent.service.example /etc/systemd/system/systemagent.service
       sudo systemctl daemon-reload && sudo systemctl enable --now systemagent
  2. 初期セットアップ（緊急認証の管理者作成）:   sudo systemagent setup
  3. 緊急ログイン:                               sudo systemagent login --emergency -u admin
  4. DB接続設定（テーブルも作成されます）:       sudo systemagent db set --server <DB/VIP>
  5. クラスタ: 最初の1台は systemagent cluster init <名前>、2台目以降は systemagent join <トークン>
設定ファイル: /etc/systemagent/systemagent.json（WebUI/APIポート、ノード間通信ポート等）
EOF
