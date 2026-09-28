#!/bin/sh
# RPM/DEB 共通のアンインストール後処理。非対話で実行される。
# 引数: RPM は残るパッケージ数（0 = 削除）、DEB は remove / purge / upgrade 等。
# 秘密情報（ホスト固有鍵）・バックアップは失うと復旧できないため自動では消さず、残っているものと手順を案内する。
set -e

case "$1" in
    0|remove|purge) ;;
    *) exit 0 ;;
esac

if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload >/dev/null 2>&1 || true
fi

cat <<'EOF'
SystemAgent をアンインストールしました。次のものは残しています（不要なら手動で削除してください）。
  /var/lib/systemagent/          秘密情報（master.key・secrets.enc）・DBバックアップ・HAの状態
                                 ※ master.key を消すと secrets.enc は復号できません。再導入する場合は残してください
  /etc/systemagent/              設定ファイル・追加したコマンドテンプレート
  /etc/systemd/system/systemagent.service（作成した場合）
                                 削除後に sudo systemctl daemon-reload を実行してください
  /etc/keepalived/keepalived.conf.systemagent.orig（HAを使った場合の元の設定）
EOF
