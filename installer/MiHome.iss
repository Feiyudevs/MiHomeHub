; MiHome 安装器（自包含单文件，目标机无需安装 .NET）
;
; 一键打包: .\scripts\release.ps1 -Bump patch
; 手动编译前置:
;   1. 先执行:  .\scripts\publish-sc.ps1
;   2. 安装 Inno Setup 6 (https://jrsoftware.org/isdl.php)
;   3. 编译:    iscc installer\MiHome.iss

#define MyAppName "MiHome"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MiHome"
#define MyAppExeName "MiHome.exe"
#define PublishDir "..\publish\sc-win-x64"

[Setup]
AppId={{B3E7F1A2-5C4D-4E8B-9A61-2D7C0E9F4A81}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 自包含单文件安装到 Program Files 仍建议管理员权限；若只想装到用户目录可改 lowest
PrivilegesRequired=admin
OutputDir=..\publish\release\{#MyAppVersion}
OutputBaseFilename=MiHome-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\logo.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 自包含发布产物（含 .NET 运行时，单文件 exe）
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
