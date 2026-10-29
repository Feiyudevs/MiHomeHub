# One-click release: sync version -> publish single-file exe -> build Inno Setup installer -> collect artifacts
#
# Usage:
#   .\scripts\release.ps1                     # package current version from csproj
#   .\scripts\release.ps1 -Bump patch         # 1.0.0 -> 1.0.1, then package
#   .\scripts\release.ps1 -Bump minor
#   .\scripts\release.ps1 -Version 1.2.0
#   .\scripts\release.ps1 -SkipInstaller      # publish only, no Inno Setup
#
# Output: publish\release\<version>\ (exe + Setup + SHA256SUMS.txt)
param(
    [string]$Version,
    [ValidateSet("none", "patch", "minor", "major")][string]$Bump = "none",
    [string]$Rid = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "MiHome.csproj"
$iss = Join-Path $root "installer\MiHome.iss"
$publishScript = Join-Path $PSScriptRoot "publish-sc.ps1"
$publishDir = Join-Path $root "publish\sc-$Rid"
$exeName = "MiHome.exe"

function Read-FileText([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}

function Write-FileText([string]$path, [string]$text) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $encoding = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($path, $text, $encoding)
}

function Read-ProjectVersion([string]$path) {
    $text = Read-FileText $path
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "Cannot find <Version> in $path" }
    return $m.Groups[1].Value.Trim()
}

function Step-Version([string]$current, [string]$kind) {
    $parts = $current.Split('.')
    if ($parts.Count -ne 3) { throw "Version must be major.minor.patch, got '$current'" }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    switch ($kind) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    return "$major.$minor.$patch"
}

function Test-VersionFormat([string]$value) {
    return [regex]::IsMatch($value, '^\d+\.\d+\.\d+$')
}

function Update-ProjectVersion([string]$path, [string]$version) {
    $text = Read-FileText $path
    $assembly = "$version.0"
    $updated = $text
    $updated = [regex]::Replace($updated, '<Version>[^<]*</Version>', "<Version>$version</Version>")
    $updated = [regex]::Replace($updated, '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$assembly</AssemblyVersion>")
    $updated = [regex]::Replace($updated, '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$assembly</FileVersion>")
    $updated = [regex]::Replace($updated, '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$version</InformationalVersion>")
    if ($updated -ne $text) {
        Write-FileText $path $updated
        Write-Host "csproj version -> $version"
    }
}

function Update-InstallerVersion([string]$path, [string]$version) {
    $text = Read-FileText $path
    $pattern = '(?m)^(\s*#define\s+MyAppVersion\s+")[^"]*(")'
    $updated = [regex]::Replace($text, $pattern, {
        param($m)
        return $m.Groups[1].Value + $version + $m.Groups[2].Value
    })
    if ($updated -ne $text) {
        Write-FileText $path $updated
        Write-Host "iss version -> $version"
    }
}

function Resolve-Iscc {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe") }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe") }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe") }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Stop-PublishInstance([string]$exePath) {
    $target = [System.IO.Path]::GetFullPath($exePath)
    $publishRoot = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent (Split-Path -Parent $exePath)) ""))
    # 同时杀 sc-win-x64 与 release\<ver>\ 下的 MiHome.exe：后者可能是硬链接残留实例，会锁住归档文件
    $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and $(
            $p = [System.IO.Path]::GetFullPath($_.ExecutablePath)
            ($p -ieq $target) -or
            ($_.Name -ieq "MiHome.exe" -and $p.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase))
        )
    })
    if ($running.Count -eq 0) { return }

    $ids = ($running | ForEach-Object { $_.ProcessId }) -join ", "
    Write-Host "Stopping running instance(s): $ids (tray resident; force stop)"
    foreach ($proc in $running) { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop }
    Start-Sleep -Milliseconds 500

    $stillRunning = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and ([System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $target)
    })
    if ($stillRunning.Count -gt 0) { throw "Failed to stop running instance(s): $exePath" }
}

# ---- resolve version ----
$currentVersion = Read-ProjectVersion $proj
if ($Version) {
    if ($Bump -ne "none") { throw "Use either -Version or -Bump, not both." }
    $targetVersion = $Version.Trim().TrimStart([char[]]"vV")
    if (-not (Test-VersionFormat $targetVersion)) { throw "Invalid -Version '$Version' (expected major.minor.patch)" }
} elseif ($Bump -ne "none") {
    $targetVersion = Step-Version $currentVersion $Bump
} else {
    $targetVersion = $currentVersion
}

Write-Host "Release version: $targetVersion (was $currentVersion)"

Update-ProjectVersion $proj $targetVersion
Update-InstallerVersion $iss $targetVersion

# ---- publish single-file exe ----
$exePath = Join-Path $publishDir $exeName
Stop-PublishInstance $exePath

& $publishScript -Rid $Rid -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "publish-sc.ps1 failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $exePath)) { throw "published exe not found: $exePath" }

$staleExes = @(Get-ChildItem -LiteralPath $publishDir -Filter *.exe -File | Where-Object { $_.Name -ne $exeName })
if ($staleExes.Count -gt 0) {
    Write-Warning ("Extra exe files in publish dir will be bundled into the installer: " +
        (($staleExes | ForEach-Object { $_.Name }) -join ", "))
}

# ---- release dir (ISCC writes the Setup straight into it) ----
$releaseDir = Join-Path $root "publish\release\$targetVersion"
if (-not (Test-Path -LiteralPath $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
}

# ---- build installer ----
$setupPath = $null
if (-not $SkipInstaller) {
    $iscc = Resolve-Iscc
    if (-not $iscc) {
        throw ("Inno Setup compiler (ISCC.exe) not found. Install it with: " +
            "winget install --id JRSoftware.InnoSetup -e   (or rerun with -SkipInstaller)")
    }

    Write-Host "ISCC: $iscc"
    & $iscc $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

    $setups = @(Get-ChildItem -LiteralPath $releaseDir -Filter "MiHome-Setup-$targetVersion-*.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($setups.Count -eq 0) { throw "installer not found in $releaseDir" }
    $setupPath = $setups[0].FullName
} else {
    # -SkipInstaller: drop stale Setup so the version dir matches SHA256SUMS
    Get-ChildItem -LiteralPath $releaseDir -Filter "MiHome-Setup-*.exe" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# ---- portable exe: hardlink (same volume) so it costs no extra disk ----
$exeCopy = Join-Path $releaseDir $exeName
if (Test-Path -LiteralPath $exeCopy) { Remove-Item -LiteralPath $exeCopy -Force }
try {
    New-Item -ItemType HardLink -Path $exeCopy -Target $exePath -ErrorAction Stop | Out-Null
    Write-Host "exe hardlinked -> $exeCopy"
} catch {
    Copy-Item -LiteralPath $exePath -Destination $exeCopy -Force
    Write-Host "exe copied -> $exeCopy"
}

$artifacts = @($exeCopy)
if ($setupPath) { $artifacts += $setupPath }

$hashLines = @()
foreach ($file in $artifacts) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
    $hashLines += ("{0}  {1}" -f $hash, (Split-Path -Leaf $file))
}
[System.IO.File]::WriteAllLines(
    (Join-Path $releaseDir "SHA256SUMS.txt"),
    [string[]]$hashLines,
    (New-Object System.Text.UTF8Encoding($false))
)

# ---- summary ----
Write-Host ""
Write-Host "Release $targetVersion -> $releaseDir"
foreach ($file in $artifacts) {
    $mb = [math]::Round((Get-Item -LiteralPath $file).Length / 1MB, 2)
    Write-Host ("  {0}  ({1} MB)" -f (Split-Path -Leaf $file), $mb)
}
Write-Host "  SHA256SUMS.txt"
if (-not $SkipInstaller) {
    Write-Host ""
    Write-Host "Next: upload the Setup to your distribution channel, then release/gray it."
}
