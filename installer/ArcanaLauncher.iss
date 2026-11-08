; Arcana Launcher installer (Inno Setup 6). CI builds it from the published exe (.github/workflows/build-launcher.yml):
;   ISCC installer\ArcanaLauncher.iss /DAppVersion=1.0.12.0 /DSourceExe=..\publish\ArcanaLauncher.exe /O..\publish
;
; Per-user: %LocalAppData%\Programs\Arcana Launcher, no admin prompt, so the launcher can update itself and the game in
; place. The launcher installs the game into Arcana\ (and Arcana-development\) beside itself; uninstall removes those too.
; Never name the output setup.exe: Windows shims that file name to load version.dll from the folder it runs in, the
; same planted-DLL problem (Lovely's version.dll in Downloads) this installer exists to get players away from.
;
; The AppId, AppMutex and AppUserModelID must match GameLauncher/LauncherInfo.cs. Never change the AppId: Windows finds
; an existing install, its uninstaller and the Apps list entry by it.

; x64os and the per-scale wizard images need Inno Setup 6.3 or newer (6.7.3 builds it today).
#if Ver < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\ArcanaLauncher.exe"
#endif
#define AppName "Arcana Launcher"
#define AppExe "ArcanaLauncher.exe"
#define Publisher "Tidan Games LLC"
#define AppUserModelID "TidanGames.ArcanaLauncher"

[Setup]
AppId={{D5E69C41-93F1-499D-8514-ABD3329B6CC2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}
AppPublisher={#Publisher}
AppPublisherURL=https://tidangames.com
AppSupportURL=https://support.tidangames.com
AppUpdatesURL=https://tidangames.com/download
AppCopyright=Copyright 2026 {#Publisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright 2026 {#Publisher}
; {autopf} is %LocalAppData%\Programs in a per-user install.
DefaultDirName={autopf}\{#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; .NET 9 needs Windows 10 or later.
MinVersion=10.0
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
WizardStyle=modern
ShowLanguageDialog=no
SetupIconFile=..\GameLauncher\ArcanaLauncher.ico
; The Arcana icon in the header and the logo on the finish page, one file per Windows display scale.
WizardSmallImageFile=WizardSmall-55.bmp,WizardSmall-64.bmp,WizardSmall-83.bmp,WizardSmall-92.bmp,WizardSmall-110.bmp,WizardSmall-138.bmp
WizardImageFile=WizardLarge-164.bmp,WizardLarge-246.bmp,WizardLarge-328.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputBaseFilename=ArcanaLauncherSetup
Compression=lzma2/max
SolidCompression=yes
; Setup and uninstall wait while the launcher is open (GameLauncher/SingleInstance.cs takes this mutex).
AppMutex=TidanGames.ArcanaLauncher
SetupMutex=TidanGames.ArcanaLauncherSetup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelID}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; AppUserModelID: "{#AppUserModelID}"

[Run]
; A launcher run from Downloads installs itself by running this setup with /SILENT /ADOPT="<its folder>"
; (GameLauncher/MainWindow.xaml.cs, InstallLauncherAsync): the installed launcher then moves that folder's game in.
Filename: "{app}\{#AppExe}"; Parameters: "--adopt ""{param:ADOPT}"""; Flags: nowait; Check: AdoptRequested
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Check: not AdoptRequested

[UninstallDelete]
; Only names this install owns: never {app} as a whole, which /DIR= could point at a shared folder.
Type: filesandordirs; Name: "{app}\Arcana"
Type: filesandordirs; Name: "{app}\Arcana.new"
Type: filesandordirs; Name: "{app}\Arcana.old"
Type: filesandordirs; Name: "{app}\Arcana.delete"
Type: filesandordirs; Name: "{app}\Arcana-development"
Type: filesandordirs; Name: "{app}\Arcana-development.new"
Type: filesandordirs; Name: "{app}\Arcana-development.old"
Type: filesandordirs; Name: "{app}\Arcana-development.delete"
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\{#AppExe}.new"
Type: files; Name: "{app}\{#AppExe}.new.part"
Type: files; Name: "{app}\{#AppExe}.failed"
Type: filesandordirs; Name: "{localappdata}\{#AppName}"
Type: dirifempty; Name: "{app}"

[Code]
function AdoptRequested: Boolean;
begin
  Result := ExpandConstant('{param:ADOPT|}') <> '';
end;

{ A folder holding a running Arcana.exe can't be renamed. Renaming it away and back tells whether the game is open,
  so uninstall never leaves half of a running game behind. }
function GameFolderInUse(const Folder: String): Boolean;
var
  Probe: String;
begin
  Result := False;
  if not DirExists(Folder) then
    Exit;
  Probe := Folder + '.closecheck';
  if RenameFile(Folder, Probe) then
    RenameFile(Probe, Folder)
  else
    Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if GameFolderInUse(ExpandConstant('{app}\Arcana')) or GameFolderInUse(ExpandConstant('{app}\Arcana-development')) then
  begin
    MsgBox('Arcana is still running. Close the game, then uninstall again.', mbError, MB_OK);
    Result := False;
  end;
end;

{ The game's own settings and downloaded data stay unless the player says otherwise: a reinstall then keeps them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also remove your Arcana settings and downloaded game data?' + #13#10#13#10 +
              'Keep them if you plan to play again on this PC.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\Arcana'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\Arcana\Arcana'), True, True, True);
      RemoveDir(ExpandConstant('{userappdata}\Arcana'));
    end;
end;
