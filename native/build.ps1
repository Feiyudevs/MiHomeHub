# 编译 Zonex.Glass.dll（设置面板的背景毛玻璃合成层）。
# 不调 vcvars64.bat：那个脚本内部会跑 reg.exe，在受限环境里会被安全策略拦掉整棵进程树。
# 这里手工拼 INCLUDE/LIB 直接调 cl.exe。
$ErrorActionPreference = 'Stop'

$msvc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207'
$sdk = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVer = '10.0.26100.0'
$here = $PSScriptRoot

if (!(Test-Path "$msvc\bin\Hostx64\x64\cl.exe")) { throw "找不到 cl.exe：$msvc" }

$env:INCLUDE = @(
  "$msvc\include",
  "$sdk\Include\$sdkVer\ucrt",
  "$sdk\Include\$sdkVer\shared",
  "$sdk\Include\$sdkVer\um",
  "$sdk\Include\$sdkVer\winrt",
  "$sdk\Include\$sdkVer\cppwinrt"
) -join ';'
$env:LIB = @(
  "$msvc\lib\x64",
  "$sdk\Lib\$sdkVer\ucrt\x64",
  "$sdk\Lib\$sdkVer\um\x64"
) -join ';'
$env:PATH = "$msvc\bin\Hostx64\x64;$sdk\bin\$sdkVer\x64;" + $env:PATH

$log = & cl.exe /nologo /utf-8 /std:c++17 /EHsc /MT /LD "$here\Glass.cpp" /Fo"$here\" `
    /link /OUT:"$here\Zonex.Glass.dll" windowsapp.lib d2d1.lib dxguid.lib user32.lib 2>&1
$log | ForEach-Object { $_.ToString() } | Where-Object { $_ -match ' error |warning C4' } | Select-Object -First 20
if ($LASTEXITCODE -ne 0) { throw "原生合成层构建失败：$LASTEXITCODE" }

Remove-Item "$here\Glass.obj", "$here\Glass.lib", "$here\Glass.exp" -ErrorAction SilentlyContinue
Get-ChildItem $here -Filter 'Zonex.Glass.dll' | Select-Object Name, Length
