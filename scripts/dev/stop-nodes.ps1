# run-node.sh で起動した検証用ノードを停止する。
# Windows側でwsl.exeを止めてもディストリビューション内のプロセスは残るため、中でプロセス名を完全一致で停止する。
param([string[]]$Distros = @('sa-alma9', 'sa-ubuntu2404', 'sa-debian'))

foreach ($d in $Distros) {
    wsl -d $d -u root --exec pkill -x SystemAgent.Web
}
