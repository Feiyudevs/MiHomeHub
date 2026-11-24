# Build the self-contained MiHome installer (embedded payload, no Inno Setup).
#
# Usage:
#   .\scripts\package-setup.ps1                       # use current csproj version, publish main app too
#   .\scripts\package-setup.ps1 -SkipMainPublish      # main exe already published by release.ps1
#   .\scripts\package-setup.ps1 -Version 1.0.1
#   .\scripts\package-setup.ps1 -Channel test -ExpiresDays 7   # 测试通道清单（不覆盖仓库根 update.json）
#
# Output: publish\release\<version>\MiHome-Setup-<version>-win-x64.exe
param(
    [string]$Rid = "win-x64",
    [string]$Version,
    [switch]$SkipMainPublish,
    [string]$Notes = "",
    [ValidateSet("stable", "test")][string]$Channel = "stable",
    [int]$ExpiresDays = 0
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "MiHome.csproj"
$shellProj = Join-Path $root "installer\MiHome.Setup\MiHome.Setup.csproj"
$publishScript = Join-Path $PSScriptRoot "publish-sc.ps1"
$publishDir = Join-Path $root "publish\sc-$Rid"
$exeName = "MiHome.exe"

function Read-FileText([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try { $bytes = $sha.ComputeHash($stream) }
    finally { $stream.Dispose(); $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace "-", "").ToLowerInvariant()
}

function Read-ProjectVersion([string]$path) {
    $text = Read-FileText $path
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "Cannot find <Version> in $path" }
    return $m.Groups[1].Value.Trim()
}

function Remove-DirWithRetry([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    for ($i = 0; $i -lt 10; $i++) {
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            return
        } catch {
            Start-Sleep -Milliseconds 400
        }
    }
    Write-Warning "Could not remove temp dir: $path"
}

# ---- resolve version ----
$targetVersion = if ($Version) { $Version.Trim().TrimStart([char[]]"vV") } else { Read-ProjectVersion $proj }
if (-not [regex]::IsMatch($targetVersion, '^\d+\.\d+\.\d+$')) {
    throw "Invalid version '$targetVersion' (expected major.minor.patch)"
}

if (-not $SkipMainPublish) {
    & $publishScript -Rid $Rid -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "publish-sc.ps1 failed with exit code $LASTEXITCODE" }
}

$exePath = Join-Path $publishDir $exeName
if (-not (Test-Path -LiteralPath $exePath)) { throw "published exe not found: $exePath" }

# ---- unique staging dirs: a previous run's leftover must never race this run ----
$runId = (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N").Substring(0, 6))
$stageRoot = Join-Path $root "publish\.setup"
$payloadDir = Join-Path $stageRoot "payload-$runId"
$zipPath = Join-Path $stageRoot "payload-$runId.zip"
$shellOut = Join-Path $stageRoot "shell-$runId"
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

# ---- payload: main exe + license/notice files; never bundle stale builds ----
Copy-Item -LiteralPath $exePath -Destination (Join-Path $payloadDir $exeName) -Force

$payloadDocs = @("LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.md")
foreach ($name in $payloadDocs) {
    $src = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $src)) { throw "payload file missing: $src" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $payloadDir $name) -Force
}

# Any extra runtime file in the publish dir (e.g. native dll) is included, except junk/build leftovers.
# MiHome.settings.local.json 是本机私有覆盖（后台地址/app_id），绝不能进安装包。
Get-ChildItem -LiteralPath $publishDir -File | Where-Object {
    $_.Name -ne $exeName -and
    $_.Name -notin $payloadDocs -and
    $_.Name -ne "MiHome.settings.local.json" -and
    $_.Name -notlike "*.old*" -and $_.Name -notlike "*.pre-old" -and
    $_.Name -notlike "MiHome-Setup-*" -and
    $_.Extension -in @(".dll", ".json", ".dat", ".bin")
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $payloadDir $_.Name) -Force
}

$payloadBytes = (Get-ChildItem -LiteralPath $payloadDir -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("Payload: {0} files, {1:N2} MB" -f (Get-ChildItem -LiteralPath $payloadDir -File).Count, ($payloadBytes / 1MB))

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $payloadDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

# ---- build shell with payload embedded (single file, trimmed) ----
if (-not $env:ProgramFiles) { $env:ProgramFiles = "C:\Program Files" }
if (-not $env:ProgramW6432) { $env:ProgramW6432 = "C:\Program Files" }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = "C:\Program Files (x86)" }

$shellVersion = "$targetVersion.0"
& dotnet publish $shellProj -c Release -r $Rid -o $shellOut --nologo `
    -p:PayloadPath=$zipPath -p:Version=$targetVersion -p:FileVersion=$shellVersion
if ($LASTEXITCODE -ne 0) { throw "MiHome.Setup publish failed with exit code $LASTEXITCODE" }

$shellExe = Join-Path $shellOut "MiHome-Setup.exe"
if (-not (Test-Path -LiteralPath $shellExe)) { throw "installer shell not found: $shellExe" }

$releaseDir = Join-Path $root "publish\release\$targetVersion"
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$setupName = "MiHome-Setup-$targetVersion-$Rid.exe"
$setupPath = Join-Path $releaseDir $setupName
Copy-Item -LiteralPath $shellExe -Destination $setupPath -Force

$sizeMb = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
$hash = Get-Sha256 $setupPath
Write-Host ""
Write-Host "Installer: $setupPath ($sizeMb MB)"
Write-Host "SHA256:    $hash"

# ---- update manifest: consumed by the in-app updater ----
# 固定地址：https://github.com/Feiyudevs/MiHomeHub/releases/latest/download/update.json
# 文件名里带版本号，所以 App 必须靠这份清单知道该下载哪个安装包、校验值是多少。
$manifest = [ordered]@{
    version   = $targetVersion
    setup     = $setupName
    sha256    = $hash
    notes     = $Notes
    mandatory = $false
    channel   = $Channel
    mirrors   = @()
}
if ($ExpiresDays -gt 0) {
    $manifest["expiresAt"] = [DateTimeOffset]::UtcNow.AddDays($ExpiresDays).ToString("o")
}
$json = ($manifest | ConvertTo-Json -Depth 4) + "`n"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $releaseDir "update.json"), $json, $utf8)

if ($Channel -eq "stable") {
    # 仓库根再放一份：给 jsDelivr 镜像兜底（GitHub 主站不通时 App 会读它），提交后即生效。
    # 只有正式通道写这里——测试清单绝不能污染镜像，否则稳定用户会读到测试版本。
    [System.IO.File]::WriteAllText((Join-Path $root "update.json"), $json, $utf8)
    Write-Host "Manifest:  $(Join-Path $releaseDir 'update.json') (stable + repo mirror)"
} else {
    Write-Host "Manifest:  $(Join-Path $releaseDir 'update.json') (channel=$Channel, repo mirror untouched)"
}

# ---- cleanup staging (best effort; never fail the build for a locked temp dir) ----
Remove-DirWithRetry $payloadDir
Remove-DirWithRetry $shellOut
Remove-DirWithRetry (Join-Path $stageRoot "obj-$runId")
try { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue } catch { }

$setupPath
