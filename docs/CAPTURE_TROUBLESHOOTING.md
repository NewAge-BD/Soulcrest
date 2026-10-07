# Aufnahme bleibt bei 0 FPS: Korrekturen und Diagnose (0.1.5)

## Befund

Auf dem Rechner eines Kollegen erscheint der Windows-Aufnahmerahmen, aber Loot-Tracker und
Karten-Vorschau liefern kein Bild. Es liegt noch kein Diagnosebericht dieses Rechners vor.
Die konkrete Ursache dort ist deshalb **nicht bestätigt**. Der Rahmen allein bestätigt nur den
Start einer Windows-Aufnahmesitzung, nicht die erfolgreiche Weiterverarbeitung ihrer Bilder.

Ausgangspunkt war der inzwischen weiterentwickelte Stand `aaf1283`, Version 0.1.4, mit dem
bereits vorhandenen Diagnose-Werkzeug. Diese Arbeit erhält dessen Funktionen und die Änderungen
an Kartenfiltern und Installer aus dem anderen Fenster.

## Änderungen

- Der von WGC und Soulcrest genutzte Direct3D-Kontext aktiviert `ID3D11Multithread`-Schutz.
  Eine C#-Sperre allein schützt keine internen Windows-Zugriffe auf diesen Kontext.
- Bei Größenwechseln werden gehaltene Bilder freigegeben, bevor der Frame-Pool neu angelegt wird.
  Der nächste Leser erhält ein Bild aus dem neuen Pool. Größen von null werden verworfen.
- Bildempfang, Größenwechsel und Freigabe werden abgestimmt. Fehler im Callback werden mit
  HRESULT protokolliert. Die Sitzung wird beim Beenden außerhalb der Callback-Sperre geschlossen.
- Die optionale Windows-Freigabe zum Entfernen des gelben Rahmens läuft im Hintergrund.
  Der erste Aufnahmeversuch wartet nicht mehr auf diesen Dialog. Der Rahmen kann sichtbar bleiben.
- Der Diagnosebericht liest einen Snapshot ohne die Aufnahmesperre. Er nennt den aktuellen
  Verarbeitungsschritt samt Wartezeit und hält Aufnahmefehler mit HRESULT fest.
- Das Diagnose-ZIP verwendet vorhandene Vorschauen. Fehlen sie, enthält es eine entsprechende
  Notiz. Es startet keine zusätzliche Aufnahme, die am selben Fehler hängen könnte.

Es wurde kein neuer Desktop-Fallback eingeführt. Der bisherige Aufnahmeweg bleibt erhalten.
Das ist keine bestätigte Behebung auf dem betroffenen Rechner; dafür ist dessen Test erforderlich.

## Prüfung

- Release-Suite: 174 Tests bestanden (110 Core, 21 OCR, 43 App).
- Anschließend sechs gezielte Tests bestanden: Diagnose trotz gehaltener Aufnahmesperre,
  vorhandene PNG/JPEG-Vorschauen, fehlende/defekte Vorschauen und echte WGC-Aufnahme.
- Der WGC-Test öffnet ausschließlich ein eigenes Testfenster, prüft dessen rote Bildpixel,
  vergrößert das Fenster und prüft anschließend blaue Pixel. Auf diesem Rechner bestanden.
  Er benötigt einen interaktiven Windows-Desktop und läuft nur nach explizitem Opt-in.
- Kein Spielstart, kein Zugriff auf Spielspeicher oder Spieldateien, keine Aufnahme des Spiels.
- Grenzen: anderer GPU-Treiber, mehrere GPUs, HDR sowie das Spiel auf dem Rechner des Kollegen
  wurden damit nicht nachgestellt.

```powershell
dotnet test Soulcrest.slnx -c Release --nologo --logger 'trx;LogFilePrefix=capture-fix' --results-directory artifacts/capture-fix-validation
$env:SOULCREST_TEST_OWN_WINDOW_CAPTURE = '1'
try {
    dotnet test tests/Soulcrest.App.Tests -c Release --nologo --filter 'FullyQualifiedName~OwnWindowCaptureTests|FullyQualifiedName~CaptureDiagnosticsTests'
} finally { Remove-Item Env:\SOULCREST_TEST_OWN_WINDOW_CAPTURE }
.\scripts\Build-Installer.ps1
```

.NET SDK 9.0.317; Installer mit Inno Setup 7.1.0. Die Versionsnummer ist 0.1.5.

## Nächster Test beim Kollegen

1. Neue Version installieren und Soulcrest neu starten.
2. Spiel sichtbar im randlosen Fenstermodus lassen und den Loot-Tracker starten.
3. Falls weiterhin kein Bild erscheint: nach etwa 30 Sekunden unter **Info → Diagnose**
   auf **Diagnose-Paket erstellen** klicken.
4. Die ZIP-Datei vom Desktop weitergeben. Sie enthält Bericht, Logs, Einstellungen und bereits
   vorhandene kleine Vorschauen. Im Bericht ist insbesondere der Abschnitt **Aufnahme** relevant.

## Technische Referenzen

- [Microsoft: ID3D11Multithread](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nn-d3d11_4-id3d11multithread)
- [Microsoft: Capture-Beispiel, Frame-Freigabe vor Recreate](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/dotnet/WPF/ScreenCapture/CaptureSampleCore/BasicCapture.cs)
