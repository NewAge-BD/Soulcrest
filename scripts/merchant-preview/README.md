# Händler-Vorschauen (offline)

Dieses Konsolenwerkzeug verwendet `MerchantCardLocator` sowie `BuildBadges` und `DrawBadges`
aus der produktiven App-Assembly. Es erzeugt ausschließlich Bitmaps. Es instanziiert weder
Soulcrest-Fenster noch Services, Capture oder Nutzerdatenspeicher und darf deshalb auch neben
der laufenden App für diese Offline-Prüfung verwendet werden.

Vom Repository-Stamm:

```powershell
dotnet run --project scripts/merchant-preview/MerchantPreview.csproj -c Release -- "$env:TEMP" "output/merchant-native"
```

Der erste Parameter enthält die fünf vom Nutzer am 2026-10-13 zugeordneten
`codex-clipboard-*.png`-Shopbilder; Dateinamen und UI-Skalen stehen in `Program.cs`.
Der zweite Parameter ist ein lokales Ausgabeverzeichnis (nicht ins Git).
Das vorhandene lokale Kartenpaket unter `imports/generated/mapdata/` muss `pets.json` und
`manifest.json` enthalten; fehlt es oder ist es leer, bricht das Werkzeug mit einer Erklärung ab.
Es lädt diese beiden Dateien direkt und verwendet die produktiven Kategorie-/Spawn-Helfer per
Reflection, ohne einen Service zu starten.

Je UI-Skala entstehen Originalansicht, auf 1920 px reduzierte Analyse mit Rückprojektion und
tatsächliche 1920×1080- und 1280×720-Ansichten. Jede Variante enthält normale Beispiel-Counter, MAX und
unbekannte Werte. Farmregionen und Pet-Kategorien stammen aus den produktiven Faktentabellen;
Spawnpunkt-Anzahlen aus den Monsterplatzierungen des lokalen Kartenpakets. Unbekannte Anzahlen
bleiben `?`. Die Anzahl beschreibt bekannte Platzierungen, keine gleichzeitig lebenden Monster. Im gemischten
Special-Tab werden nur die drei sichtbaren Pet-Karten ergänzt.

Die Pet-Kategorie steht mittig über dem nativen Petnamen, der Counter mittig unter dem Preis.
Header und Kartenfuß sind getrennte Zeichenbereiche; der Zwischenraum über Name und Artwork
bleibt vollständig transparent. Die Vorschau rendert die produktive `SurfaceBounds`-Union,
damit auch die oben liegende Kategorie enthalten ist.

Zusätzlich erzeugte `synthetic-stress`-Varianten setzen die Farmzeile bewusst auf
`Abyss (209) / Altgard (398) / Verteron (377)` und verteilen Cogni, Fera, Natura und Varian
auf die vorhandenen Pet-Karten. Bei den drei Pet-Karten des gemischten Tabs rotiert die Auswahl
zwischen den Auflösungsvarianten, damit ebenfalls alle vier Kategorien vorkommen.
Zusätzliche `synthetic-priority`-Originalansichten zeigen `0/5` mit `Altgard (12)` getrennt von
`Special` (auch zusammen mit unbekanntem Counter und MAX); deren Pet-Kategorien bleiben echt.
Diese Gestaltungs- und Breitentests behaupten keine solchen Quellen oder Stress-Kategorien für
die abgebildeten Pets.
Alle Counter sind illustrative Werte und lesen keinen echten Fortschritt.

Ausgaben: zusammengesetztes PNG, transparentes Overlay-PNG, JSON mit Ankern/Koordinaten und
Abdeckung sowie `manifest.json`. Die Berichte enthalten Kategorie, Farmzeile mit Anzahlen,
Kartenpaketversion, getrennte Koordinaten für Header, Fuß und Renderfläche sowie Kennzeichnungen
für synthetische Counter, Regionen, Anzahlen
und Kategorien. Der Renderer muss bei 96/144/192 DPI pixelidentisch bleiben;
fehlende Kartenanker oder abweichende DPI-Pixel brechen die Prüfung ab.
Die Screenshots, PNGs und das Manifest bleiben lokale, ignorierte Artefakte.

Optional erzeugt Pillow drei Vergleichstafeln aus den fertigen PNGs/JSON-Dateien. Die dynamisch
höheren Ausschnitte zeigen Kategorie, Petname, Artwork, Preis und Counter gemeinsam:

```powershell
python scripts/merchant-preview/make_gallery.py "output/merchant-native"
```
