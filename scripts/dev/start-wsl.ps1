# WSLはプロセスが無いとディストリビューションを自動停止する（MariaDBも止まる）ため、
# 開発中は常駐プロセスを置いて起動状態を維持する。停止は `wsl --terminate <name>`。
param([string[]]$Distros = @('sa-alma9', 'sa-ubuntu2404', 'sa-debian'))

foreach ($d in $Distros) {
    Start-Process wsl -ArgumentList @('-d', $d, '--exec', 'sleep', 'infinity') -WindowStyle Hidden
}
