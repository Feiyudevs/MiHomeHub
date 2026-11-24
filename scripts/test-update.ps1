# 本地验证应用内自动更新（完全不依赖 GitHub Release）
#
# 用法（仓库根目录）：
#   .\scripts\test-update.ps1                  # 装 1.0.0 基线 → 出 1.0.1 包 → 启动已装实例点更新
#   .\scripts\test-update.ps1 -SkipInstall     # 已装基线，只出下一版包并启动
#   .\scripts\test-update.ps1 -BaselineVersion 1.0.1
#
# 原理：把环境变量 MIHOME_UPDATE_SOURCE 指向 publish\release\<新版本> 目录，
# 应用会从该目录读 update.json、拷贝安装包并校验 SHA256，再走 --silent 静默替换。
param(
    [string]$BaselineVersion = "1.0.0",
    [string]$Rid = "win-x64",
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "MiHome.csproj"
$installedExe = Join-Path $env:LOCALAPPDATA "Programs\MiHome\app\MiHome.exe"

function Get-ProjectVersion {
    $text = [System.IO.File]::ReadAllText($proj)
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "Cannot find <Version> in $proj" }
    return $m.Groups[1].Value.Trim()
}

function Stop-PublishInstances {
    # 便携实例会占着单实例互斥体，导致新启动的「已安装版本」被顶回去
    Get-CimInstance Win32_Process -Filter "Name='MiHome.exe'" |
        Where-Object { $_.ExecutablePath -like (Join-Path $root 'publish\*') } |
        ForEach-Object {
            Write-Host "停止便携实例 PID $($_.ProcessId)"
            Stop-Process -Id $_.ProcessId -Force
        }
}

function Stop-InstalledInstances {
    # 基线安装完成页默认会勾选「启动 MiHome」；那个进程没带联调变量，必须先关掉
    Get-CimInstance Win32_Process -Filter "Name='MiHome.exe'" |
        Where-Object { $_.ExecutablePath -like (Join-Path $env:LOCALAPPDATA 'Programs\MiHome\*') } |
        ForEach-Object {
            Write-Host "停止已安装实例 PID $($_.ProcessId)"
            Stop-Process -Id $_.ProcessId -Force
        }
}

# ---- 1) 基线安装 ----
if (-not $SkipInstall) {
    if (Test-Path $installedExe) {
        $current = (Get-Item $installedExe).VersionInfo.ProductVersion
        Write-Host "本机已安装 $current（$installedExe）；如需从 $BaselineVersion 开始，请先卸载"
    } else {
        $baselineSetup = Join-Path $root "publish\release\$BaselineVersion\MiHome-Setup-$BaselineVersion-$Rid.exe"
        if (-not (Test-Path -LiteralPath $baselineSetup)) {
            throw "缺少基线安装包：$baselineSetup（先出 $BaselineVersion 的包，或指定 -BaselineVersion）"
        }
        Write-Host "安装基线 $BaselineVersion ..."
        Stop-PublishInstances
        Start-Process -FilePath $baselineSetup -Wait
        if (-not (Test-Path $installedExe)) { throw "基线安装未完成：$installedExe" }
    }
}

# ---- 2) 出一版更高版本（顺带生成 update.json） ----
$before = Get-ProjectVersion
Write-Host "当前 csproj 版本 $before，开始 patch 递增打包..."
& (Join-Path $PSScriptRoot "release.ps1") -Bump patch
if ($LASTEXITCODE -ne 0) { throw "release.ps1 failed with exit code $LASTEXITCODE" }

$after = Get-ProjectVersion
if ($after -eq $before) { throw "版本没有变化（$after），无法构造更新" }

$source = Join-Path $root "publish\release\$after"
$manifest = Join-Path $source "update.json"
if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少更新清单：$manifest" }
Write-Host ""
Write-Host "更新源：$source"
Write-Host "清单版本：$after   已装版本：$before"

# ---- 3) 启动已安装实例（带覆盖源） ----
Stop-PublishInstances
Stop-InstalledInstances
# 落到文件而不是只靠环境变量：从资源管理器/托盘启动也能继承，重启应用也还有效
$overrideFile = Join-Path $env:APPDATA 'Feiyu\MiHome\update\source.txt'
New-Item -ItemType Directory -Path (Split-Path $overrideFile) -Force | Out-Null
[System.IO.File]::WriteAllText($overrideFile, $source, (New-Object System.Text.UTF8Encoding($false)))
$env:MIHOME_UPDATE_SOURCE = $source
Start-Process explorer.exe -ArgumentList $installedExe
Write-Host ""
Write-Host "已启动已安装版本（联调源已写入 $overrideFile）"
Write-Host "请打开「设置 → 关于 → 更新」："
Write-Host "  1) 点「检查更新」应显示 发现新版本 $after"
Write-Host "  2) 点「立即更新」应看到下载进度 → 程序自行退出 → 静默替换 → 自动重启为新版本"
Write-Host "  3) 测完删掉 $overrideFile，就会回到线上 GitHub 更新源"
Write-Host ""
Write-Host "想验证校验失败：把 $manifest 里的 sha256 改成任意 64 位 hex，再点「立即更新」应提示 SHA256 不匹配且不安装。"
