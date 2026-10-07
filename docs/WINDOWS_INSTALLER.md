# Privater Windows-Installer

Der Installer enthält den lokalen Soulcrest-Stand, die .NET-Laufzeit und das bereits
generierte Offline-Kartendatenpaket. Er installiert pro Benutzer nach
`%LOCALAPPDATA%\Programs\Soulcrest`, legt einen Startmenüeintrag an und bietet optional
eine Desktopverknüpfung an. Die Deinstallation erhält `%LOCALAPPDATA%\Soulcrest`.

## Erzeugen

Voraussetzungen zum Bauen: .NET SDK 9, Windows PowerShell 5.1 oder PowerShell 7, Inno Setup und
`imports/generated/mapdata/manifest.json`. Der Installer lädt keine Daten nach.

```powershell
./scripts/Build-Installer.ps1 -Compiler './artifacts/tools/inno-7.1.0/ISCC.exe'
```

Ausgabe: `artifacts/installer/Soulcrest-<Version>-Setup-win-x64.exe`, SHA256-Datei und
`BUILD-INFO.json` mit Quellrevision und Kartendaten-Prüfsumme. Das frische Publish-Verzeichnis
liegt unter `artifacts/installer-build/<Zeitstempel>/payload`.
Ohne `-Version` verwenden Build und Installationstest die Version aus `Directory.Build.props`.

Geprüft mit Inno Setup 7.1.0, offiziell von
<https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0>. Der signierte Tool-Installer
wurde portabel mit `/PORTABLE=1 /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS`
und `/DIR="<Repository>\artifacts\tools\inno-7.1.0"` entpackt. Kein globaler Tool-Eintrag.
SHA256 des Tool-Installers:
`0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`.

## Prüfen

```powershell
dotnet test Soulcrest.slnx -c Release
./scripts/Test-Installer.ps1
```

Der Installationstest benutzt einen eigenen Ordner unter `artifacts/installer-validation`,
leitet App-Nutzerdaten über `SOULCREST_DATA` dorthin um und entfernt anschließend seine
Testinstallation. Er prüft den echten UI-/Karten-/Capture-/OpenCV-Selbsttest, erneute
Installation derselben Version und Deinstallation. Bei einer bereits registrierten
Soulcrest-Installation bricht er vorher ab, um diese nicht zu verändern.
Wenn bereits Soulcrest oder der Desktop-Tester läuft, wird der App-Selbsttest ausdrücklich
übersprungen; die vorhandene Instanz wird nicht beendet. Installation, erneute Installation
und Deinstallation werden trotzdem geprüft.

Auf dem Zielrechner werden weiterhin WebView2, passende Windows-OCR-Sprachpakete und
gegebenenfalls die Visual-C++-Laufzeit x64 benötigt. Npcap ist nur für die optionale
Netzwerk-Erfassung nötig und wird nicht mitgeliefert. Ein .NET SDK ist nicht erforderlich.
Der private Soulcrest-Installer ist nicht digital signiert.
