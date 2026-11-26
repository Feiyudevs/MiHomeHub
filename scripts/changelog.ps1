# 变更说明流水线：累积记录 -> 打包带入更新说明 -> 发版整段封存
#
# Usage:
#   .\scripts\changelog.ps1                             # 查看未发布条目与状态
#   .\scripts\changelog.ps1 -Check                      # 有代码改动却没写说明时报错（发版前拦截）
#   .\scripts\changelog.ps1 -Check -AllowMissing        # 同上，但只警告不中断
#   .\scripts\changelog.ps1 -Notes                      # 输出纯文本说明（给 update.json 的 notes）
#   .\scripts\changelog.ps1 -Notes -Format md -OutFile notes.md   # 输出 markdown（给 GitHub Release）
#   .\scripts\changelog.ps1 -Seal -Version 1.0.3        # 把未发布条目整段封存进 changelog/released.md 并清空
#   .\scripts\changelog.ps1 -Seal -Bump patch -DryRun   # 只看封存结果，不写文件
#   .\scripts\changelog.ps1 -Draft                      # 用上个 tag 以来的 git log 生成待润色草稿
#
# 约定：
#   changelog\unreleased.md 是唯一手工 / AI 维护的文件，四个分段：新增 / 优化 / 修复 / 内部。
#   「新增/优化/修复」对外（更新弹窗 + GitHub Release），「内部」只进 changelog/released.md。
param(
    [switch]$Check,
    [switch]$Notes,
    [switch]$Seal,
    [switch]$Draft,
    [string]$Version,
    [ValidateSet("none", "patch", "minor", "major")][string]$Bump = "none",
    [string]$Date,
    [string]$OutFile,
    [ValidateSet("text", "md")][string]$Format = "text",
    [switch]$DryRun,
    [switch]$AllowMissing
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$archivePath = Join-Path $root "changelog\released.md"
$unreleasedPath = Join-Path $root "changelog\unreleased.md"
$proj = Join-Path $root "MiHomeHub.csproj"

$Sections = @("新增", "优化", "修复", "内部")
$PublicSections = @("新增", "优化", "修复")
$anchor = "<!-- new-release-here -->"

function Read-Text([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "文件不存在: $path" }
    return [System.IO.File]::ReadAllText($path)
}

function Write-Text([string]$path, [string]$text) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $hasBom = $false
    if (Test-Path -LiteralPath $path) {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    }
    $encoding = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($path, $text, $encoding)
}

function Detect-NewLine([string]$text) {
    if ($text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

# 解析：支持 "## 段落"（未发布区）与 "### 段落"（已封存区），取 "- xxx" 条目，跳过 HTML 注释。
function Parse-Entries([string]$text) {
    $entries = @{}
    foreach ($s in $Sections) { $entries[$s] = New-Object System.Collections.ArrayList }
    $current = $null
    $inComment = $false
    foreach ($raw in ($text -split "`r?`n")) {
        $line = $raw.Trim()
        if ($inComment) {
            if ($line.Contains("-->")) { $inComment = $false }
            continue
        }
        if ($line.StartsWith("<!--")) {
            if (-not $line.Contains("-->")) { $inComment = $true }
            continue
        }
        if ($line -match "^#{2,3}\s+(.+)$") {
            $name = $Matches[1].Trim()
            if ($Sections -contains $name) { $current = $name } else { $current = $null }
            continue
        }
        if ($line -match "^#\s") { $current = $null; continue }
        if ($null -ne $current -and ($line -match "^[*-]\s+(.+)$")) {
            $body = $Matches[1].Trim()
            if ($body) { [void]$entries[$current].Add($body) }
        }
    }
    return $entries
}

function Get-TotalCount($entries) {
    $n = 0
    foreach ($s in $Sections) { $n += $entries[$s].Count }
    return $n
}

function Get-PublicCount($entries) {
    $n = 0
    foreach ($s in $PublicSections) { $n += $entries[$s].Count }
    return $n
}

function Read-ProjectVersion([string]$path) {
    $text = Read-Text $path
    $m = [regex]::Match($text, "<Version>([^<]+)</Version>")
    if (-not $m.Success) { throw "Cannot find <Version> in $path" }
    return $m.Groups[1].Value.Trim()
}

function Step-Version([string]$current, [string]$kind) {
    $parts = $current.Split(".")
    if ($parts.Count -ne 3) { throw "Version must be major.minor.patch, got '$current'" }
    $major = [int]$parts[0]; $minor = [int]$parts[1]; $patch = [int]$parts[2]
    switch ($kind) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    return "$major.$minor.$patch"
}

function Test-InRepo {
    & git -C $root rev-parse --git-dir *> $null
    return ($LASTEXITCODE -eq 0)
}

function Get-BaseRef {
    $tag = (& git -C $root describe --tags --abbrev=0 --match "v*" 2>$null)
    if ($LASTEXITCODE -eq 0 -and $tag) { return $tag.Trim() }
    $first = (& git -C $root rev-list --max-parents=0 HEAD 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $first) { return $first.Trim() }
    return $null
}

# 自上个版本 tag 起有改动的源码文件（不含文档、生成产物、变更说明本身）。
function Get-ChangedCodeFiles {
    $base = Get-BaseRef
    if (-not $base) { return @() }
    $raw = @()
    $raw += @(& git -C $root diff --name-only $base -- 2>$null)
    $raw += @(& git -C $root ls-files --others --exclude-standard 2>$null)

    $codeExt = @(".cs", ".axaml", ".ps1", ".cmd", ".csproj", ".iss", ".sln")
    $skipDirs = @("publish/", "obj/", "bin/", "artifacts/", "scratch/", "showreel/", "docs/", "changelog/",
        ".mimocode/", ".workbuddy/", ".trae-html-share-packages/", "_icon_debug/", "NVIDIA Corporation/")

    $result = New-Object System.Collections.ArrayList
    foreach ($f in $raw) {
        if (-not $f) { continue }
        $path = $f.Trim().Replace("\", "/")
        $skip = $false
        foreach ($d in $skipDirs) { if ($path.StartsWith($d)) { $skip = $true; break } }
        if ($skip) { continue }
        $ext = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
        if (-not ($codeExt -contains $ext)) { continue }
        if (-not $result.Contains($path)) { [void]$result.Add($path) }
    }
    return @($result)
}

function Format-TextNotes($entries) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($s in $PublicSections) {
        foreach ($item in $entries[$s]) { [void]$lines.Add("【$s】$item") }
    }
    return (($lines -join "`n"))
}

function Format-MarkdownNotes($entries) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($s in $PublicSections) {
        if ($entries[$s].Count -eq 0) { continue }
        [void]$sb.Append("### $s`n`n")
        foreach ($item in $entries[$s]) { [void]$sb.Append("- $item`n") }
        [void]$sb.Append("`n")
    }
    return $sb.ToString().TrimEnd()
}

# 从 changelog/released.md 取某个已封存版本的对外条目（用于"已封存但还没发布"的补发场景）。
function Read-ArchivedVersion([string]$version) {
    $text = Read-Text $archivePath
    $pattern = "(?m)^##\s+\[$([regex]::Escape($version))\][\s\S]*?(?=^##\s+\[|\z)"
    $m = [regex]::Match($text, $pattern)
    if (-not $m.Success) { return $null }
    return (Parse-Entries ($m.Value -replace "(?m)^##\s+\[.+", "## archived"))
}

# ---- 模式：查看状态（默认） ----
if (-not ($Check -or $Notes -or $Seal -or $Draft)) {
    $entries = Parse-Entries (Read-Text $unreleasedPath)
    $total = Get-TotalCount $entries
    $pub = Get-PublicCount $entries
    Write-Host "当前版本:   $(Read-ProjectVersion $proj)"
    Write-Host "未发布条目: $total 条（对外 $pub 条 / 内部 $($total - $pub) 条）"
    foreach ($s in $Sections) {
        if ($entries[$s].Count -eq 0) { continue }
        Write-Host "  [$s]"
        foreach ($item in $entries[$s]) { Write-Host "    - $item" }
    }
    if ($total -eq 0) { Write-Host "提示: 还没有未发布条目，改完代码请追加到 changelog\unreleased.md。" }
    return
}

# ---- 模式：草稿 ----
if ($Draft) {
    $base = Get-BaseRef
    if (-not $base) { throw "找不到基线（既无 v* tag 也无提交历史）" }
    $log = @(& git -C $root log "$base..HEAD" --no-merges --pretty="- %s" 2>$null)
    $files = Get-ChangedCodeFiles
    Write-Output "<!-- git 草稿：请改写成用户视角后粘进 changelog\unreleased.md 对应分段 -->"
    Write-Output ""
    if ($log.Count -eq 0) { Write-Output "（$base 之后没有新提交）" } else { Write-Output ($log -join "`n") }
    if ($files.Count -gt 0) {
        Write-Output ""
        Write-Output "改动文件（供归纳）："
        foreach ($f in $files) { Write-Output "- $f" }
    }
    return
}

# ---- 模式：检查 ----
if ($Check) {
    $entries = Parse-Entries (Read-Text $unreleasedPath)
    $total = Get-TotalCount $entries
    $pub = Get-PublicCount $entries
    $files = @()
    if (Test-InRepo) { $files = Get-ChangedCodeFiles }

    if ($files.Count -gt 0 -and $total -eq 0) {
        $base = Get-BaseRef
        $msg = "自 $base 以来有 $($files.Count) 个源码文件改动，但 changelog\unreleased.md 里没有任何条目。"
        $msg += "`n请在对应分段补写说明（纯内部改动写进「内部」），或加 -AllowMissing 跳过。`n改动文件:"
        foreach ($f in ($files | Select-Object -First 12)) { $msg += "`n  $f" }
        if ($files.Count -gt 12) { $msg += "`n  ...（其余 $($files.Count - 12) 个省略）" }
        if ($AllowMissing) { Write-Warning $msg } else { throw $msg }
    } elseif ($files.Count -gt 0 -and $pub -eq 0) {
        Write-Warning ("有 $($files.Count) 个源码文件改动，但对外条目为 0（只有「内部」条目）。" +
            "如果这次改动用户看得见，请在「新增/优化/修复」补一条。")
    }

    Write-Host "[changelog] OK: 未发布 $total 条（对外 $pub 条）"
    return
}

# ---- 模式：输出更新说明 ----
if ($Notes) {
    $entries = Parse-Entries (Read-Text $unreleasedPath)
    if ((Get-TotalCount $entries) -eq 0 -and $Version) {
        $archived = Read-ArchivedVersion $Version
        if ($archived) {
            $entries = $archived
            Write-Host "[changelog] 未发布区为空，改用 changelog/released.md 中 $Version 的条目"
        }
    }
    if ((Get-PublicCount $entries) -eq 0) {
        Write-Warning "[changelog] 没有对外条目（「新增/优化/修复」全为空），更新说明将为空。"
        return
    }
    $body = if ($Format -eq "md") { Format-MarkdownNotes $entries } else { Format-TextNotes $entries }
    if ($body.Length -gt 600) {
        Write-Warning "[changelog] 更新说明 $($body.Length) 字，弹窗折叠预览为 180 字，建议精简到 3~5 条。"
    }

    if ($OutFile) {
        Write-Text $OutFile ($body + "`n")
        Write-Host "已写入: $OutFile"
    } else {
        Write-Output $body
    }
    return
}

# ---- 模式：封存 ----
if ($Seal) {
    $entries = Parse-Entries (Read-Text $unreleasedPath)
    if ((Get-TotalCount $entries) -eq 0) { throw "changelog\unreleased.md 没有条目，无可封存。" }

    $currentVersion = Read-ProjectVersion $proj
    if ($Version) {
        if ($Bump -ne "none") { throw "Use either -Version or -Bump, not both." }
        $targetVersion = $Version.Trim().TrimStart([char[]]"vV")
        if (-not [regex]::IsMatch($targetVersion, "^\d+\.\d+\.\d+$")) {
            throw "Invalid -Version '$Version' (expected major.minor.patch)"
        }
    } elseif ($Bump -ne "none") {
        $targetVersion = Step-Version $currentVersion $Bump
    } else {
        $targetVersion = $currentVersion
    }

    $day = if ($Date) { $Date } else { (Get-Date).ToString("yyyy-MM-dd") }
    $text = Read-Text $archivePath
    $nl = Detect-NewLine $text

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("## [$targetVersion] - $day$nl$nl")
    foreach ($s in $Sections) {
        if ($entries[$s].Count -eq 0) { continue }
        [void]$sb.Append("### $s$nl$nl")
        foreach ($item in $entries[$s]) { [void]$sb.Append("- $item$nl") }
        [void]$sb.Append($nl)
    }
    $block = $sb.ToString().TrimEnd() + $nl

    if ($text.Contains($anchor)) {
        $idx = $text.IndexOf($anchor) + $anchor.Length
        $updated = $text.Substring(0, $idx) + $nl + $nl + $block + $text.Substring($idx)
    } elseif ($text -match "(?m)^##\s+\[") {
        $idx = $text.IndexOf("## [")
        $updated = $text.Substring(0, $idx) + $block + $text.Substring($idx)
    } else {
        $updated = $text.TrimEnd() + $nl + $nl + $block
    }

    # 清空未发布区：删掉 "- " 条目行，保留顶部说明与四个分段标题（注释内的示例行不动）
    $template = Read-Text $unreleasedPath
    $resetLines = New-Object System.Collections.ArrayList
    $inComment = $false
    foreach ($raw in ($template -split "`r?`n")) {
        $t = $raw.Trim()
        if ($inComment) {
            [void]$resetLines.Add($raw)
            if ($t.Contains("-->")) { $inComment = $false }
            continue
        }
        if ($t.StartsWith("<!--")) {
            [void]$resetLines.Add($raw)
            if (-not $t.Contains("-->")) { $inComment = $true }
            continue
        }
        if ($t -match "^[*-]\s+") { continue }
        [void]$resetLines.Add($raw)
    }
    $reset = (($resetLines -join $nl).TrimEnd()) + $nl

    if ($DryRun) {
        Write-Host "==== 将写入 changelog/released.md ===="
        Write-Output $block
        Write-Host "==== 版本 $targetVersion（csproj 当前 $currentVersion），共 $(Get-TotalCount $entries) 条 ===="
        return
    }

    Write-Text $archivePath $updated
    Write-Text $unreleasedPath $reset
    Write-Host "[changelog] 已封存 $targetVersion 到 changelog/released.md（对外 $(Get-PublicCount $entries) 条 / 内部 $(Get-TotalCount $entries) 条），unreleased.md 已清空。"
    return
}
