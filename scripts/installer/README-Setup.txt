Soulcrest - privater Windows-Build

Dieses Setup installiert den lokalen Projektstand einschliesslich Offline-Karten
und .NET-Laufzeit fuer Windows x64. Ein .NET SDK ist zum Starten nicht erforderlich.

Voraussetzungen:
- Windows 10 ab Version 2004 oder Windows 11 (x64)
- Microsoft Edge WebView2 Runtime
- Npcap fuer passiven Pet-, Erkundungs- und Loot-Fortschritt
- Microsoft Visual C++ Redistributable 2015-2022 x64 fuer native Komponenten

Npcap prueft das Setup selbst (Seite "Voraussetzungen"):
- Fehlt es, laedt das Setup auf Wunsch den offiziellen Npcap-Installer 1.89 von
  npcap.com (Pruefsumme festgelegt) und startet ihn. Npcap wird nicht mitgeliefert;
  seine Lizenz zeigt der Npcap-Installer. Windows fragt dafuer nach Administratorrechten.
- Die Offline-Karte und das Spieler-Tracking funktionieren auch ohne Npcap.
- Windows-OCR-Sprachpakete werden nicht benoetigt oder installiert.

Installation: nur fuer das aktuelle Windows-Benutzerkonto, ohne Administratorrechte.
Ein Startmenueeintrag wird angelegt; eine Desktopverknuepfung ist optional.
Fortschritt und Einstellungen liegen unter %LOCALAPPDATA%\Soulcrest und bleiben
bei einer Deinstallation erhalten. Bestehende Soulcrest-Profile werden weiterverwendet.

Die enthaltenen Kartendaten sind ausschliesslich fuer die private Nutzung bestimmt.
Quellen und Lizenzhinweise: THIRD_PARTY_NOTICES.md im Installationsordner.

Dies ist ein lokaler, nicht digital signierter Build.
