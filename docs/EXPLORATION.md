# Charakterbezogene Exploration

## Scannen auf Deutsch und Englisch (2026-10-08)

Im Reiter **Erkundungsfortschritt scannen** liest ein gemeinsamer Scan Kibelisks, versiegelte
Dungeons und Garnisonen. Unter **Sprache im Spiel (Scan)** stehen **Automatisch (Deutsch/Englisch)**,
**Deutsch** und **English** bereit. Die gespeicherte Auswahl gilt ab dem nächsten Scan, auch für
Pets; die Oberfläche bleibt unabhängig. Deutsche Ortsnamen kommen aus den lokalen
gaming.tools-Kartendaten und behalten dieselben IDs und Charakterprofile.

1. Das passende Charakterprofil wählen. Weltkarte und gewünschte Liste im Spiel öffnen, dann
   **Scan starten**. Karte und geöffnete Kategorie werden automatisch erkannt; bei Kartenwechsel
   muss der Scan nicht gestoppt oder die Karte in Soulcrest von Hand gewählt werden.
2. **100 % bei Dungeons oder Garnisonen:** Kartenname, Kategorieüberschrift und deren Prozentanzeige
   müssen in zwei aufeinanderfolgenden ruhigen Aufnahmen zusammenpassen. Dann werden sämtliche
   Katalogorte dieser Kategorie auf dieser Karte für den beim Start gewählten Charakter als fertig
   gespeichert. Einzelne Namen und Haken müssen dafür nicht gelesen werden; Durchscrollen entfällt.
   Im gemeinsamen Scan die nächste offene Liste öffnen: die fertige
   Kategorie wartet nur auf einen Bildwechsel und wird dabei nicht erneut per OCR gelesen.
3. **Unter 100 %:** Langsam scrollen und jede Seite kurz stehen lassen. Nur ein eindeutig zugeordneter
   Name mit dem goldenen Kreis-und-Haken-Symbol derselben Zeile darf nach zwei ruhigen Aufnahmen
   einen Ort abschließen. Ein goldener Kreis allein, ein Stern oder ein Haken der Nachbarzeile zählt
   nicht. Fehlende oder unlesbare Haken nehmen niemals vorhandenen Fortschritt zurück.
4. **Kibelisks:** „Bindung abgeschlossen“ / „Bind Complete“ hakt ab; „Bindung nicht abgeschlossen“ /
   „Binding Incomplete“ nimmt den Haken zurück. Der vollständige Statustext muss passen. Die
   bestehende Bestätigung über zwei ruhige Aufnahmen bleibt erhalten; hier gibt es keinen
   100-%-Kurzweg. Unbekannte oder mehrdeutige Namen verhindern, dass fehlende Orte am Listenende
   als ungebunden übernommen werden oder die Liste vorzeitig als fertig gilt. Ein bereits gelesenes
   Bindungsergebnis kann nach zwei neuen ruhigen Aufnahmen aktualisiert werden.
5. Das durchklickbare Overlay zeigt geprüfte Zeilen, Zähler und Scrollhinweise. Grüne Zeilen haben
   einen zweimal bestätigten Namen. Das ist unabhängig vom Abschluss-Haken. Sind alle sichtbaren Namen geprüft, darf weitergescrollt werden;
   Zeilen ohne belegten Haken bleiben dabei offen. **Unentdeckt / Undiscovered** beendet den
   entdeckten Abschnitt nach zwei Aufnahmen, sobald alle sichtbaren Namen eindeutig und wiederholt
   gelesen sind; unklare Namen halten den Scan offen. Namen darunter werden ignoriert. Der gemeinsame Scan
   endet, sobald alle drei Listen abgeschlossen sind; **Scan stoppen** bleibt jederzeit möglich.
6. Nicht eindeutige Namen der aktuell geöffneten Liste stehen unter der Vorschau. Texte außerhalb
   einer erkannten Weltkarte und Kategorie gelangen nicht in diese Liste. Die Einträge darunter
   erlauben nach dem Scan manuelle Korrekturen. Ein Charakterwechsel beendet den laufenden Scan.

## Aufnahme und Abgleich

- Die feste Listenmaske folgt links 22 % der auf 16:9 begrenzten Fensterbreite und vertikal
  12–98 % der Höhe (563 px bei 2559×1439; 423 px bei 1920×1080 und 2560×1080). Auf Ultrawide
  wächst die Karte, nicht die Namens-/Hakenspalte. Zusätzlich enthält dieselbe WGC-Aufnahme den Kartenkopf darüber;
  der Kartenname muss eindeutig zu einer bekannten Karte mit Erkundungsorten gehören. Zwei
  ruhige Beobachtungen bestätigen die Karte. Widersprüchliche oder mehrdeutige Titel setzen diese
  Bestätigung zurück. Überschriften
  validieren den Inhalt, verschieben oder verkleinern die feste Maske aber nicht.
- Eine zweite, gleich große Graustufenansicht mit lokal begrenzter Kontrastverstärkung (CLAHE)
  und zweifacher OCR-Vergrößerung liest ausgegraute Namen. Original und Kontrastansicht werden
  getrennt nach Karte/Kategorie validiert, dann nur eindeutige physische Zeilen zusammengeführt.
  Unterschiedliche gültige Namen für dieselbe Zeile bleiben unklar. Auch eine mehrdeutige
  Kandidatenmenge darf nicht durch einen fremden Namen des anderen OCR-Passes ersetzt werden.
  Ein zweiter Pass kann nur einen darin enthaltenen Kandidaten bestätigen. Abschluss-Haken und
  Bewegung werden immer am Original geprüft;
  **100 % ausschließlich in der Kontrastansicht schließen keine Kategorie ab**.
- Nutzerbeleg 2026-10-11: Die mit Punkten oder … gekürzte Überschrift **Versiegelter Dunge...**
  wird als Dungeon-Kategorie erkannt. Karte, räumlicher Überschriftenbereich und zwei ruhige
  Lesungen bleiben Pflicht. Die bekannte Präfixform muss vollständig vorliegen und ausdrücklich
  gekürzt sein; kurze oder nur ähnlich klingende Texte werden nicht als Kategorie geraten.
  Synthetische Windows-OCR mit DE/Auto belegt auch die Fehlablesungen „Versiegelter Dunge.«“
  und „Versiegelter Dunge.„“; nur diese beiden zusätzlichen Endungen werden repariert.
  Die gleichen belegten DE/EN-Kategorieüberschriften helfen der automatischen OCR-Sprachauswahl;
  widersprüchliche Sprachhinweise bleiben unentschieden.
- Desktop-/GDI-Ersatzbilder werden abgewiesen. Nach jedem OCR-Lauf wird das aktuelle Bild erneut
  auf Bewegung geprüft; Scrollen oder ein Fensterwechsel verwirft die Bestätigung. Zwischen den
  Läufen gilt weiterhin das gemeinsame Capture-Budget. Keine Spieleingaben.
- Das Abschluss-Symbol stammt aus dem Nutzerbeleg vom 8.10.2026. Farbmaske und Formvergleich
  berücksichtigen verschiedene Fenstergrößen und UI-Skalierungen; gesucht wird nur in der rechten
  Symbolspalte der Liste (60–95 % ihrer Breite). Die Nutzeraufnahmen vom 11.10. belegen den
  rechten Hakenrand außerhalb der früheren 85-%-Grenze bei **Sehr groß**. **Sehr klein** und
  **Sehr groß** werden mit unveränderten Farb-/Formschwellen geprüft. Ein Symbol darf genau einer
  nahegelegenen Namenszeile zugeordnet werden.
- `ExplorationNameIndex` baut Referenzen aus sämtlichen vollständigen deutschen und englischen
  Katalognamen auf (aktuelles Datenpaket: 304 Ortsidentitäten, 608 Quellnamen). Im Scan wird zuerst
  nach erkannter Karte und Kategorie gefiltert. DE/EN-Varianten desselben Orts zählen als eine ID;
  echte gleichnamige Kibelisken bleiben getrennte Kandidaten. Es gibt keine Liste einzelner
  gekürzter Ortsnamenszustände und keine gerenderten Schriftbilder.
- Exakte normalisierte Vollnamen zuerst; sonst mindestens 90 % Ähnlichkeit und 8 Prozentpunkte
  Abstand zum zweiten Kandidaten. Entrance und die deutschen Eingang-Zusätze einschließlich
  **Eingang zu** werden entfernt. Der initiale Abschnitt vor der ersten ausdrücklichen Ellipse
  (`..` oder `…`) wird gegen alle vollständigen Referenzen verglichen, auch wenn OCR Kartenlabels
  oder Entfernung dahinter angehängt hat. Dungeon-/Garnisonspräfixe brauchen mindestens 12
  normalisierte Zeichen und höchstens einen Zeichenfehler; mehrere plausible IDs bleiben offen.
  Ohne Ellipse ist zusätzlich nur ein exaktes langes Präfix zulässig. Ein vollständiger anderer
  Katalogname hinter der Ellipse macht die Zeile widersprüchlich. Originaltext, Wörter und
  Hakenkoordinaten bleiben erhalten. Rechts beginnende Kartenlabels außerhalb der linken
  Namensstartspalte (2,5–30 % Listenbreite) sind keine zusätzlichen Listenzeilen.
- Kibelisk-Namen verwenden denselben Referenzindex. Ihre bestehenden exakten Präfixe ab acht
  Zeichen und der eindeutige Ein-Zeichen-Abgleich für Vollnamen bleiben erhalten. Echte
  Kandidatengruppen werden erst durch passende Zeilenzahl und einheitlichen Bindungsstatus
  aufgelöst; ein Anhang hinter einer Ellipse darf keine Gruppe erzwingen.
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

## Scanhilfe und Diagnose (2026-10-11)

Die Scan-Kachel führt durch Karte/Liste öffnen, Stillhalten und langsames Scrollen. Sie trennt
**geprüfte sichtbare Zeilen**, **übernommene Einträge** der aktuell erkannten Karte und **gespeicherte Abschlüsse**
des Charakters. Kibelisk-Einträge zählen beim Übernehmen auch mit bestätigtem ungebundenem Status.
Die Vorschau und unklare Namen lassen sich ausklappen; nach dem Stoppen bleibt die manuelle
Korrektur möglich. Das durchklickbare Scan-Overlay verwendet dieselbe Zählerbedeutung und Legende.

Diagnose-Pakete enthalten jetzt den letzten bereits aufgenommenen Listenbereich in voller
Auflösung (`bilder/erkundung-letzte-liste.png`), Original-/Kontrast-OCR mit Koordinaten
(`erkundung-ocr.txt`) sowie das begrenzte `exploration-scan.log`. Dafür wird keine neue Aufnahme
ausgelöst. Das vom Nutzer bereitgestellte Paket vom 11.10., 04:31 enthielt nur Karten- und
Pet-Bilder, keine Dungeon-Liste. Der konkrete graue Nutzerframe konnte daher nicht reproduziert
werden. Vorhandene Original-Dungeon-Fixtures sowie ausdrücklich synthetische Kontrast- und
Ultrawide-Varianten prüfen die Verbesserung. Die nachgereichten Diagnosepakete vom 11.10.,
19:59:56 (**Sehr groß**) und 20:02:47 (**Sehr klein**) enthalten Originalaufnahmen der Altgard-
Garnisonsliste. Ein isolierter Offline-Abgleich mit der Produktionslogik erkennt in beiden
Karte, Kategorie und zwei Namen; nur Grabräuberversteck besitzt den Abschluss-Haken, Provisorium
der Klingenlegion bleibt offen. Die Diagnosebilder werden nicht als Repository-Fixtures gespeichert.
Automatische Kartenwechsel und die Korrektur benötigen weiterhin einen Live-Nachtest nach Tester-Neustart.

Das Paket vom 11.10., 20:21:40, stammt vom aktuellen Tester und belegt einen zusätzlichen
Namensfehler: `Verlassenes Gefängnis der...Wlu s Brücke` bzw. `...Mu@s Brücke` enthält hinter
der Ellipse einen Kartenlabel-Anhang. Die vollständige Katalogreferenz lautet **Eingang zum
Verlassenen Gefängnis der Archonen** / **Abandoned Archon Jail Entrance**. Allgemeine
Ellipsentrennung und Referenzvergleich lösen den Namen mit der vorhandenen Ein-Zeichen-Toleranz
auf; separate rechts beginnende Kartenlabels werden nicht als Listennamen gewertet. Ein
isolierter Offline-Abgleich der gespeicherten Garnisonsseite erkennt den einen offenen Ort.
Die beiden betroffenen Dungeon-OCR-Frames erkennen 14/14 bzw. 10/10 Namen ohne Unklarheiten;
dafür werden leere synthetische Bilder verwendet, da die passenden alten Dungeonpixel fehlen.
Auch die Kategorie wird dabei aus dem jeweiligen Scanlog vorgegeben: Der Textdump enthält
keine Wortrechtecke für die Reparatur der verbundenen Symbol-/Überschriftenzeile.
Das beweist Namens-/Zeilenzuordnung, keine historische Kategorieerkennung oder Dungeon-Abschlüsse. Synthetische
Regressionen prüfen die Original-Hakenbindung separat. Live-Nachtest nach erneutem Testerstart nötig.

## Automatischer Abschluss bei Ankunft

Auf der Karte im Exploration-Progressionsbereich **Abschlussradius (Kartenpixel)** einstellen (Standard 40; 0 deaktiviert, maximal 500). Der angezeigte Kreis skaliert mit der Karte. Bei laufendem Spieler-Tracking wird das aktuelle Ziel innerhalb des Radius im aktiven Charakterprofil als fertig gespeichert und das nächste offene Ziel gewählt. Manuelle Korrekturen bleiben im jeweiligen Scan-Tab möglich. Strongholds gelten bei Ankunft am Ort als abgeschlossen. **Sealed Dungeons** (Nutzerentscheidung 2026-10-11) werden beim Betreten abgehakt, auch ohne Progressionsmodus: Die letzte Spielerposition liegt im Abschlussradius eines Sealed Dungeons und das Tracking erkennt höchstens zwei Minuten danach eine Instanz (Minimap nach dem Ladebildschirm verschwunden). Bei mehreren Dungeons im Radius zählt der nächstgelegene. Vorbeigehen allein hakt nichts ab; zusätzliche Dungeon-Abschlussbedingungen (Boss besiegt) werden nicht geprüft.

## Freischaltung von Sealed Dungeons (Nutzerangabe 2026-10-07, nach aion2maps)

- Drei Sealed Dungeons je Karte öffnen nacheinander: Verteron (Elyos) Distorted Cave → Fissure Cave →
  Rift Cave, Altgard (Asmodier) Lost Ruin → Twisted Pit → Rift Fissure.
- „Ruins of the Ancient City of Ru“ (Verteron und Altgard) öffnet erst, wenn alle anderen Sealed Dungeons
  derselben Karte erledigt sind.
- Der Progressionsmodus überspringt einen gesperrten Dungeon; das Popup der Karte nennt, was fehlt
  (`ExplorationService.Prerequisites`/`MissingFor`).

## Übersicht und automatische Kartenwahl (2026-10-11)

Nutzerentscheidung aus dem Interview: Gesamtübersicht, Filter und Checkliste stehen im Vordergrund.
Nutzerkorrektur 2026-10-11: Der Scan-Einstieg steht dauerhaft sichtbar direkt unter dem Seitentitel,
vor Gesamtübersicht und Checkliste. Charakter-/Kartenwahl und Start/Stop bleiben zusammen;
nur die Hilfen und Vorschau innerhalb der Scan-Kachel sind einklappbar. Gesamt- und
Kategorie-Zähler gelten immer für die gesamte gewählte Karte und den aktuellen Charakter; Suche
oder Statusfilter verändern sie nicht. Die Suche berücksichtigt belegte englische und deutsche
Ortsnamen. Gesperrte Dungeons nennen die noch offenen Voraussetzungen. Einzelne Haken bleiben
manuell korrigierbar; während eines Scans sind sie gesperrt.

Standardmäßig folgt die Übersicht der im Spiel erkannten Karte. Eine manuelle Auswahl oder
„Karte fixieren“ speichert `ExplorationPinnedMap`; „Spielkarte folgen“ löscht diese Fixierung.
`LastMap` bleibt die Auswahl des Karten-Tabs und dient hier nur als anfänglicher Ersatz. Bei
fehlender aktueller Erkennung bleibt die bisherige Auswahl sichtbar und wird entsprechend benannt.
Während eines Scans hat dessen automatisch bestätigte `ScanMap` Vorrang. Die gespeicherte
Fixierung und Ansichtsfilter beschränken die Erkennung nicht und bleiben nach dem Scan erhalten.
Bei bestätigtem Kartenwechsel werden Ortskandidaten, Kibelisk-Zeilen, Zwei-Frame-Bestätigungen,
Listenende, 100-%-Kurzwege und sichtbare Zähler neu aufgebaut; gespeicherte Abschlüsse bleiben
unangetastet. Keine alte Lesung darf die neue Karte abschließen. Der Scan bleibt an den beim
Start gewählten Charakter gebunden.

`MapTrackingService.DetectedMap` enthält Karten-ID, Minimap/Weltkarte und Zeit des bestätigten
SIFT-Treffers. Bildfluss erneuert diesen Zeitstempel nicht. Für die Anzeige gilt ein Fenster von
fünf Sekunden; Stop, Ladebildschirm, Menü und verlorene Erkennung entwerten den Kontext. Die
geöffnete Weltkarte kann die angezeigte Übersicht wechseln, liefert aber keine Spielerposition.
Der Tab verwendet vorhandene Erkennung und startet keine zusätzliche Aufnahme.

„Nächster offener Ort“ berücksichtigt die gewählte Kategorie, offene Voraussetzungen und eine
aktuelle Spielerposition derselben Karte. Suche und Statusfilter verändern die Empfehlung nicht.
Ohne frische Position wird keine Entfernung geschätzt. Entfernungen werden mit dem belegten
Kartenmaßstab in Meter umgerechnet, sonst als Kartenpixel angezeigt. „Auf Karte zeigen“ fokussiert
den Ort in Soulcrest; es setzt keinen Abschluss und ändert keine Progressions- oder Boss-Modi.
Die bestehenden Ankunftsregeln bleiben beim `ExplorationArrivalService`.

## Progression folgt Kartenwechseln (2026-10-11)

Nutzerkorrektur: Der aktivierte Progression Mode folgt einem bestätigten Wechsel der Spielerkarte,
auch wenn „Spieler verfolgen“ ausgeschaltet ist. Beispiel: Altgard → Abyss darf kein Altgard-Ziel
behalten. Vor dem Kartenwechsel wird das bisherige Progressionsziel verworfen, anschließend auf
der neuen Karte neu berechnet. Späte Zielmeldungen der alten Karte werden ignoriert. Das bloße
Öffnen einer anderen Weltkarte ist kein Spielerwechsel. Die Exploration-Fixierung betrifft nur
die Erkundungsübersicht und verhindert diesen Progressionswechsel nicht.
