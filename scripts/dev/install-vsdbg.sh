#!/bin/sh
# VS Code からWSL上のSystemAgentにアタッチするためのデバッガ（Microsoft vsdbg）を /root/vsdbg に入れる（rootで実行）。
#   例: wsl -d sa-alma9 -u root --exec sh scripts/dev/install-vsdbg.sh
set -eu
if [ -x /root/vsdbg/vsdbg ]; then echo "vsdbg はインストール済みです: /root/vsdbg"; exit 0; fi
command -v unzip >/dev/null || { command -v dnf >/dev/null && dnf -y -q install unzip || apt-get install -y -qq unzip; }
curl -sSL https://aka.ms/getvsdbgsh | sh /dev/stdin -v latest -l /root/vsdbg
echo "vsdbg をインストールしました: /root/vsdbg"
