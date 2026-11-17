; Spotlight 安装脚本 (Inno Setup 6.5+ / 7.x)
; 由 CI 调用：ISCC.exe /DMyAppVersion=0.1.1 installer\SpotlightSearch.iss
; 本地调试：ISCC.exe /DMyAppVersion=0.1.1 /DSourceDir=publish\scd installer\SpotlightSearch.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

; VersionInfoVersion 只接受纯数字（a.b.c.d），CI 会单独传一个干净的
#ifndef VersionInfo
  #define VersionInfo MyAppVersion
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\scd"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "Spotlight"
#define MyAppExeName "SpotlightSearch.exe"
#define MyAppPublisher "liwenyao016828"
#define MyAppURL "https://github.com/liwenyao016828/SpotlightSearch"

[Setup]
; 固定 AppId：升级时覆盖安装而不是并存
AppId={{3F1C7A64-2B0E-4D51-9A7E-6C51B2D8A9F3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases

; per-user 安装，无需管理员权限
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes

; x64 only（.NET 10 win-x64 产物）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 需要先关掉正在运行的实例才能覆盖 exe
CloseApplications=no
RestartApplications=no

OutputDir={#OutputDir}
OutputBaseFilename=Spotlight-{#MyAppVersion}-setup
SetupIconFile=..\src\SpotlightSearch\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
ShowLanguageDialog=auto
VersionInfoVersion={#VersionInfo}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#VersionInfo}

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "开机自动启动（推荐）"; GroupDescription: "选项："
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "选项："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; 开机启动（per-user，卸载时自动清除）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先结束常驻进程，否则 exe 被占用无法删除
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillSpotlight"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
{ 覆盖安装时也要先结束旧进程，否则 Files 段写不进去 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
