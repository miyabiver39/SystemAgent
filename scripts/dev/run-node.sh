#!/bin/sh
# WSLのディストリビューション内で、publish済みのSystemAgentを検証用ノードとして起動する（rootで実行）。
# WSLのディストリビューションはネットワークを共有するため、ノードごとにポートを分け、接続先は127.0.0.1にする。
#   使い方: run-node.sh <ノード名> <WebUI/APIポート> <ノード間通信ポート>
#   例:     wsl -d sa-alma9 -u root --exec sh scripts/dev/run-node.sh node-a 5000 5443
set -eu
NAME=$1
PORT=$2
CLUSTER_PORT=$3
REPO=$(cd "$(dirname "$0")/../.." && pwd)

cd "$REPO/artifacts/linux/web"
# SYSTEMAGENT_WAIT_FOR_DEBUGGER=1 を付けて起動すると、デバッガがアタッチされるまで待つ（VS Code用）
exec env \
  SYSTEMAGENT_WAIT_FOR_DEBUGGER="${SYSTEMAGENT_WAIT_FOR_DEBUGGER:-0}" \
  Urls="http://0.0.0.0:$PORT" \
  Cluster__NodeName="$NAME" \
  Cluster__Port="$CLUSTER_PORT" \
  Cluster__AdvertiseAddress=127.0.0.1 \
  ConnectionStrings__Default="Server=localhost;Port=3306;Database=systemagent;User=systemagent;Password=systemagent_dev;Connection Timeout=5;" \
  ./SystemAgent.Web
