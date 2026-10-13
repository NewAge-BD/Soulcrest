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

Je UI-Skala entstehen Originalansicht, auf 1920 px reduzierte Analyse mit Rückprojektion und
tatsächliche 1920×1080- und 1280×720-Ansichten. Jede Variante enthält normale Beispiel-Counter, MAX und
unbekannte Werte. Farmregionen stammen aus dem vorhandenen Offline-Katalog. Im gemischten
Special-Tab werden nur die drei sichtbaren Pet-Karten ergänzt.

Zusätzlich erzeugte `synthetic-stress`-Varianten setzen die Farmzeile bewusst auf alle drei
Regionen. Zusätzliche `synthetic-priority`-Originalansichten zeigen `0/5` getrennt von `Special`
(auch zusammen mit unbekanntem Counter und MAX). Diese Gestaltungs- und Breitentests
behaupten keine solchen Quellen für die abgebildeten Pets.
Alle Counter sind illustrative Werte und lesen keinen echten Fortschritt.

Ausgaben: zusammengesetztes PNG, transparentes Overlay-PNG, JSON mit Ankern/Koordinaten und
Abdeckung sowie `manifest.json`. Der Renderer muss bei 96/144/192 DPI pixelidentisch bleiben;
fehlende Kartenanker oder abweichende DPI-Pixel brechen die Prüfung ab.
Die Screenshots, PNGs und das Manifest bleiben lokale, ignorierte Artefakte.

Optional erzeugt Pillow drei Vergleichstafeln aus den fertigen PNGs/JSON-Dateien:

```powershell
python scripts/merchant-preview/make_gallery.py "output/merchant-native"
```
