; EmuDroid installer (Inno Setup 6).
; Build:  ISCC.exe /DAppVersion=1.0.0 /DPublishDir=..\publish installer\EmuDroid.iss
; The installer only contains EmuDroid itself. Android components are downloaded from Google by the app.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "EmuDroid"
#define AppExe "EmuDroid.exe"
#define AppUrl "https://github.com/danilomitic/EmuDroid-Android-Emulator"

[Setup]
AppId={{1662A9D7-2410-462C-A82A-81C2F58B9DCD}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=EmuDroid
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
; Per-user install: no admin rights needed.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\EmuDroid\app
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=EmuDroid-Setup
SetupIconFile=..\src\EmuDroid.App\Assets\emudroid.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Normal install: offer to start EmuDroid. Silent auto-update: start it again automatically.
Filename: "{app}\{#AppExe}"; Description: "Start EmuDroid"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait skipifnotsilent

[Code]
// On uninstall, ask whether to also remove Android versions and instances (can be many GB).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\EmuDroid');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also delete your Android instances, apps and downloaded Android versions?' + #13#10 +
                'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
