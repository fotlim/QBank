; 题库工坊 QBank —— Inno Setup 安装脚本
; 编译：ISCC.exe install\qbank.iss
; 产物：install\Output\QBank-Setup-<版本>.exe
;
; 安装策略：
;   · 按用户安装（PrivilegesRequired=lowest），不弹 UAC，装到 %LOCALAPPDATA%\Programs\QBank
;   · 程序是 unpackaged WinUI3 + 自包含 .NET，用户机器不需要另装运行时
;   · 用户数据放 %LOCALAPPDATA%\QBank，升级/卸载都不动它

#define AppName "题库工坊"
#define AppNameEn "QBank"
#define AppVersion "1.0.0"
#define AppPublisher "Escape-Lin"
#define AppUrl "https://github.com/fotlim/QBank"
#define AppExeName "QBank.exe"
#define BuildDir "..\publish"

[Setup]
AppId={{8F3C71A2-5D64-4E1B-9C37-2A6F0B4D8E15}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={localappdata}\Programs\{#AppNameEn}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; 按用户安装，不需要管理员
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 单实例只读数据放用户目录，装到 Program Files 也能用
OutputDir=Output
OutputBaseFilename={#AppNameEn}-Setup-{#AppVersion}
SetupIconFile=..\src\QBank.App\Assets\QBank.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=LICENSE.txt
ShowLanguageDialog=auto
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "chinese"; MessagesFile: "..\tools\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "startupicon"; Description: "开机自动启动 {#AppName}"; GroupDescription: "启动选项"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 便携模式开关：想让数据跟着程序走，把下面这行取消注释
; Source: "portable.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清我们自己生成的日志与临时文件，用户数据目录一律不动
Type: files; Name: "{app}\startup.log"
Type: filesandordirs; Name: "{app}\data"

[Code]
// 卸载时问一句要不要连数据一起删
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Answer: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\QBank');
    if not DirExists(DataDir) then
      DataDir := ExpandConstant('{app}\data');

    if DirExists(DataDir) then
    begin
      Answer := MsgBox('是否同时删除题库数据（配置、本地题库、历史记录）？' + #13#10 + #13#10 +
                       DataDir + #13#10 + #13#10 +
                       '选「否」可以保留，以后重装还能接着用。',
                       mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
      if Answer = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
