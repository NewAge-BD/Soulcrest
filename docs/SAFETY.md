# Technische Sicherheitsgrenze

Aion 2 hat einen Anti-Cheat. Soulcrest bleibt deshalb strikt **passiv** und verhält sich wie
eine Bildschirmaufnahme- plus Overlay-Software.

## Soulcrest darf

- Pixel des Aion-2-Fensters per Windows Graphics Capture lesen (Fenster per HWND und
  Prozess-ID gebunden; kein Rückfall auf eine Desktopaufnahme ohne ausdrückliche Wahl);
- Fenstergeometrie, Monitor, DPI, Vordergrund- und Minimierungsstatus des Spielfensters lesen;
- eigene transparente Fenster zeichnen und globale Hotkeys für **eigene** Funktionen registrieren;
- lokale Dateien im eigenen Datenordner `%LOCALAPPDATA%\Soulcrest\` und im App-Verzeichnis
  lesen und schreiben.

## Soulcrest darf nie

- Spielprozess-Speicher lesen oder schreiben, Module laden/injizieren, DLLs in das Spiel bringen;
- Netzwerkverkehr des Spiels verändern oder senden; mitschneiden nur in der Ausnahme unten;
- Tastatur- oder Mauseingaben an das Spiel senden oder Spielaktionen automatisieren;
- Spieldateien verändern;
- zur Laufzeit Daten von Drittseiten abrufen. Kein Telemetrie- oder Update-Netzwerkzugriff in v1.
  Der einmalige Kartendatenimport ist ein Entwicklerskript, kein App-Pfad;
- bisheriger Kartenanbieter abrufen, scrapen oder deren Token nachbauen (siehe `MAP_DATA.md`).

## Ausnahme: Netzwerk-Mitschnitt für das Loot-Tracking (Nutzerentscheidung 2026-10-03)

Der Nutzer hat entschieden, dass das Loot-Tracking den eigenen Spielverkehr als Alternative zur OCR
auswerten darf (DPS-Meter sind für Aion 2 laut Nutzer erlaubt). Dafür gilt:

- **standardmäßig aktiv auf ausdrücklichen Nutzerwunsch vom 2026-10-04**, dauerhaft ausschaltbar; nur per Npcap/`dumpcap`, nur **lesend**;
- nur der Verkehr des Spielprozesses (Filter auf dessen TCP-Gegenstellen und UDP-Ports), kein
  sonstiger Rechnerverkehr;
- **keine Entschlüsselung**, kein Auslesen von Schlüsseln aus Client, Speicher oder Dateien, kein
  Senden oder Verändern von Paketen. Ist der Verkehr verschlüsselt, ist dieser Weg zu Ende;
- Mitschnitte liegen nur lokal unter `%LOCALAPPDATA%\Soulcrest\captures`.
- **Charaktererkennung (Nutzerentscheidung 2026-10-06):** Aus demselben Mitschnitt wird nur der
  **eigene** Charaktername gelesen, aus der Nachricht `33 36` beim Betreten der Welt. Damit folgt
  das Erkundungsprofil dem eingeloggten Charakter. Namen oder Positionen anderer Spieler werden
  nicht ausgewertet. Echte Charakternamen kommen nicht ins Repository.

Aufnahme: `scripts/Record-GameTraffic.ps1`.

## Datenschutz

- Bilddaten bleiben ohne aktivierte Diagnose im Arbeitsspeicher.
- Diagnoseaufzeichnungen (Feed-/Chat-Ausschnitte) sind opt-in, starten ausgeschaltet und liegen
  nur lokal. Chat-Ausschnitte können Spielernamen und Chatnachrichten anderer enthalten. Darauf
  in der UI hinweisen und Aufbewahrung begrenzen (Standard 3 h, wie Grindcrest).
- Overlays sind standardmäßig aus Bildschirmaufnahmen ausgeblendet.

Die Nutzung erfolgt auf eigenes Risiko. Ob Overlays und Bildschirmauswertung erlaubt sind,
entscheiden die Nutzungsbedingungen von NCSOFT.
