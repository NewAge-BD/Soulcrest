# Charakterbezogene Exploration

## Scannen auf Deutsch und Englisch (2026-10-08)

Im Reiter **Erkundungsfortschritt scannen** liest ein gemeinsamer Scan Kibelisks, versiegelte
Dungeons und Garnisonen. Unter **Sprache im Spiel (Scan)** stehen **Automatisch (Deutsch/Englisch)**,
**Deutsch** und **English** bereit. Die gespeicherte Auswahl gilt ab dem nächsten Scan, auch für
Pets; die Oberfläche bleibt unabhängig. Deutsche Ortsnamen kommen aus den lokalen
gaming.tools-Kartendaten und behalten dieselben IDs und Charakterprofile.

1. Das passende Charakterprofil und dieselbe Karte wie im Spiel wählen. Weltkarte und gewünschte
   Liste im Spiel öffnen, dann **Scan starten**.
2. **100 % bei Dungeons oder Garnisonen:** Kartenname, Kategorieüberschrift und deren Prozentanzeige
   müssen in zwei aufeinanderfolgenden ruhigen Aufnahmen zusammenpassen. Dann werden sämtliche
   Katalogorte dieser Kategorie auf dieser Karte für den beim Start gewählten Charakter als fertig
   gespeichert. Einzelne Namen und Haken müssen dafür nicht gelesen werden; Durchscrollen entfällt.
   Ein einzelner Kategoriescan endet. Im gemeinsamen Scan die nächste offene Liste öffnen: die fertige
   Kategorie wartet nur auf einen Bildwechsel und wird dabei nicht erneut per OCR gelesen.
3. **Unter 100 %:** Langsam scrollen und jede Seite kurz stehen lassen. Nur ein eindeutig zugeordneter
   Name mit dem goldenen Kreis-und-Haken-Symbol derselben Zeile darf nach zwei ruhigen Aufnahmen
   einen Ort abschließen. Ein goldener Kreis allein, ein Stern oder ein Haken der Nachbarzeile zählt
   nicht. Fehlende oder unlesbare Haken nehmen niemals vorhandenen Fortschritt zurück.
4. **Kibelisks:** „Bindung abgeschlossen“ / „Bind Complete“ hakt ab; „Bindung nicht abgeschlossen“ /
   „Binding Incomplete“ nimmt den Haken zurück. Der vollständige Statustext muss passen. Die
   bestehende Bestätigung über zwei ruhige Aufnahmen bleibt erhalten; hier gibt es keinen
   100-%-Kurzweg.
5. Das durchklickbare Overlay zeigt geprüfte Zeilen, Zähler und Scrollhinweise. Grüne Zeilen haben
   einen bestätigten Abschluss. Sind alle sichtbaren Namen geprüft, darf weitergescrollt werden;
   Zeilen ohne belegten Haken bleiben dabei offen. **Unentdeckt / Undiscovered** beendet den
   entdeckten Abschnitt nach zwei Aufnahmen, Namen darunter werden ignoriert. Der gemeinsame Scan
   endet, sobald alle drei Listen abgeschlossen sind; **Scan stoppen** bleibt jederzeit möglich.
6. Nicht eindeutige Namen der aktuell geöffneten Liste stehen unter der Vorschau. Texte außerhalb
   einer erkannten Weltkarte und Kategorie gelangen nicht in diese Liste. Die Einträge darunter
   erlauben nach dem Scan manuelle Korrekturen. Ein Charakterwechsel beendet den laufenden Scan.

## Aufnahme und Abgleich

- Die feste Listenmaske bleibt links bei 22 % der Fensterbreite und vertikal bei 12–98 % der Höhe
  (563 px breit bei 2559 px). Zusätzlich enthält dieselbe WGC-Aufnahme den Kartenkopf darüber;
  der Kartenname aus dem Katalog muss mit dem gewählten Kartenprofil übereinstimmen. Überschriften
  validieren den Inhalt, verschieben oder verkleinern die feste Maske aber nicht.
- Desktop-/GDI-Ersatzbilder werden abgewiesen. Nach jedem OCR-Lauf wird das aktuelle Bild erneut
  auf Bewegung geprüft; Scrollen oder ein Fensterwechsel verwirft die Bestätigung. Zwischen den
  Läufen gilt weiterhin das gemeinsame Capture-Budget. Keine Spieleingaben.
- Das Abschluss-Symbol stammt aus dem Nutzerbeleg vom 8.10.2026. Farbmaske und Formvergleich
  berücksichtigen verschiedene Fenstergrößen und UI-Skalierungen; gesucht wird nur in der rechten
  Symbolspalte der Liste. Ein Symbol darf genau einer nahegelegenen Namenszeile zugeordnet werden.
- Exakte normalisierte EN/DE-Namen zuerst; sonst mindestens 90 % Ähnlichkeit und 8 Prozentpunkte
  Abstand zum zweiten Kandidaten. Entrance und die deutschen Eingang-Zusätze einschließlich
  **Eingang zu** werden entfernt. Sichtbar mit Punkten oder … gekürzte Namen ab 12 normalisierten
  Zeichen werden nur bei einem eindeutigen Präfix übernommen; höchstens ein verlorenes, zusätzliches
  oder falsches Zeichen ist erlaubt. Mehrdeutige Präfixe bleiben offen.
- Die von Windows OCR beobachtete Schreibweise `IOO %` wird ausschließlich im räumlich zugeordneten
  Prozentfeld als `100 %` gelesen (`I`/`l` für die Eins, `O`/`o` für die beiden Nullen). Kategorie,
  Kartenkopf und Bestätigung über zwei Aufnahmen bleiben Pflicht. Andere Texte werden nicht so korrigiert.
- Bestehende Altgard-Aliase: `Lost Ruins` → `Lost Ruin Entrance`, `Fafnite Storage Room` →
  `Fafnite Storage Entrance`. Die Namensdiagnostik allein speichert keinen Abschluss.
- `exploration.json` speichert die fertigen Orts-IDs atomar je Charakter. IDs bestehen aus Karte,
  Kategorie und englischem Quellnamen. Serverweiter Pet-Fortschritt bleibt getrennt.

## Nachtest und Grenzen

Der deutsche Live-Nachtest vom 8.10.2026 verwendete nach einem zunächst veralteten Tester die
Version `fc681d8`: 120 Zuordnungen, Kibelisks 54/61 gebunden, Dungeons 51/61 und Garnisonen 8/15,
obwohl beide letzten Kategorien im Spiel 100 % zeigten. Damit waren fehlende Namenszuordnungen
kein verlässliches Maß für den Abschluss. Der Nutzer bestätigte anschließend das Abschlusssymbol
und den Kurzweg über 100 %.

Die neue Auswertung liest in den beiden ursprünglichen englischen Dungeon-Ausschnitten 14/14 und
9/9 Namen **mit zugeordnetem Abschluss-Symbol**; der zweite Ausschnitt enthält das Listenende.
Alle 17 im deutschen Nachtest protokollierten gekürzten Namen bzw. Eingang-Formen lassen sich
gegen den Katalog eindeutig auflösen. Tests prüfen 100 % mit Karten-/Kategoriebindung,
Charaktertrennung, zwei Aufnahmen, Kategorie- und Prozentwechsel, fehlende Haken, falsche Symbole,
Auflösungen und Fremdtexte. Deutsche/englische 100-%-Überschriften wurden zusätzlich mit synthetischer
Schrift und echtem Windows OCR geprüft. Diese synthetischen Bilder ersetzen keine Spielaufnahmen.

Die Änderungen müssen erneut im laufenden deutschen Client geprüft werden, insbesondere die
100-%-Abschlüsse und eine Liste unter 100 %. Bestehende Nutzerdaten wurden nicht nachträglich auf
Grundlage des Beobachtungsberichts geändert. Original-Fixtures liegen in
`tests/fixtures/exploration-list`; sie enthalten ausschließlich Kartenkopf und linken Listenbereich.

```powershell
$env:SOULCREST_REQUIRE_WINDOWS_OCR = '1'
dotnet test Soulcrest.slnx -c Release
node --test tests/exploration-map.test.cjs tests/map-filter.test.cjs
```

## Automatischer Abschluss bei Ankunft

Auf der Karte im Exploration-Progressionsbereich **Abschlussradius (Kartenpixel)** einstellen (Standard 40; 0 deaktiviert, maximal 500). Der angezeigte Kreis skaliert mit der Karte. Bei laufendem Spieler-Tracking wird das aktuelle Ziel innerhalb des Radius im aktiven Charakterprofil als fertig gespeichert und das nächste offene Ziel gewählt. Manuelle Korrekturen bleiben im jeweiligen Scan-Tab möglich. Der Abschluss bedeutet hier Ankunft am Ort; zusätzliche Dungeon-Abschlussbedingungen werden nicht geprüft.

## Freischaltung von Sealed Dungeons (Nutzerangabe 2026-10-07, nach aion2maps)

- Drei Sealed Dungeons je Karte öffnen nacheinander: Verteron (Elyos) Distorted Cave → Fissure Cave →
  Rift Cave, Altgard (Asmodier) Lost Ruin → Twisted Pit → Rift Fissure.
- „Ruins of the Ancient City of Ru“ (Verteron und Altgard) öffnet erst, wenn alle anderen Sealed Dungeons
  derselben Karte erledigt sind.
- Der Progressionsmodus überspringt einen gesperrten Dungeon; das Popup der Karte nennt, was fehlt
  (`ExplorationService.Prerequisites`/`MissingFor`).
