# Publish the packaged release to GitHub: push tag v<version> + create GitHub Release with artifacts
#
# Usage:
#   .\scripts\release-github.ps1                        # use version from csproj
#   .\scripts\release-github.ps1 -Version 1.2.0
#   .\scripts\release-github.ps1 -Draft                 # create as draft, publish later
#   .\scripts\release-github.ps1 -Notes "修复了xxx"
#   .\scripts\release-github.ps1 -NotesFile changelog.md
#   .\scripts\release-github.ps1 -Channel test          # 发到滚动 tag test（pre-release，正式通道看不到）
#
# Prereqs:
#   - artifacts already built: publish\release\<version>\ (via .\package.cmd [-Channel test])
#   - GitHub CLI installed + logged in:  winget install GitHub.cli ; gh auth login
param(
    [string]$Version,
    [switch]$Draft,
    [string]$Notes,
    [string]$NotesFile,
    [ValidateSet("stable", "test")][string]$Channel = "stable"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "MiHome.csproj"

function Read-ProjectVersion([string]$path) {
    $text = [System.IO.File]::ReadAllText($path)
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "Cannot find <Version> in $path" }
    return $m.Groups[1].Value.Trim()
}

# ---- resolve gh ----
$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    throw ("GitHub CLI not found. Install it with: winget install --id GitHub.cli -e  " +
        "then run: gh auth login")
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI not logged in. Run: gh auth login" }

# ---- resolve version ----
if ($Version) {
    $targetVersion = $Version.Trim().TrimStart([char[]]"vV")
    if (-not [regex]::IsMatch($targetVersion, '^\d+\.\d+\.\d+$')) {
        throw "Invalid -Version '$Version' (expected major.minor.patch)"
    }
} else {
    $targetVersion = Read-ProjectVersion $proj
}
$tag = if ($Channel -eq "test") { "test" } else { "v$targetVersion" }

# ---- collect artifacts ----
$releaseDir = Join-Path $root "publish\release\$targetVersion"
if (-not (Test-Path -LiteralPath $releaseDir)) {
    throw ("Release dir not found: $releaseDir`nBuild it first with: .\package.cmd -Version $targetVersion")
}

$exe = Get-ChildItem -LiteralPath $releaseDir -Filter "MiHome.exe" -File -ErrorAction SilentlyContinue
$setups = @(Get-ChildItem -LiteralPath $releaseDir -Filter "MiHome-Setup-$targetVersion-*.exe" -File -ErrorAction SilentlyContinue)
$sums = Get-ChildItem -LiteralPath $releaseDir -Filter "SHA256SUMS.txt" -File -ErrorAction SilentlyContinue
$manifest = Get-ChildItem -LiteralPath $releaseDir -Filter "update.json" -File -ErrorAction SilentlyContinue

$artifacts = @()
if ($exe) { $artifacts += $exe.FullName }
$artifacts += ($setups | ForEach-Object { $_.FullName })
if ($sums) { $artifacts += $sums.FullName }
# App 的更新源就是这个固定地址：releases/latest/download/update.json，漏传等于自动更新失效
if (-not $manifest) { throw "update.json missing in $releaseDir (rebuild with .\package.cmd)" }
$artifacts += $manifest.FullName
if ($artifacts.Count -eq 0) {
    throw "No artifacts (MiHome.exe / MiHome-Setup-$targetVersion-*.exe / SHA256SUMS.txt) in $releaseDir"
}

Write-Host "Release $tag -> $releaseDir"
foreach ($file in $artifacts) {
    $mb = [math]::Round((Get-Item -LiteralPath $file).Length / 1MB, 2)
    Write-Host ("  {0}  ({1} MB)" -f (Split-Path -Leaf $file), $mb)
}

# ---- 清单通道必须和发布通道一致，否则装上后 App 会拒绝这份清单 ----
$manifestJson = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json
$manifestChannel = if ($manifestJson.channel) { [string]$manifestJson.channel } else { "stable" }
if ($manifestChannel -ne $Channel) {
    throw ("update.json 是 '$manifestChannel' 清单，与 -Channel $Channel 不符；" +
        "先重新打包：.\package.cmd -Channel $Channel（版本号相同时加 -Version 覆盖）")
}

$releaseArgs = @()
if ($Notes) { $releaseArgs += "--notes", $Notes }
elseif ($NotesFile) {
    if (-not (Test-Path -LiteralPath $NotesFile)) { throw "Notes file not found: $NotesFile" }
    $releaseArgs += "--notes-file", $NotesFile
}

if ($Channel -eq "test") {
    # ── 测试通道：固定的滚动 tag 'test'，Release 标 pre-release（releases/latest 永远选不到它）
    # 重复发版只替换资产，不改 tag、不重建 Release：download/test/<file> 这个地址永远有效。
    & gh release view $tag *> $null
    if ($LASTEXITCODE -ne 0) {
        $localTag = git tag --list $tag
        if (-not $localTag) {
            $dirty = git status --porcelain
            if ($dirty) { Write-Warning "Working tree has uncommitted changes; the tag will point at the last commit." }
            git tag $tag
        }
        git push origin $tag
        if ($LASTEXITCODE -ne 0) { throw "git push failed for tag $tag" }

        $createArgs = @("release", "create", $tag) + $artifacts +
            @("--title", "MiHome 测试通道（内部验证）", "--prerelease") +
            $(if ($releaseArgs.Count -gt 0) { $releaseArgs } else { @("--notes", "内部测试版本，正式通道不会分发。") })
        & gh @createArgs
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed for test channel (exit $LASTEXITCODE)" }
    } else {
        & gh release upload $tag @artifacts --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for test channel (exit $LASTEXITCODE)" }
        # 防御：任何情况下测试 Release 都不能变成正式 Release
        & gh release edit $tag --prerelease
        if ($LASTEXITCODE -ne 0) { throw "gh release edit --prerelease failed (exit $LASTEXITCODE)" }
        if ($releaseArgs.Count -gt 0) { & gh release edit $tag @releaseArgs }
    }
} else {
    # ---- guard: tag must not already exist ----
    $localTag = git tag --list $tag
    if ($localTag) { throw "Tag '$tag' already exists locally. Bump the version or delete it first." }
    git ls-remote --tags origin "refs/tags/$tag" | Out-Null
    if ($LASTEXITCODE -eq 0 -and (git ls-remote --tags origin "refs/tags/$tag")) {
        throw "Tag '$tag' already exists on origin. Bump the version or delete it first."
    }

    # ---- push tag (requires a clean, committed tree) ----
    $dirty = git status --porcelain
    if ($dirty) { Write-Warning "Working tree has uncommitted changes; the tag will point at the last commit." }
    git tag $tag
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "git push failed for tag $tag" }
    Write-Host "Tag pushed: $tag"

    # ---- create GitHub release ----
    $ghArgs = @("release", "create", $tag)
    $ghArgs += $artifacts
    $ghArgs += "--title", $tag
    if ($releaseArgs.Count -gt 0) { $ghArgs += $releaseArgs }
    else { $ghArgs += "--generate-notes" }
    if ($Draft) { $ghArgs += "--draft" }

    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE). Tag $tag is already pushed; fix the issue and re-run only the release step." }
}

Write-Host ""
Write-Host "Done. Release page:"
& gh release view $tag --json url -q .url
