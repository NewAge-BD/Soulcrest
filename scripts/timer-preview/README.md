# Native Timer-Leiste offline prüfen

Das Konsolenwerkzeug ruft ausschließlich `TimerStripRenderer.Layout`/`Paint` und
`TimerStripLockRenderer.Paint` und die reine Benachrichtigungszeichnung
`AlertsOverlayForm.PaintNotifications` aus der produktiven App-Assembly per Reflection auf.
Die Rift-Beispiele verwenden zusätzlich die produktive reine `EventTimerDisplay`-Projektion. Es erzeugt keine Form, keinen Dienst, keine
Erfassung und liest keine Nutzerdaten. Die ausgelieferten lokalen Timer-PNGs werden
über das Projekt nach `wwwroot/icons/events/` im Ausgabeverzeichnis kopiert. Ein Fallback
auf Downloads existiert nicht.

Vom Repository-Stamm mit einer selbst gewählten lokalen Referenz und Ausgabe:

```powershell
dotnet run --project scripts/timer-preview/TimerPreview.csproj -c Release -- "C:\Pfad\Referenz.png" "output/timer-preview"

# Optional: echte lokale Boss-Porträts aus dem vorhandenen Kartenpaket
dotnet run --project scripts/timer-preview/TimerPreview.csproj -c Release -- "C:\Pfad\Referenz.png" "output/timer-preview" "imports/generated/mapdata"
```

Der erste Parameter ist ein vorhandener vollständiger Spiel-Screenshot. Der zweite Parameter
ist das lokale Ausgabeverzeichnis. Das Werkzeug setzt die Leiste zur Gestaltungsprüfung an
Pixelposition `(104, 36)`, unmittelbar unter die Währungszeile der Referenz. Kleine Referenzen
begrenzen die produktive Layoutbreite; dadurch kann die Leiste in mehrere Zeilen umbrechen.
Der normale Betrieb bleibt frei verschiebbar und wird hier nicht gestartet.
Der optionale dritte Parameter bezeichnet ausschließlich ein lokales Kartenpaket. Aus dessen
Manifest und Boss-Katalogen werden die ersten zwei unterschiedlichen vorhandenen Porträts
ausgewählt und über den produktiven `TimerIconCache` geladen. Es wird kein Fortschritts- oder
Boss-Dienst instanziiert. Titel bleiben „Boss A“ und „Boss B“; ohne Bildquelle zeigt der produktive
Renderer das allgemeine Boss-Symbol. Eine weitere Beispielzeile prüft diesen Fallback auch mit
vorhandenen Porträts. Der Bericht nennt ausschließlich relative `map:`-Icon-IDs und öffentliche
Spawn-IDs, keine absoluten Kartenpfade oder gespeicherten Namen.

Alle Werte sind synthetisch und verwenden einen festen UTC-Zeitpunkt. Pro Sprache DE/EN
und Skalierung `0.6 / 1 / 1.25 / 2` entstehen zwölf Varianten: vier Event-Icons, aktive Events, Rift während der zehnminütigen
Öffnung mit sichtbarem Namen und direkt nach dem Schließen bis zum nächsten Start,
vorläufige Bossstände, Bosse/Reset/eigene Timer, Minuten ohne Sekunden, Porträts und neue Fallback-Icons,
entsperrt, kleine/große Abstände und schmale mehrzeilige Anordnung. Titel sind generisch;
die echten lokalen Event-Icons werden unverändert durch den produktiven Renderer gezeichnet.
Der Hintergrund ist immer vollständig transparent; eine Hintergrundoption existiert nicht.
Die native Schlossdarstellung wird rechts außerhalb der Leiste mit denselben physischen Maßen
wie im Overlay ergänzt (18 × Nutzerskalierung, mindestens 10 px; Abstand 3 × Nutzerskalierung).
Sie ist Bestandteil der DPI-Pixelprüfung; die gemeldete logische Layoutbreite betrifft weiterhin
nur die Timerzellen. `lock-states.png` vergleicht geschlossen, offen, Hover und gedrückt bei allen
vier Größen ohne ein natives Fenster zu erzeugen.

Ausgaben:

- 96 Renderer-PNGs und acht Kontaktbögen mit Größen-/Zustandsvergleich.
- 32 zusätzliche native Bannerbilder in DE/EN bei 240/400 px Breite und denselben vier
  Skalierungen: vor und nach Ablauf einer Vorwarnung. Die abgelaufene Zeile fehlt im zweiten
  Layoutsnapshot; keine Wartemeldung. Die tatsächliche Quellen-/Bannerentfernung prüfen die
  Laufzeittests. Die Bilder benötigen ebenfalls keine Form oder Dienste.
- Montagen auf der Referenz sowie Ausschnitte einschließlich Währungszeile: alle Varianten
  bei Skala 1 und die normale Viererleiste zusätzlich bei den anderen Skalierungen.
- `timer-preview.json` mit Zell-/Icon-/Text-/Namensgrenzen, Einstellungen, Asset-Hashes und Rohpixel-Hashes
  bei 96/144/192 DPI. Die DPI-Varianten müssen bei gleicher Nutzerskalierung pixelidentisch sein;
  abweichende Bilder werden zusätzlich gespeichert, das Werkzeug endet dann mit Exitcode 1.

PNG-Metadaten und Kompression gehen nicht in den Pixelvergleich ein. Es wird genau einmal
mit der gewählten Nutzerskalierung gezeichnet; DPI ist ausschließlich die Vergleichsvariable.
Fehlende Assets, ungültige Layoutgrenzen und Rendererfehler brechen mit einer Meldung ab.

Referenzen, Montagen, Kontaktbögen und Berichte bleiben lokale Artefakte und gehören nicht ins Git.
Die Referenz kann echte Namen enthalten; das Werkzeug übernimmt deren Pixel ausschließlich in
die lokalen Montagen. Quellcode und JSON-Bericht enthalten keine solchen Namen oder absoluten
Referenzpfade. Version, Patchnotes und Nutzereinstellungen werden nicht geändert.
