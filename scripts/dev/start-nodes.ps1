# 検証用の3ノードをバックグラウンドで起動する（先に dotnet publish で artifacts/linux/web を作っておく）。
#   node-a: sa-alma9      WebUI/API 5000, ノード間 5443
#   node-b: sa-ubuntu2404 WebUI/API 5001, ノード間 5444
#   node-c: sa-debian     WebUI/API 5002, ノード間 5445
# ログは artifacts/logs/<ノード名>.log。停止は stop-nodes.ps1。
$repo = Resolve-Path "$PSScriptRoot/../.."
$logs = Join-Path $repo 'artifacts/logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$script = (wsl -d sa-alma9 -u root --exec wslpath -a ((Join-Path $repo 'scripts/dev/run-node.sh') -replace '\\', '/')).Trim()

$nodes = @(
    @{ Distro = 'sa-alma9'; Name = 'node-a'; Port = 5000; ClusterPort = 5443 },
    @{ Distro = 'sa-ubuntu2404'; Name = 'node-b'; Port = 5001; ClusterPort = 5444 },
    @{ Distro = 'sa-debian'; Name = 'node-c'; Port = 5002; ClusterPort = 5445 }
)
foreach ($n in $nodes) {
    $log = Join-Path $logs "$($n.Name).log"
    Start-Process wsl -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err" `
        -ArgumentList @('-d', $n.Distro, '-u', 'root', '--exec', 'sh', $script, $n.Name, $n.Port, $n.ClusterPort)
}

foreach ($n in $nodes) {
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest "http://localhost:$($n.Port)/api/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep 1 }
    }
    Write-Host "$($n.Name) : http://localhost:$($n.Port)"
}
