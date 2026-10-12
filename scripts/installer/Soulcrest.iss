#ifndef PayloadDir
  #error PayloadDir must point to the published application including mapdata.
#endif
#ifndef OutputDir
  #error OutputDir must be set.
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
; UpdateOnly (user request 2026-10-07): the same setup without the map data (about 100 MB instead of
; 420 MB) for an existing installation whose map data matches MapManifestSha256. The update search in
; Soulcrest takes it when the map data did not change.
#ifdef UpdateOnly
  #ifndef MapManifestSha256
    #error UpdateOnly needs MapManifestSha256 (SHA-256 of mapdata\manifest.json).
  #endif
  #define SetupName "Update"
#else
  #define SetupName "Setup"
#endif

[Setup]
AppId={{2946C774-E30B-4C98-A366-108062322891}
AppName=Soulcrest
AppVersion={#AppVersion}
AppPublisher=Soulcrest (privat)
DefaultDirName={localappdata}\Programs\Soulcrest
DefaultGroupName=Soulcrest
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Soulcrest-{#AppVersion}-{#SetupName}-win-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#PayloadDir}\wwwroot\soulcrest.ico
UninstallDisplayIcon={app}\Soulcrest.exe
DisableProgramGroupPage=yes
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=README-Setup.txt

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Soulcrest"; Filename: "{app}\Soulcrest.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Soulcrest"; Filename: "{app}\Soulcrest.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Soulcrest.exe"; Description: "{cm:LaunchProgram,Soulcrest}"; Flags: nowait postinstall skipifsilent unchecked
; Started by the update search in Soulcrest ("/SILENT /RESTARTAPP"): open Soulcrest again afterwards.
Filename: "{app}\Soulcrest.exe"; Flags: nowait; Check: RestartRequested

; User progress/settings in LOCALAPPDATA\Soulcrest are deliberately not installer files.
; Uninstall removes only the installed payload and shortcuts, never the user's data.

[CustomMessages]
german.UpdateNeedsInstall=Dieses Update setzt eine installierte Soulcrest-Version voraus. Bitte den vollständigen Installer (Soulcrest-{#AppVersion}-Setup-win-x64.exe) verwenden.
german.UpdateNeedsMapData=Dieses Update enthält keine Kartendaten, und die installierten passen nicht dazu. Bitte den vollständigen Installer (Soulcrest-{#AppVersion}-Setup-win-x64.exe) verwenden.
german.PrereqCaption=Voraussetzungen
german.PrereqDescription=Npcap für passiven Netzwerkfortschritt
german.PrereqSub=Npcap wird für Pet-, Erkundungs- und Loot-Fortschritt aus dem Netzwerk benötigt. Wenn es fehlt und ausgewählt ist, wird der offizielle Installer gestartet; Windows fragt dafür nach Administratorrechten.
german.PrereqNpcap=Npcap 1.89 herunterladen und installieren (npcap.com, für das Netzwerk-Tracking). Im Npcap-Installer die Voreinstellungen übernehmen; die kostenlose Npcap-Lizenz erlaubt bis zu 5 Installationen.
german.PrereqAlreadyInstalled=bereits installiert
german.PrereqDownloadFailed=Npcap konnte nicht heruntergeladen werden: %1%n%nDu kannst es später selbst von https://npcap.com installieren. Die Installation von Soulcrest geht weiter.
german.PrereqInstallingNpcap=Npcap-Installer läuft …
german.PrereqDone=Voraussetzungen:
german.PrereqNpcapOk=Npcap: installiert
german.PrereqNpcapMissing=Npcap: fehlt noch (https://npcap.com); ohne Npcap kein Netzwerk-Tracking
english.UpdateNeedsInstall=This update needs an installed Soulcrest. Please use the full installer (Soulcrest-{#AppVersion}-Setup-win-x64.exe).
english.UpdateNeedsMapData=This update contains no map data, and the installed map data does not match. Please use the full installer (Soulcrest-{#AppVersion}-Setup-win-x64.exe).
english.PrereqCaption=Requirements
english.PrereqDescription=Npcap for passive network progress
english.PrereqSub=Npcap is needed to read pet, exploration and loot progress from the network. If it is missing and selected, the official installer starts; Windows asks for administrator rights.
english.PrereqNpcap=Download and install Npcap 1.89 (npcap.com, for network tracking). Keep the defaults in the Npcap installer; the free Npcap licence allows up to 5 installations.
english.PrereqAlreadyInstalled=already installed
english.PrereqDownloadFailed=Npcap could not be downloaded: %1%n%nYou can install it later from https://npcap.com. Soulcrest setup continues.
english.PrereqInstallingNpcap=Npcap installer running …
english.PrereqDone=Requirements:
english.PrereqNpcapOk=Npcap: installed
english.PrereqNpcapMissing=Npcap: still missing (https://npcap.com); no network tracking without Npcap

[Code]
// Passive network progress (user request 2026-10-12) requires only Npcap.
// Npcap is downloaded on request from its official site, checked against the pinned SHA-256,
// and installed interactively with elevation. Soulcrest itself installs without admin rights.
// Silent installs skip prerequisite installation.
const
  NpcapUrl = 'https://npcap.com/dist/npcap-1.89.exe';
  NpcapFile = 'npcap-1.89.exe';
  NpcapSha256 = '8aed85e900d783d1308506e919587d3e540451947af8a82f2d04f819e44305cc';

var
  PrereqPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  NpcapIndex: Integer;
  NpcapDownloaded: Boolean;

function RestartRequested: Boolean;
begin
  Result := Pos('/RESTARTAPP', Uppercase(GetCmdTail)) > 0;
end;

#ifdef UpdateOnly
// The update keeps the installed map data: it must be there and be the one this version was built with.
function InitializeSetup: Boolean;
var
  Location, Manifest: String;
begin
  Result := False;
  if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2946C774-E30B-4C98-A366-108062322891}_is1',
    'InstallLocation', Location) then
  begin
    SuppressibleMsgBox(CustomMessage('UpdateNeedsInstall'), mbError, MB_OK, IDOK);
    Exit;
  end;
  Manifest := AddBackslash(Location) + 'mapdata\manifest.json';
  if not FileExists(Manifest) or (CompareText(GetSHA256OfFile(Manifest), '{#MapManifestSha256}') <> 0) then
  begin
    SuppressibleMsgBox(CustomMessage('UpdateNeedsMapData'), mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := True;
end;
#endif

function NpcapInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'));
end;

procedure InitializeWizard;
begin
  NpcapIndex := -1;
  PrereqPage := CreateInputOptionPage(wpSelectTasks, CustomMessage('PrereqCaption'), CustomMessage('PrereqDescription'),
    CustomMessage('PrereqSub'), False, False);
  if not NpcapInstalled then
    NpcapIndex := PrereqPage.Add(CustomMessage('PrereqNpcap'));
  if NpcapIndex >= 0 then PrereqPage.Values[NpcapIndex] := True;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function Chosen(Index: Integer): Boolean;
begin
  Result := False;
  if WizardSilent or (Index < 0) then Exit;
  Result := PrereqPage.Values[Index] and PrereqPage.CheckListBox.ItemEnabled[Index];
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = PrereqPage.ID) and (WizardSilent or (NpcapIndex < 0));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not Chosen(NpcapIndex) then
    Exit;
  DownloadPage.Clear;
  DownloadPage.Add(NpcapUrl, NpcapFile, NpcapSha256);
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      NpcapDownloaded := True;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox(FmtMessage(CustomMessage('PrereqDownloadFailed'), [GetExceptionMessage]), mbError, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if NpcapDownloaded then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('PrereqInstallingNpcap');
    if not ShellExec('runas', ExpandConstant('{tmp}\' + NpcapFile), '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode) then
      Log('Npcap installer not started: ' + SysErrorMessage(ErrorCode));
  end;
end;

// The finish page says what is in place now.
procedure CurPageChanged(CurPageID: Integer);
var
  Summary: String;
begin
  if (CurPageID <> wpFinished) or WizardSilent then
    Exit;
  Summary := CustomMessage('PrereqDone');
  if NpcapInstalled then
    Summary := Summary + #13#10 + '• ' + CustomMessage('PrereqNpcapOk')
  else
    Summary := Summary + #13#10 + '• ' + CustomMessage('PrereqNpcapMissing');
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Summary;
end;
