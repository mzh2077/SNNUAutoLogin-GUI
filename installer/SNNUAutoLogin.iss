; ============================================================================
;  陕师大校园网自动登录 —— 安装包构建脚本（Inno Setup 6）
;
;  构建：  build_installer.bat
;  或手工：ISCC.exe SNNUAutoLogin.iss
;
;  产物：  installer\Output\SNNUAutoLogin-Setup-<版本>.exe
; ============================================================================

#define AppName        "陕师大校园网自动登录"
#define AppNameEn      "SNNUAutoLogin"
#define AppVer         "1.6.0"
#define AppVerFull     "1.6.0.0"
#define AppExe         "SNNUAutoLogin.exe"
#define AppPublisher   "SNNU"
#define AppURL         "https://www.snnu.edu.cn"
#ifndef SrcDir
  #define SrcDir       "..\release"
#endif

[Setup]
; AppId 决定「升级 / 修复 / 卸载」是否识别为同一个程序，一旦发布就不要改
AppId={{8F3C2A91-5D74-4E2B-9C18-6A7B0E4D3F52}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoVersion={#AppVerFull}
VersionInfoProductVersion={#AppVer}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoCopyright=Copyright (C) 2026 SNNU

; 默认装到当前用户的 Programs 目录（与 VS Code 用户版、Chrome 一致）：
; 不弹 UAC、不需要管理员权限，宿舍 / 实验室的受限账户也能装。
; 向导首屏允许切换成「为所有用户安装」（此时会装进 Program Files 并请求提权）。
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DirExistsWarning=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
AllowNoIcons=yes

; WinUI 3 需要 Windows 10 2004（19041）及以上
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=Output
OutputBaseFilename={#AppNameEn}-Setup-{#AppVer}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
DisableWelcomePage=no
SetupLogging=yes

; 安装前若程序正在驻留托盘：由 [Code] 里的 InitializeSetup 直接结束进程，
; 这里不再让 Restart Manager 插手（它对托盘程序的处理不可预期），装完后按需重新拉起
AppMutex=Local\SNNUAutoLogin.SingleInstance
CloseApplications=no
RestartApplications=no

[Languages]
Name: "cn"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 卸载时顺带清掉开机自启项（ValueType=none 表示安装阶段不写入，只登记删除动作）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "SNNUAutoLogin"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent
; 静默覆盖升级时不会走"安装完成"页，这里把被关掉的程序自动恢复运行
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RestartAppAfterSilentUpgrade

[UninstallDelete]
; 卸载时移除本机保存的账号 / 密码 / 配置
Type: filesandordirs; Name: "{localappdata}\{#AppNameEn}"

[Code]
const
  RunKey    = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue  = 'SNNUAutoLogin';
  MutexName = 'Local\SNNUAutoLogin.SingleInstance';

var
  WasRunning: Boolean;   { 安装前程序是否在运行，用来决定装完要不要拉起来 }

{ 程序是否还在运行（托盘驻留时没有主窗口，只能靠单实例互斥量判断） }
function RunningInstance: Boolean;
begin
  Result := CheckForMutexes(MutexName);
end;

{ 结束正在运行的程序。它会拦截关闭消息收进托盘，所以直接强制结束；
  进程在文件被占用时会让覆盖安装失败，这一步必须做在 Inno 的 AppMutex 检查之前。
  返回 True 表示确实结束了一个正在运行的实例（据此决定装完要不要拉起来）。 }
function KillRunningInstance: Boolean;
var
  ResultCode: Integer;
  I: Integer;
begin
  Result := False;
  if not RunningInstance then
  begin
    Log('SNNU: 未检测到运行中的实例');
    Exit;
  end;
  Log('SNNU: 检测到运行中的实例，正在结束');

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('SNNU: taskkill exit=%d', [ResultCode]));

  // 以 taskkill 的结果为准：互斥量可能比进程晚一步释放，不能拿来判断"是否杀过"
  Result := (ResultCode = 0);

  for I := 1 to 25 do
  begin
    Sleep(120);
    if not RunningInstance then
    begin
      Log(Format('SNNU: 进程已退出（等待 %d ms）', [I * 120]));
      Exit;
    end;
  end;
  Log('SNNU: 等待进程退出超时');
end;

{ 这一步早于 Inno 的 AppMutex 检查，所以覆盖更新不会被"程序正在运行"挡住 }
function InitializeSetup(): Boolean;
begin
  WasRunning := KillRunningInstance;
  Result := True;
end;

{ 静默升级时把程序拉回来，避免"更新完自动登录悄悄停摆" }
function RestartAppAfterSilentUpgrade: Boolean;
begin
  Result := WasRunning and WizardSilent;
end;

{ 覆盖安装时清掉旧版本留下、新版本已经不再发布的目录。
  典型是 WinUI 的 80 多个语言资源目录：1.4.0 起只保留 zh-CN / en-us，
  但 Inno 的 [Files] 只覆盖同名文件，不会删掉"这次没带过来的"目录，
  不主动清就会一直留着一份几十 MB 的死重量。
  这一步跑在 ssInstall（真正复制文件之前），所以新版本要用的目录随后会被重新建好。 }
procedure CleanupStaleDirs;
var
  FindRec: TFindRec;
  Name: String;
  Target: String;
begin
  if not FindFirst(ExpandConstant('{app}\*'), FindRec) then Exit;

  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then Continue;

      Name := FindRec.Name;
      if (Name = '.') or (Name = '..') then Continue;
      if (CompareText(Name, 'zh-CN') = 0) then Continue;
      if (CompareText(Name, 'en-us') = 0) then Continue;
      if (CompareText(Name, 'Microsoft.UI.Xaml') = 0) then Continue;

      Target := ExpandConstant('{app}\') + Name;
      Log('SNNU: 清理旧版本目录 ' + Target);
      DelTree(Target, True, True, True);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ 递归删掉安装目录里的旧文件（保留卸载器 unins*）。
  1.4.1 起发布端删掉了 50 多个 winmd / 未加载程序集，而 Inno 的 [Files] 只覆盖同名文件，
  不主动删就会让老用户升级后目录里堆着一份再也用不到的死文件（实测 162MB vs 128MB）。 }
procedure DeleteStaleFiles(Dir: String);
var
  FindRec: TFindRec;
  Name: String;
  Full: String;
begin
  if not FindFirst(Dir + '\*', FindRec) then Exit;

  try
    repeat
      Name := FindRec.Name;
      if (Name = '.') or (Name = '..') then Continue;

      Full := Dir + '\' + Name;

      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        DeleteStaleFiles(Full);
        Continue;
      end;

      if Pos('unins', Lowercase(Name)) = 1 then Continue;

      if not DeleteFile(Full) then
        Log('SNNU: 旧文件删除失败 ' + Full);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ 覆盖升级时把开机自启改指到安装目录：
  旧版本可能是便携版（自启写的是别的路径），不改的话升级完还在启动旧程序。 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  OldValue: String;
  InstallExe: String;
begin
  if CurStep = ssInstall then
  begin
    DeleteStaleFiles(ExpandConstant('{app}'));
    CleanupStaleDirs;
    Exit;
  end;

  if CurStep <> ssPostInstall then Exit;
  if not RegQueryStringValue(HKCU, RunKey, RunValue, OldValue) then Exit;
  if OldValue = '' then Exit;

  InstallExe := ExpandConstant('{app}\{#AppExe}');
  if CompareText('"' + InstallExe + '" --tray', OldValue) = 0 then Exit;

  RegWriteStringValue(HKCU, RunKey, RunValue, '"' + InstallExe + '" --tray');
end;
