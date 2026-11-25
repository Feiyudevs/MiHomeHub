# Self-contained single-file publish (distribution size follows this script)
# Usage: .\scripts\publish-sc.ps1 [-Rid win-x64]
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "MiHomeHub.csproj"
$out = Join-Path $root "publish\sc-$Rid"

# Fix NuGet restore: path1 cannot be null
if (-not $env:ProgramFiles) { $env:ProgramFiles = "C:\Program Files" }
if (-not $env:ProgramW6432) { $env:ProgramW6432 = "C:\Program Files" }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = "C:\Program Files (x86)" }

dotnet publish $proj `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) {
    throw "publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $out "MiHomeHub.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "published exe not found: $exe"
}

# 变量字段文件（后台地址/app_id 等）：跟着 exe 走，改配置不用重编代码。
# 单文件 publish 偶尔不会带 Content，这里显式复制，保证 publish 目录始终有它。
foreach ($name in @("MiHomeHub.settings.json", "MiHomeHub.settings.local.json")) {
    $src = Join-Path $root $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $out $name) -Force
    }
}

$mb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 2)
$dirMb = [math]::Round((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "OK -> $exe ($mb MB exe / $dirMb MB dir)"
