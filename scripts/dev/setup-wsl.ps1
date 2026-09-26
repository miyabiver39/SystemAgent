# 開発・検証用WSL2環境のセットアップ（ADR-014）。冪等に再実行可能。
#   sa-alma9      : RHEL系検証用 (AlmaLinux 9) + MariaDB（開発用DB）
#   sa-ubuntu2404 : Debian/Ubuntu系検証用 (Ubuntu 24.04)
# 既存の個人用ディストリビューションには触れない。
$ErrorActionPreference = 'Stop'
$env:WSL_UTF8 = 1

function Invoke-WslRoot([string]$Distro, [string]$Script) {
    # 標準入力で渡すとPowerShellが末尾にCRLFを付加しbashが誤動作するため、base64で渡す
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Script -replace "`r", "")))
    wsl -d $Distro -u root -- bash -c "echo $b64 | base64 -d | bash"
    if ($LASTEXITCODE -ne 0) { throw "$Distro でのスクリプト実行に失敗しました (rc=$LASTEXITCODE)" }
}

$distros = @{ 'sa-alma9' = 'AlmaLinux-9'; 'sa-ubuntu2404' = 'Ubuntu-24.04' }
$installed = (wsl -l -q) -replace "`0", "" | Where-Object { $_ }

foreach ($name in $distros.Keys) {
    if ($installed -notcontains $name) {
        wsl --install $distros[$name] --name $name --no-launch
    }
    Invoke-WslRoot $name "printf '[boot]\nsystemd=true\n' > /etc/wsl.conf"
    wsl --terminate $name | Out-Null
}

Invoke-WslRoot 'sa-alma9' @'
set -e
# libicu: .NETのグローバリゼーションに必要（最小構成のイメージには入っていない）
dnf -y -q install podman chrony iproute NetworkManager procps-ng mariadb-server libicu
systemctl enable --now mariadb
mysql <<'SQL'
CREATE DATABASE IF NOT EXISTS systemagent CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'systemagent'@'%' IDENTIFIED BY 'systemagent_dev';
GRANT ALL ON systemagent.* TO 'systemagent'@'%';
FLUSH PRIVILEGES;
SQL
'@

Invoke-WslRoot 'sa-ubuntu2404' @'
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq podman docker.io chrony iproute2 libicu74
'@

Write-Host "セットアップ完了。開発中は scripts/dev/start-wsl.ps1 でディストリビューションを起動維持してください。"
