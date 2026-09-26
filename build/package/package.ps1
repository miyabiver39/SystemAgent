# RPM/DEBパッケージを作成する（ADR-006）。出力: artifacts/package/*.rpm, *.deb
#   ./build/package/package.ps1 -Version 0.1.0
# 必要: .NET SDK、nfpm（go install github.com/goreleaser/nfpm/v2/cmd/nfpm@latest）
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path "$PSScriptRoot/../.."
Push-Location $repo
try {
    $out = 'artifacts/package'
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

    $common = @('-c', 'Release', '-r', 'linux-x64', '--self-contained', "-p:Version=$Version", '-p:DebugType=None')
    dotnet publish src/SystemAgent.Web @common -o "$out/web"
    if ($LASTEXITCODE -ne 0) { throw 'Web の publish に失敗しました' }
    dotnet publish src/SystemAgent.Cli @common -p:PublishSingleFile=true -o "$out/cli"
    if ($LASTEXITCODE -ne 0) { throw 'CLI の publish に失敗しました' }
    # 開発用の設定はパッケージに含めない
    Remove-Item "$out/web/appsettings.Development.json" -ErrorAction SilentlyContinue
    # 実行ファイルは実行権限(0755)を付けるため、ディレクトリ一式(0644)とは分けて同梱する
    New-Item -ItemType Directory -Force "$out/web-exec" | Out-Null
    foreach ($exe in 'SystemAgent.Web', 'createdump') { Move-Item "$out/web/$exe" "$out/web-exec/$exe" }

    $nfpm = (Get-Command nfpm -ErrorAction SilentlyContinue)?.Source ?? (Join-Path (go env GOPATH) 'bin/nfpm.exe')
    $env:SYSTEMAGENT_VERSION = $Version
    foreach ($packager in 'rpm', 'deb') {
        & $nfpm package --config build/package/nfpm.yaml --packager $packager --target $out
        if ($LASTEXITCODE -ne 0) { throw "nfpm ($packager) に失敗しました" }
    }
    Get-ChildItem $out -File | Where-Object Extension -in '.rpm', '.deb' | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally {
    Pop-Location
}
