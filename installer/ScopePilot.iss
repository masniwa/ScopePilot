#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by Build-Installer.ps1
#endif
#ifndef ArtifactDir
  #error ArtifactDir must be supplied by Build-Installer.ps1
#endif

[Setup]
AppId={{C539DD3E-846B-4C62-961A-C34F4D13FA9B}
AppName=ScopePilot Fork
AppVersion={#AppVersion}
AppPublisher=ScopePilot Fork contributors
DefaultDirName={localappdata}\Programs\ScopePilotFork
DefaultGroupName=ScopePilot Fork
PrivilegesRequired=lowest
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
MinVersion=10.0.19044
OutputDir={#ArtifactDir}
OutputBaseFilename=ScopePilot-Fork-{#AppVersion}-{#Runtime}-Setup
VersionInfoVersion={#AppVersion}.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=120
DisableWelcomePage=no
DisableDirPage=yes
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayIcon={app}\ScopePilot.exe
UninstallDisplayName=ScopePilot Fork
SetupLogging=yes
AppMutex=Local\ScopePilot.Fork.Application
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=BeforeInstall.txt
LicenseFile=..\LICENSE
ChangesAssociations=no
ChangesEnvironment=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "FirstRun.html"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ScopePilot Fork"; Filename: "{app}\ScopePilot.exe"; WorkingDir: "{app}"
Name: "{group}\初回セットアップガイド"; Filename: "{app}\FirstRun.html"
Name: "{autodesktop}\ScopePilot Fork"; Filename: "{app}\ScopePilot.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FirstRun.html"; Description: "初回セットアップガイドを開く"; Flags: postinstall shellexec skipifsilent
Filename: "{app}\ScopePilot.exe"; Description: "ScopePilot Forkを起動する"; Flags: postinstall nowait skipifsilent unchecked

; Do not add recursive uninstall deletions: project data and Codex settings are user-owned.

[Code]
function InitializeSetup(): Boolean;
var
  ExistingDir: String;
  ExistingMS, ExistingLS: Cardinal;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C539DD3E-846B-4C62-961A-C34F4D13FA9B}_is1',
    'Inno Setup: App Path', ExistingDir) then
  begin
    if GetVersionNumbers(ExistingDir + '\ScopePilot.dll', ExistingMS, ExistingLS) then
    begin
      if (ExistingMS > {#VersionMS}) or
         ((ExistingMS = {#VersionMS}) and (ExistingLS > {#VersionLS})) then
      begin
        SuppressibleMsgBox('より新しいScopePilot Forkがインストールされています。古い版への上書きはできません。', mbError, MB_OK, IDOK);
        Result := False;
      end;
    end;
  end;
end;
