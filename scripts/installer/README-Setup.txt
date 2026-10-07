Soulcrest - privater Windows-Build

Dieses Setup installiert den lokalen Projektstand einschliesslich Offline-Karten
und .NET-Laufzeit fuer Windows x64. Ein .NET SDK ist zum Starten nicht erforderlich.

Voraussetzungen:
- Windows 10 ab Version 2004 oder Windows 11 (x64)
- Microsoft Edge WebView2 Runtime
- Windows-OCR-Sprachpaket Englisch bzw. Deutsch fuer die gewaehlte OCR-Sprache
- Microsoft Visual C++ Redistributable 2015-2022 x64 fuer native Komponenten

Voraussetzungen prueft das Setup selbst (Seite "Voraussetzungen"):
- Npcap fuer das Loot-Tracking: fehlt es, laedt das Setup auf Wunsch den offiziellen
  Npcap-Installer 1.89 von npcap.com (Pruefsumme festgelegt) und startet ihn. Npcap
  wird nicht mitgeliefert; seine Lizenz zeigt der Npcap-Installer.
- Windows-OCR-Paket Englisch (Spielclient Englisch) bzw. Deutsch: fehlt es, installiert
  Windows es auf Wunsch selbst (DISM).
Beides fragt einmal nach Administratorrechten. Fuer Karte und OCR ist Npcap nicht noetig.

Installation: nur fuer das aktuelle Windows-Benutzerkonto, ohne Administratorrechte.
Ein Startmenueeintrag wird angelegt; eine Desktopverknuepfung ist optional.
Fortschritt und Einstellungen liegen unter %LOCALAPPDATA%\Soulcrest und bleiben
bei einer Deinstallation erhalten. Bestehende Soulcrest-Profile werden weiterverwendet.

Die enthaltenen Kartendaten sind ausschliesslich fuer die private Nutzung bestimmt.
Quellen und Lizenzhinweise: THIRD_PARTY_NOTICES.md im Installationsordner.

Dies ist ein lokaler, nicht digital signierter Build.
