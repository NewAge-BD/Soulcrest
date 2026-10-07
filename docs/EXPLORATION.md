# Charakterbezogene Exploration

## Bedienung

1. Tab **Sealed Dungeons** oder **Stronghold** öffnen. Ein Charakterprofil anlegen (Name und Server frei benennbar) oder auswählen.
2. Die Karte passend zur Spielkarte wählen. Im Spiel denselben Exploration-Tab öffnen.
3. Der Scan nutzt in allen Tabs dieselbe feste Maske: linke 22 % des Spielfensters, vertikal 12–98 %. Das entspricht der maximalen UI-Skalierung im bereitgestellten Screenshot (563 px Breite bei 2559 px). Eine Bereichsauswahl oder Layout-Erkennung entfällt.
4. Liste ganz nach oben scrollen, **Scan starten**, dann langsam nach unten scrollen. Jede Seite etwa zwei Sekunden stehen lassen. Vorschau und Zähler zeigen die Aufnahme. Das Scan-Overlay rahmt den Listenbereich ein: grüne Zeilen sind bestätigt, orange Zeilen noch nicht. Erst bei „weiterscrollen“ zur nächsten Seite gehen; sonst kurz stillhalten oder unklare Namen später manuell prüfen. **Scan stoppen** bleibt jederzeit verfügbar.
5. Namen müssen in zwei aufeinanderfolgenden Aufnahmen eindeutig passen. Zwei Aufnahmen mit „Undiscovered“ beenden den Scan; Namen darunter werden ignoriert. Vorher erkannte Orte bleiben erhalten. Bei vollständig entdeckter Liste ohne diesen Eintrag manuell stoppen.
6. Nicht eindeutige Texte werden angezeigt. Die Liste darunter erlaubt manuelle Korrekturen. Nicht sichtbare Orte werden niemals automatisch zurückgesetzt.
7. Auf der Karte den Progressionsmodus einschalten und **Sealed Dungeons**, **Stronghold** oder **Sealed Dungeons + Stronghold** wählen. Das nächste offene Ziel auf der aktuellen Karte wird anhand der Spielerposition bestimmt; ohne Position anhand der Kartenmitte. **Ziel als fertig markieren** führt zum nächsten Ort. Fertige Marker tragen einen grünen Haken.

Der Scan speichert „entdeckt“ gemäß Nutzerauftrag als „fertig“. Er unterscheidet keine zusätzlichen Dungeon-Abschlussbedingungen.

## Speicherung und Abgleich

- `exploration.json` im Soulcrest-Datenordner: Charakterprofile mit zufälligen IDs, aktivem Profil und fertigen Orts-IDs. Atomare Speicherung. Bestehender serverweiter Pet-Fortschritt bleibt getrennt.
- Orts-ID aus Karte, Kategorie und englischem Quellnamen; Positionskorrekturen ändern den Fortschritt nicht. Eine künftige Umbenennung der Quelldaten benötigt ggf. eine Migration.
- Quelle: bestehende lokale `data.js`-Dateien des gaming.tools-Kartenpakets. Ausschließlich Kategorien `Sealed Dungeon` und `Stronghold`; die unspezifische Kategorie `Dungeon` gehört nicht dazu.
- Exakte normalisierte EN/DE-Namen bevorzugt; ansonsten Ähnlichkeit mindestens 90 %, mindestens 8 Prozentpunkte Abstand zum zweiten Kandidaten. Entrance/Eingang-Zusätze werden entfernt.
- Zwei Altgard-Aliase aus den Nutzer-Screenshots vom 04.10.2026: `Lost Ruins` → `Lost Ruin Entrance`, `Fafnite Storage Room` → `Fafnite Storage Entrance`.
- Scan bindet Karte, Kategorie und Charakter beim Start. Ein Profilwechsel in der Oberfläche ist während des Scans gesperrt.
- Nur passive Aufnahme des Spielfensters. Desktop-/GDI-Ersatzbilder werden abgewiesen, um Soulcrests eigene Namensliste nicht als Spielbefund zu lesen. Vorübergehend belegte Aufnahme wird erneut versucht. Keine Spieleingaben.

## Prüfung

- Beide bereitgestellten englischen Screenshots per Windows OCR gelesen: 14/14 und 9/9 Namen; zweite Aufnahme mit erkanntem Undiscovered-Ende.
- Tests für separate Charakterprofile und Wiederladen, manuelle Rücknahme, konservativen Abgleich, 15 Altgard-Stronghold-Namen, Ende der Liste und Bestätigung über zwei Aufnahmen.
- Node-Tests für Kategorienwahl, nächstes offenes Ziel, Profilwechsel, Fertig-Haken sowie bestehende Pet-Progression und Pet-Filter.
- Ein Live-Scan im Spiel, ein deutscher Spiel-Screenshot und ein Stronghold-Spiel-Screenshot wurden noch nicht geprüft.

Befehle:
```powershell
dotnet build Soulcrest.slnx -c Release
dotnet test tests/Soulcrest.App.Tests/Soulcrest.App.Tests.csproj -c Release --filter "FullyQualifiedName~ExplorationTests|FullyQualifiedName~RegionSelectorTests|FullyQualifiedName~CaptureDiagnosticsTests"
node --test tests/exploration-map.test.cjs tests/map-filter.test.cjs
# Optional: SOULCREST_EXPLORATION_SCREENSHOT auf einen der bereitgestellten Screenshots setzen.
```


## Feste Scanmaske und Overlay (04.10.2026)

Auf Nutzerkorrektur wird keine Überschrift zur Bereichserkennung verwendet. Die feste Maske deckt die größte Listenbreite ab und ist für alle Tabs identisch. Sie skaliert ausschließlich mit der Größe des Spielfensters; der Fensterursprung bestimmt die Bildschirmposition des Overlays. Die Namenszuordnung verwendet weiterhin die in Soulcrest gewählte Karte und Kategorie. Bekannte Listenüberschriften werden bei der OCR-Auswertung ignoriert.

Das Overlay bleibt durchklickbar und von der Aufnahme ausgeschlossen. Es zeigt die Maske, bestätigte/unklare Zeilen sowie Scrollhinweise und Zähler. Die Abschlussmeldung bleibt fünf Sekunden stehen.

Prüfung: dieselbe 563 px breite Maske auf allen vier Screenshots; Dungeon-Erkennung weiterhin 14/14 und 9/9 Namen einschließlich Undiscovered-Ende. 13 gezielte Tests bestanden, einschließlich Auflösungsverhältnissen, OCR, Overlay-Rendern und Charakterfortschritt. Live-Spielbetrieb nicht geprüft.

Screenshot-Tests: `SOULCREST_LAYOUT_FIXTURES` auf den Ordner der vier bereitgestellten PNGs setzen; Testfilter `FullyQualifiedName~ExplorationLayoutTests`.

## Automatischer Abschluss bei Ankunft

Auf der Karte im Exploration-Progressionsbereich **Abschlussradius (Kartenpixel)** einstellen (Standard 40; 0 deaktiviert, maximal 500). Der angezeigte Kreis skaliert mit der Karte. Bei laufendem Spieler-Tracking wird das aktuelle Ziel innerhalb des Radius im aktiven Charakterprofil als fertig gespeichert und das nächste offene Ziel gewählt. Manuelle Korrekturen bleiben im jeweiligen Scan-Tab möglich. Der Abschluss bedeutet hier Ankunft am Ort; zusätzliche Dungeon-Abschlussbedingungen werden nicht geprüft.

## Freischaltung von Sealed Dungeons (Nutzerangabe 2026-10-07, nach aion2maps)

- Drei Sealed Dungeons je Karte öffnen nacheinander: Verteron (Elyos) Distorted Cave → Fissure Cave →
  Rift Cave, Altgard (Asmodier) Lost Ruin → Twisted Pit → Rift Fissure.
- „Ruins of the Ancient City of Ru“ (Verteron und Altgard) öffnet erst, wenn alle anderen Sealed Dungeons
  derselben Karte erledigt sind.
- Der Progressionsmodus überspringt einen gesperrten Dungeon; das Popup der Karte nennt, was fehlt
  (`ExplorationService.Prerequisites`/`MissingFor`).
