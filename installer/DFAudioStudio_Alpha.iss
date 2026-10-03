; 三角洲音频工坊 · DFAudioStudio · Alpha 定时过期版 安装脚本（Inno Setup 7）
; 只打包程序本体（自包含 .NET + WindowsAppSDK 运行时），不含任何模型 / 音频 / 索引数据。
; 本版本启动时通过 NTP 服务器校验网络时间，2026-09-24（北京时间）正式过期。
; 编译：ISCC.exe DFAudioStudio_Alpha.iss   （需先把 dotnet publish 的产物拷到 installer\app）
#define MyAppName "三角洲音频工坊 Alpha测试版"
#define MyAppVersion "Alpha-20260924"
#define MyVersionInfoVersion "1.0.0.0"
#define MyAppPublisher "河豚潘PufferPan"
#define MyAppExeName "DFAudioStudio.App.exe"
#define MyExpiryDate "2026年9月24日"

[Setup]
; Alpha 专用 AppId（与正式版不同，可同时安装、互不覆盖）
AppId={{5C1E7B44-9D2A-4A17-8E63-2F7A9C31D508}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} ({#MyAppVersion})
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DFAudioStudio_Alpha
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=DFAudioStudio_Alpha_Setup
LicenseFile=license_alpha.txt
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyVersionInfoVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=三角洲音频工坊 Alpha测试版（2026-09-24 过期）

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 程序本体（自包含，约 190MB；不含 data / models 等资源文件）
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// 安装前告知限时 Alpha 的时间校验规则
function InitializeSetup(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  Answer := MsgBox('本安装包为 Alpha 测试版本，将于 {#MyExpiryDate} 正式过期。' + #13#10 + #13#10 +
    '启动时会通过公网 NTP 服务器获取网络时间来校验有效期：' + #13#10 +
    '· 系统时间与网络时间不一致（偏差超过 5 分钟）；' + #13#10 +
    '· 网络时间已超过过期日期；' + #13#10 +
    '· 无法连接任何 NTP 服务器完成校验。' + #13#10 +
    '出现以上任一情况，程序会提示「此版本为Alpha测试版本。于2026年9月24日正式过期，请等待正式版本发布」并退出。' + #13#10 + #13#10 +
    '本安装包只包含程序本体，不含音频 / 模型 / 索引数据（首次使用请在软件内配置素材目录，或沿用正式版已有的数据目录）。' + #13#10 + #13#10 +
    '是否继续安装？', mbConfirmation, MB_YESNO);
  if Answer = IDNO then
    Result := False;
end;
