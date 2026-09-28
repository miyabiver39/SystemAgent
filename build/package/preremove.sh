#!/bin/sh
# RPM/DEB 共通のアンインストール前処理。非対話で実行される。
# 引数: RPM は残るパッケージ数（0 = 削除、1以上 = アップグレード）、DEB は remove / upgrade / deconfigure / failed-upgrade。
# 削除のときだけ、稼働中の SystemAgent を停止し、自動起動を無効にする（アップグレードでは止めない）。
set -e

case "$1" in
    0|remove|deconfigure) ;;
    *) exit 0 ;;
esac

UNIT=systemagent.service
if command -v systemctl >/dev/null 2>&1 && systemctl cat "$UNIT" >/dev/null 2>&1; then
    echo "SystemAgent を停止し、自動起動を無効にします（$UNIT）。"
    systemctl stop "$UNIT" || true
    systemctl disable "$UNIT" || true
fi

# unit を別名にしている・手動で起動している場合も、削除される実行ファイルのプロセスを残さない
if command -v pkill >/dev/null 2>&1 && pkill -TERM -x SystemAgent.Web 2>/dev/null; then
    echo "実行中の SystemAgent.Web を停止しました。"
fi

exit 0
