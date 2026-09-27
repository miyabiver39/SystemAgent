# WSL の検証用ノード向けに Linux 版を発行し、CLI を各ディストリビューションの /usr/bin/systemagent に入れる。
#   ./scripts/dev/publish-linux.ps1                  # Release
#   ./scripts/dev/publish-linux.ps1 -Configuration Debug   # VS Code でアタッチしてデバッグする場合
# 発行先: artifacts/linux/web（run-node.sh が使う）、artifacts/linux/cli
# 実行中のノードは先に stop-nodes.ps1 で止めておくこと（実行中のファイルは上書きできない）。
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string[]]$Distros = @('sa-alma9', 'sa-ubuntu2404', 'sa-debian')
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot/../.."

dotnet publish "$repo/src/SystemAgent.Web" -c $Configuration -r linux-x64 --self-contained -o "$repo/artifacts/linux/web" -v q
if ($LASTEXITCODE -ne 0) { throw "SystemAgent.Web の発行に失敗しました。" }
dotnet publish "$repo/src/SystemAgent.Cli" -c $Configuration -r linux-x64 --self-contained -p:PublishSingleFile=true -o "$repo/artifacts/linux/cli" -v q
if ($LASTEXITCODE -ne 0) { throw "CLI の発行に失敗しました。" }

$cli = (wsl -d $Distros[0] -u root --exec wslpath -a (("$repo/artifacts/linux/cli/systemagent") -replace '\\', '/')).Trim()
foreach ($d in $Distros) {
    wsl -d $d -u root --exec install -m 0755 -o root -g root $cli /usr/bin/systemagent
}
Write-Host "発行しました（$Configuration）。ノードの起動は scripts/dev/start-nodes.ps1"
