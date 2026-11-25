# Preview the install / uninstall UI without touching the system.
#
# Usage:
#   .\scripts\preview-setup.ps1              # opens install + uninstall previews
#   .\scripts\preview-setup.ps1 install
#   .\scripts\preview-setup.ps1 update       # install UI in "update" state
#   .\scripts\preview-setup.ps1 reinstall
#   .\scripts\preview-setup.ps1 uninstall
#   .\scripts\preview-setup.ps1 error        # simulated failure state
param(
    [ValidateSet("install", "update", "reinstall", "uninstall", "error")]
    [string]$Mode
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$setup = Get-ChildItem -LiteralPath (Join-Path $root "publish\release") -Filter "MiHomeHub-Setup-*.exe" -File -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $setup) {
    throw "没有找到安装包。请先运行 .\package.cmd 打包。"
}

$modes = if ($Mode) { @($Mode) } else { @("install", "uninstall") }
foreach ($item in $modes) {
    Start-Process -FilePath $setup.FullName -ArgumentList @("--preview", $item)
    Write-Host "preview: $item"
}
