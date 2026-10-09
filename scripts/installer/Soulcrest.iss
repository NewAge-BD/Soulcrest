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
Source: "check-ocr.ps1"; Flags: dontcopy

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
german.PrereqDescription=Npcap und Windows-OCR
german.PrereqSub=Soulcrest braucht Npcap fürs Loot-Tracking und das Windows-OCR-Paket für die Sprache deines Spielclients. Angehakte Teile installiert das Setup jetzt; Windows fragt dafür einmal nach Administratorrechten.
german.PrereqNpcap=Npcap 1.89 herunterladen und installieren (npcap.com, für das Loot-Tracking). Im Npcap-Installer die Voreinstellungen übernehmen; die kostenlose Npcap-Lizenz erlaubt bis zu 5 Installationen.
german.PrereqOcrEn=Englisches OCR-Paket installieren (Spielclient auf Englisch, Standard)
german.PrereqOcrDe=Deutsches OCR-Paket installieren (nur für den deutschen Spielclient nötig)
german.PrereqDownloadFailed=Npcap konnte nicht heruntergeladen werden: %1%n%nDu kannst es später selbst von https://npcap.com installieren. Die Installation von Soulcrest geht weiter.
german.PrereqInstallingOcr=Windows-OCR-Paket %1 wird installiert …
german.PrereqInstallingNpcap=Npcap-Installer läuft …
german.PrereqOcrPatience=Windows lädt das Sprachpaket herunter und installiert es. Das kann einige Minuten dauern – das Setup läuft weiter, bitte nicht abbrechen.
german.PrereqDone=Voraussetzungen:
german.PrereqNpcapOk=Npcap: installiert
german.PrereqNpcapMissing=Npcap: fehlt noch (https://npcap.com); ohne Npcap kein Loot-Tracking
german.PrereqOcrOk=OCR %1: installiert
german.PrereqOcrMissing=OCR %1: noch nicht verfügbar; nach einem Windows-Neustart erneut in Soulcrest unter Optionen prüfen
english.UpdateNeedsInstall=This update needs an installed Soulcrest. Please use the full installer (Soulcrest-{#AppVersion}-Setup-win-x64.exe).
english.UpdateNeedsMapData=This update contains no map data, and the installed map data does not match. Please use the full installer (Soulcrest-{#AppVersion}-Setup-win-x64.exe).
english.PrereqCaption=Requirements
english.PrereqDescription=Npcap and Windows OCR
english.PrereqSub=Soulcrest needs Npcap for loot tracking and the Windows OCR package for your game client's language. Setup installs the ticked parts now; Windows asks for administrator rights once.
english.PrereqNpcap=Download and install Npcap 1.89 (npcap.com, for loot tracking). Keep the defaults in the Npcap installer; the free Npcap licence allows up to 5 installations.
english.PrereqOcrEn=Install the English OCR package (game client in English, default)
english.PrereqOcrDe=Install the German OCR package (only for the German game client)
english.PrereqDownloadFailed=Npcap could not be downloaded: %1%n%nYou can install it later from https://npcap.com. Soulcrest setup continues.
english.PrereqInstallingOcr=Installing the Windows OCR package %1 …
english.PrereqInstallingNpcap=Npcap installer running …
english.PrereqOcrPatience=Windows is downloading and installing the language package. This can take several minutes – setup is still working, please do not cancel.
english.PrereqDone=Requirements:
english.PrereqNpcapOk=Npcap: installed
english.PrereqNpcapMissing=Npcap: still missing (https://npcap.com); no loot tracking without Npcap
english.PrereqOcrOk=OCR %1: installed
english.PrereqOcrMissing=OCR %1: not available yet; after a Windows restart check again in Soulcrest under Options

[Code]
// Requirements (user request 2026-10-07): Npcap and the Windows OCR package are checked and, when
// missing, installed on request. Npcap may not be bundled (free licence), so the official installer
// is downloaded from npcap.com (pinned version and SHA-256, signed by Nmap Software LLC) and runs
// interactively. OCR comes from Windows itself (DISM capability). Both need elevation; Soulcrest itself
// installs without admin rights. Silent installs skip all of this.
const
  NpcapUrl = 'https://npcap.com/dist/npcap-1.89.exe';
  NpcapFile = 'npcap-1.89.exe';
  NpcapSha256 = '8aed85e900d783d1308506e919587d3e540451947af8a82f2d04f819e44305cc';

var
  PrereqPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  OcrLanguages: String;
  NpcapIndex, OcrEnIndex, OcrDeIndex: Integer;
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

// Installed OCR languages as ",de-de,en-us," (lower case), read the way Soulcrest reads them.
function ReadOcrLanguages: String;
var
  Output: TExecOutput;
  ResultCode, I: Integer;
begin
  Result := ',';
  try
    ExtractTemporaryFile('check-ocr.ps1');
    if ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\check-ocr.ps1') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode, Output) then
      for I := 0 to GetArrayLength(Output.StdOut) - 1 do
        Result := Result + Lowercase(Trim(Output.StdOut[I])) + ',';
  except
    Log('OCR check failed: ' + GetExceptionMessage);
  end;
  Log('OCR languages: ' + Result);
end;

function HasOcr(Prefix: String): Boolean;
begin
  Result := Pos(',' + Prefix + '-', OcrLanguages) > 0;
end;

procedure InitializeWizard;
begin
  OcrLanguages := ReadOcrLanguages;
  NpcapIndex := -1;
  OcrEnIndex := -1;
  OcrDeIndex := -1;
  PrereqPage := CreateInputOptionPage(wpSelectTasks, CustomMessage('PrereqCaption'), CustomMessage('PrereqDescription'),
    CustomMessage('PrereqSub'), False, False);
  if not NpcapInstalled then
    NpcapIndex := PrereqPage.Add(CustomMessage('PrereqNpcap'));
  if not HasOcr('en') then
    OcrEnIndex := PrereqPage.Add(CustomMessage('PrereqOcrEn'));
  if not HasOcr('de') then
    OcrDeIndex := PrereqPage.Add(CustomMessage('PrereqOcrDe'));
  if NpcapIndex >= 0 then PrereqPage.Values[NpcapIndex] := True;
  if OcrEnIndex >= 0 then PrereqPage.Values[OcrEnIndex] := True;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function Chosen(Index: Integer): Boolean;
begin
  Result := (Index >= 0) and PrereqPage.Values[Index] and not WizardSilent;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = PrereqPage.ID) and (NpcapIndex < 0) and (OcrEnIndex < 0) and (OcrDeIndex < 0);
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

// DISM downloads the package from Windows Update: several minutes in which the bar stood at 100 % and
// setup looked stuck (user report 2026-10-09). The bar runs as a marquee meanwhile (setup keeps pumping
// messages while it waits) and a second line says that this takes a while.
procedure InstallOcr(Tag: String);
var
  ErrorCode: Integer;
begin
  WizardForm.StatusLabel.Caption := FmtMessage(CustomMessage('PrereqInstallingOcr'), [Tag]);
  WizardForm.FilenameLabel.Caption := CustomMessage('PrereqOcrPatience');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not ShellExec('runas', ExpandConstant('{sys}\dism.exe'),
      '/Online /Add-Capability /CapabilityName:Language.OCR~~~' + Tag + '~0.0.1.0 /NoRestart /Quiet',
      '', SW_HIDE, ewWaitUntilTerminated, ErrorCode) then
      Log('DISM ' + Tag + ' not started: ' + SysErrorMessage(ErrorCode));
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
    WizardForm.FilenameLabel.Caption := '';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if Chosen(OcrEnIndex) then InstallOcr('en-US');
  if Chosen(OcrDeIndex) then InstallOcr('de-DE');
  if NpcapDownloaded then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('PrereqInstallingNpcap');
    if not ShellExec('runas', ExpandConstant('{tmp}\' + NpcapFile), '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode) then
      Log('Npcap installer not started: ' + SysErrorMessage(ErrorCode));
  end;
  if Chosen(OcrEnIndex) or Chosen(OcrDeIndex) then
    OcrLanguages := ReadOcrLanguages;
end;

// The finish page says what is in place now.
procedure CurPageChanged(CurPageID: Integer);
var
  Summary: String;
begin
  if (CurPageID <> wpFinished) or (NpcapIndex < 0) and (OcrEnIndex < 0) and (OcrDeIndex < 0) then
    Exit;
  Summary := CustomMessage('PrereqDone');
  if NpcapInstalled then
    Summary := Summary + #13#10 + '• ' + CustomMessage('PrereqNpcapOk')
  else
    Summary := Summary + #13#10 + '• ' + CustomMessage('PrereqNpcapMissing');
  if HasOcr('en') then
    Summary := Summary + #13#10 + '• ' + FmtMessage(CustomMessage('PrereqOcrOk'), ['en-US'])
  else
    Summary := Summary + #13#10 + '• ' + FmtMessage(CustomMessage('PrereqOcrMissing'), ['en-US']);
  if Chosen(OcrDeIndex) then
    if HasOcr('de') then
      Summary := Summary + #13#10 + '• ' + FmtMessage(CustomMessage('PrereqOcrOk'), ['de-DE'])
    else
      Summary := Summary + #13#10 + '• ' + FmtMessage(CustomMessage('PrereqOcrMissing'), ['de-DE']);
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Summary;
end;
