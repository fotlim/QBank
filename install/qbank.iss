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
#define AppVersion "1.0.2"
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

; 一定要显示「选择安装位置」页。
; Inno 的默认值 auto 在 PrivilegesRequired=lowest（纯用户安装）时会自动隐藏目录页，
; 用户就没法改路径了 —— 所以显式设成 no。
DisableDirPage=no
; 目录页上允许手输，也允许浏览；路径可以带空格
DirExistsWarning=auto

; 按用户安装，不需要管理员；但允许用户主动选择「为所有用户安装」
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

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
Name: "portablemode"; Description: "数据跟程序放一起（便携模式，配置和题库存到安装目录的 data）"; GroupDescription: "数据位置"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{autostartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清我们自己生成的日志与临时文件，用户数据目录一律不动
Type: files; Name: "{app}\startup.log"
Type: filesandordirs; Name: "{app}\data"

[Code]
// 便携模式：装完在程序目录写一个 portable.txt，程序启动时就认它
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('portablemode') then
    SaveStringToFile(ExpandConstant('{app}\portable.txt'),
      'QBank portable mode. Data is stored in the "data" folder next to QBank.exe.' + #13#10 +
      'Delete this file to use the default location.' + #13#10, False);
end;

// 卸载时问一句要不要连数据一起删
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Answer: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 便携模式下数据就在 {app}\data，优先判断它
    if FileExists(ExpandConstant('{app}\portable.txt')) then
      DataDir := ExpandConstant('{app}\data')
    else
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
