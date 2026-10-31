#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef ProjectRoot
  #define ProjectRoot SourcePath + ".."
#endif
#ifndef OutputRoot
  #define OutputRoot ProjectRoot + "\artifacts"
#endif

[Setup]
#ifdef TestBuild
AppId={{25E20F77-3F61-469A-B46C-A3DB9AB85A1E}
#else
AppId={{7D1C732B-9E31-42B4-AC9B-8264492E0591}
#endif
AppName=LumaDock
AppVersion={#AppVersion}
AppVerName=LumaDock {#AppVersion}
AppPublisher=LumaDock
AppCopyright=LumaDock
#ifdef TestBuild
DefaultDirName={#TestRoot}\Default install\LumaDock
#else
DefaultDirName=C:\LumaDock
#endif
DefaultGroupName=LumaDock
DisableDirPage=no
AppendDefaultDirName=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=no
#ifdef TestBuild
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
MinVersion=10.0
OutputDir={#OutputRoot}
#ifdef TestBuild
OutputBaseFilename=LumaDock-Setup-Test
#else
OutputBaseFilename=LumaDock-{#AppVersion}
#endif
SetupIconFile={#ProjectRoot}\assets\MonitorControl.ico
UninstallDisplayIcon={app}\LumaDock.exe
UninstallDisplayName=LumaDock
VersionInfoVersion={#AppVersion}.0
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark polar includetitlebar hidebevels
WizardBackColor=#111820
WizardImageBackColor=#14212C
WizardSmallImageBackColor=#111820
WizardImageFile=assets\wizard.png
WizardSmallImageFile=assets\wizard-small.png
WizardSizePercent=115
WizardImageStretch=yes
WizardResizable=no
CloseApplications=no
#ifdef TestBuild
AppMutex=Local\LumaDock.InstallerTests
#else
AppMutex=Local\LumaDock.App
#endif
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
Uninstallable=yes

#ifdef TestBuild
CreateUninstallRegKey=no
UsePreviousAppDir=no
UsePreviousTasks=no
#else
UsePreviousAppDir=yes
UsePreviousTasks=yes
#endif

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Messages]
WelcomeLabel1=LumaDock 설치
WelcomeLabel2=모니터 제어 앱을 설치합니다.
SelectDirDesc=설치 위치 선택
SelectDirLabel3=선택한 폴더 아래 LumaDock 폴더에 설치합니다.
SelectDirBrowseLabel=설치 폴더
SelectTasksDesc=바로가기 선택
SelectTasksLabel2=추가할 바로가기를 선택하세요.
FinishedHeadingLabel=설치가 완료되었습니다
FinishedLabel=설치가 완료되었습니다.
BeveledLabel=LumaDock

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "바로가기"; Flags: unchecked
Name: "startmenuicon"; Description: "시작 메뉴에 추가하기"; GroupDescription: "바로가기"

[Files]
Source: "{#ProjectRoot}\bin\LumaDock.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\bin\LumaDock.exe.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
#ifdef TestBuild
Name: "{#TestRoot}\Desktop\LumaDock"; Filename: "{app}\LumaDock.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{#TestRoot}\StartMenu\LumaDock\LumaDock"; Filename: "{app}\LumaDock.exe"; WorkingDir: "{app}"; Tasks: startmenuicon
#else
Name: "{autodesktop}\LumaDock"; Filename: "{app}\LumaDock.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\LumaDock\LumaDock"; Filename: "{app}\LumaDock.exe"; WorkingDir: "{app}"; Tasks: startmenuicon
#endif

[Run]
Filename: "{app}\LumaDock.exe"; Description: "LumaDock 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  EffectiveDirLabel: TNewStaticText;
  OriginalDirChange: TNotifyEvent;
  UpdatingDirectory: Boolean;

function NormalizeInstallDir(Dir: String): String;
var
  Parent: String;
begin
  Result := Trim(Dir);
  StringChangeEx(Result, '/', '\', True);
  Result := RemoveBackslashUnlessRoot(Result);
  if Result = '' then Exit;
  while CompareText(ExtractFileName(Result), 'LumaDock') = 0 do
  begin
    Parent := RemoveBackslashUnlessRoot(ExtractFileDir(Result));
    if CompareText(ExtractFileName(Parent), 'LumaDock') <> 0 then Break;
    Result := Parent;
  end;
  if CompareText(ExtractFileName(Result), 'LumaDock') <> 0 then
    Result := AddBackslash(Result) + 'LumaDock';
end;

procedure DirectoryChanged(Sender: TObject);
var
  CurrentDir, ParentDir, EffectiveDir: String;
begin
  if UpdatingDirectory then Exit;
  CurrentDir := Trim(WizardForm.DirEdit.Text);
  StringChangeEx(CurrentDir, '/', '\', True);
  CurrentDir := RemoveBackslashUnlessRoot(CurrentDir);
  ParentDir := RemoveBackslashUnlessRoot(ExtractFileDir(CurrentDir));
  EffectiveDir := NormalizeInstallDir(CurrentDir);
  if (CompareText(ExtractFileName(CurrentDir), 'LumaDock') = 0) and
     (CompareText(ExtractFileName(ParentDir), 'LumaDock') = 0) then
  begin
    UpdatingDirectory := True;
    WizardForm.DirEdit.Text := EffectiveDir;
    UpdatingDirectory := False;
  end;
  if OriginalDirChange <> nil then OriginalDirChange(Sender);
  if EffectiveDirLabel <> nil then
    EffectiveDirLabel.Caption := '실제 설치 위치: ' + EffectiveDir;
end;

procedure InitializeWizard;
begin
  EffectiveDirLabel := TNewStaticText.Create(WizardForm);
  EffectiveDirLabel.Parent := WizardForm.DirEdit.Parent;
  EffectiveDirLabel.AutoSize := False;
  EffectiveDirLabel.WordWrap := True;
  EffectiveDirLabel.SetBounds(WizardForm.DirEdit.Left,
    WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(12),
    WizardForm.SelectDirLabel.Width, ScaleY(42));
  EffectiveDirLabel.Font.Size := 8;
  OriginalDirChange := WizardForm.DirEdit.OnChange;
  WizardForm.DirEdit.OnChange := @DirectoryChanged;
  WizardForm.DirEdit.Text := NormalizeInstallDir(WizardForm.DirEdit.Text);
  DirectoryChanged(WizardForm.DirEdit);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpSelectDir then
    WizardForm.DirEdit.Text := NormalizeInstallDir(WizardForm.DirEdit.Text);
  Result := True;
end;

function HasDotNet48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if (not Result) and IsWin64 then
    Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function InitializeSetup: Boolean;
begin
  Result := HasDotNet48;
  if not Result then
    MsgBox('LumaDock을 실행하려면 .NET Framework 4.8 이상이 필요합니다.' + #13#10 + 'Windows 업데이트 또는 Microsoft 공식 .NET Framework 설치 프로그램으로 설치한 뒤 다시 실행해 주세요.', mbError, MB_OK);
end;
