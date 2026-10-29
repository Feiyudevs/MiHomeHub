# One-click Release build: compile -> publish single-file exe -> launch
# Usage: .\scripts\build.ps1          (compile + publish + launch)
#        .\scripts\build.ps1 -NoRun   (compile only)
param(
    [switch]$NoRun
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "MiHome.csproj"
$dll = Join-Path $root "bin\Release\net10.0\win-x64\MiHome.dll"
$publishScript = Join-Path $PSScriptRoot "publish-sc.ps1"
$publishDir = Join-Path $root "publish\sc-win-x64"
$exePath = Join-Path $publishDir "MiHome.exe"

# Fix NuGet restore: path1 cannot be null
if (-not $env:ProgramFiles) { $env:ProgramFiles = "C:\Program Files" }
if (-not $env:ProgramW6432) { $env:ProgramW6432 = "C:\Program Files" }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = "C:\Program Files (x86)" }

dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $dll)) { throw "build output not found: $dll" }
Write-Host "OK -> $dll"

if ($NoRun) {
    Write-Host "Skipped publish/launch (-NoRun)."
    return
}

# publish-sc.ps1 overwrites MiHome.exe, so a running tray instance locks it.
# 杀本仓库 publish\ 下全部实例（含 release\1.0.0 等旧拷贝），避免点到旧界面。
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $root "publish"))
$running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -ieq "MiHome.exe" -and $_.ExecutablePath
})
$toStop = @()
foreach ($proc in $running) {
    $path = [System.IO.Path]::GetFullPath($proc.ExecutablePath)
    if ($path.StartsWith($publishRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $toStop += $proc
    }
}
if ($toStop.Count -gt 0) {
    $ids = ($toStop | ForEach-Object { $_.ProcessId }) -join ", "
    Write-Host "Stopping running instance(s): $ids"
    foreach ($proc in $toStop) { Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
}

& $publishScript
if ($LASTEXITCODE -ne 0) { throw "publish-sc.ps1 failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $exePath)) { throw "published exe not found: $exePath" }

# 托盘常驻应用：交给 explorer 启动，进程归属桌面 shell，避免被当前命令会话回收
Start-Process explorer.exe -ArgumentList ('"{0}"' -f $exePath)
Write-Host "Launched -> $exePath"
