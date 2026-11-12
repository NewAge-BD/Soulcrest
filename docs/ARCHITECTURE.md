# Architektur

## Crafting Calculator

Der Haupttab `crafting` verwendet einen gebündelten Rezept- und Itemkatalog mit
Originalicons, Unterrezepten, Materialalternativen und belegten NPC-Angeboten.
Für normales Crafting simuliert `CraftingCalculator` eine feste Zahl von
Startrezeptversuchen (`TargetAttempts`): Jeder Versuch zählt einschließlich
Fehlschlag; automatische COMBO-Rollen und die Beschaffung von Vorprodukten erhöhen
nicht diese Zielversuchszahl. Der gesamte mögliche Endausgabekorb aus Basis- und
COMBO-Ergebnissen trägt zu erwartetem Nettoerlös und Profit bei. Hergestellte
Vorproduktreste bleiben separat; sie werden nicht nochmals dem Enderlös zugerechnet.
Fehlt für ein mögliches Endergebnis ein qualifizierter Verkaufspreis, bleibt die
Bewertung unvollständig, statt nur den bekannten Teil als Gesamterlös auszugeben.

Davon getrennt simuliert `CraftingOutputTargetCalculator` bis zur exakten neu
erzeugten Menge einer konkreten COMBO-Ausgabe-ID. Transfercrafting behält seine
reine Kostenrechnung. Diese drei Rechenverträge dürfen nicht durch dieselbe
UI-Zielmenge oder einen Fortschritts-Pin miteinander gleichgesetzt werden.

Standardmäßig 1.000 deterministische Gesamtdurchläufe (einstellbar von 1 bis
100.000) teilen Zwischenbestände und liefern erwartete Kosten. Intern berechnete
Kostenquantile, etwa `P90Cost`, bleiben Rechenergebnisse und Testverträge. Sie sind
keine sichtbare Budgetkennzahl: Die Anzeige „Estimated 90% budget“ ist seit der
Nutzerentscheidung vom 11.11.2026 aus normalem Crafting und Transfercrafting entfernt.
Unvollständige Regeln, Preise, Fraktionsbindungen und abgebrochene Simulationen
liefern keinen behaupteten Profit.

`CraftingCalculatorService` hält das aktive Rechenszenario im RAM.
`CraftingScenarioStore` speichert ausschließlich die manuelle Auswahl pro lokaler
Profil-GUID atomar in `crafting-scenarios.json`: Bereich, Unterkategorie, Suche,
Rezept/Variante, Zielversuchszahl, Simulationszahl, Bezugswege, Alternativen,
manuelle Bestandsangaben und manuelle Rechenlevel. Die Zielstückzahl eines
angehefteten Fortschrittsziels gehört zu dessen getrenntem Vertrag.
500-ms-Debounce und serieller Writer fassen Eingaben zusammen; letzte Lease und
Dispose sichern verbleibende Änderungen. Profilwechsel restaurieren getrennte
Szenarien, gelöschte Profile können durch verspätete Schreibvorgänge nicht
wiederkehren. Defekte oder unbekannte Dateischemata bleiben schreibgeschützt;
Speicherfehler werden separat gemeldet und sperren die Berechnung nicht.
Beobachtete Level, abgeleitete Referenzlevel, Preise und Ergebnisse werden nie als
Szenario gespeichert. Nur eine erworbene
Ansichtslease aktiviert Preise und Berechnung; Änderungen widerrufen ältere
Ergebnisse. Der Dienst teilt Exchange-Client, Cache, Serverzuordnung und Gebühren
mit den bestehenden Preisfunktionen. Beutefakten werden dabei nicht verändert.
Vor jeder Marktanfrage werden benötigte Item-IDs auf bestätigte Handelbarkeit
gefiltert. Gebundene, nicht handelbare und unbekannt klassifizierte Items bleiben
im Rezeptschema und in der NPC-Preisprojektion, benötigen aber weder aktuelle
Angebote noch 24h-Ersatzwerte. Ohne marktfähige Items wird auch bei explizitem
Preis-Refresh kein Serverkatalog geladen. Die passive Diagnose zählt getrennt
marktfähige, nicht handelbare und ungeklärt klassifizierte benötigte Items.
`CraftingProficiencyStateService` liest ausschließlich die nach vollständiger
Own-Prüfung veröffentlichte Fertigkeitsdomain. Eine separate atomare Datei
`crafting-proficiency.json` hält die Historie pro lokaler Profil-GUID; gespeicherte
Werte und manuelle Rechenlevel bleiben von Livebelegen unterscheidbar.

Die auswählbare Diagnose `crafting-calculator` liest nur bestehende Zustände.
Die zusätzliche Debugprojektion enthält ausschließlich Kategorien, Level,
Quellenstatus und Faktzeiten und wird nur bei gültig konfiguriertem Debugport
ausgegeben. Eine Änderungsmeldung für Proficiency ist noch nicht qualifiziert;
der erkannte Stand stammt ausdrücklich vom letzten bestätigten Welteintritt.

### Abhängigkeitskarte

Die normale Rezeptansicht verwendet `CraftingDependencyMapProjection` als reine
Projektion des vorhandenen Szenarios. Exakte Item-IDs, gewählte Unterrezepte und
Materialalternativen erzeugen einen begrenzten azyklischen Graphen. Gemeinsame
Zutaten erscheinen einmal; ihr erwarteter Gesamtbedarf stammt aus den tatsächlichen
Simulationsschritten. Der bereits bestandsbereinigte Einkaufsrest wird nicht
erneut um denselben Bestand vermindert. Mehrdeutige Herstellungswege erfordern
eine explizite Auswahl. Sammeländerungen werden als eine Szenarioänderung gebucht.

Die Mengenbalken projizieren den wirksamen Planbestand. Der getrennte private
Bestandsdienst ergänzt beobachtete eigene Teilmengen, deren Historie und geschützte
manuelle Korrekturen; unbekannte Inventar-/Lagerbereiche gelten nicht als leer.
Gold mit dem bestehenden Unique-Glanz bedeutet gedeckten Durchschnittsbedarf ohne
offenen Einkaufs- oder Produktionsanteil. Diese Planabdeckung ist kein beobachteter
Herstellungserfolg. Ein getrennt angeheftetes Ziel darf nur neue bestätigte und
bereits journalisierte Zugänge zählen; die Zielstückzahl bleibt unabhängig von
normalen Craftversuchen. Bestätigte Craft-Ereignisse, tatsächliche Ausgaben und
Fehlcraft-Verluste sind noch nicht qualifiziert. Die eigene auswählbare Diagnose
`crafting-dependency-map` weist diese Quellenlücke aus und aktiviert keine Quelle.
Transfercrafting behält seine getrennte Kostenansicht und Beschaffungsregeln.

### Referenzlevel und konkrete COMBO-Ziele

`CraftingReferenceProjection` traversiert nur die gewählten Herstellungswege und
erreichbaren COMBO-Stufen. Pro Beruf wird das Maximum der benötigten Mindestlevel
abgeleitet. Ein bekanntes zu niedriges Rechenlevel wird ausschließlich für diese
Rechnung angehoben; Charakterstand und manuelle Eingaben bleiben unverändert.
Die UI zeigt Charakterwarnung, Referenzsimulation und verwendete Mindestlevel.
Unbekannte Level bleiben unbekannt; fehlende Kurven und andere Fachlücken werden
dadurch nicht ersetzt. Schon vorhandene garantierte einzelne Transferausrüstung
kann ihre Vorstufenkette einsparen, ein stochastisches COMBO-Ziel dagegen nicht.

COMBO-Ausrüstung erscheint mit eigener Ausgabe-ID als separates auswählbares
Produkt. `CraftingOutputTargetCalculator` simuliert bis zur exakten neu erzeugten
Zielmenge, mit unabhängigen Basiserfolgen, Aktivierungen und COMBO-Erfolgen.
Vorhandene Zielkopien und normale Ausgaben erfüllen dieses Ziel nicht. Exakte
COMBO-Vorstufen werden nicht gegen ein ähnlich benanntes Basisitem ersetzt.
Tatsächliche Ausgabekörbe, gemeinsame Zwischenbestände und Fehlschlagsverbrauch
bestimmen Kosten und Gesamtquantile. Nur der Zielverkauf fließt in den Profit ein;
zusätzliche mögliche Nebenproduktverkäufe bleiben separat. Fehlt ausschließlich
der Zielverkauf, bleiben nach abgeschlossener Simulation Kosten und Bedarf
verfügbar; Erlös und Profit bleiben unbekannt. Fehlende Einkaufspreise und
unvollständige Simulationen liefern keine Kostenkennzahlen. Transfer-COMBO-Ziele
verwenden ebenfalls die konkrete ID und behalten reine Kostenkennzahlen.

### Transferkosten und vorhandene Materialien

Der zusätzliche Browserbereich `Transfercrafting` hat 19 direkte Ausrüstungsarten,
erzeugt aber keinen sechsten Proficiency-Wert. Native Item-/Rezeptklassifikationen
trennen Ausrüstung und Transferrezepte; fehlende Klassifikationen bleiben nullable.
Die fünf normalen Berufsbereiche behalten ihre bestehende Unterteilung.
Transferfilter verwenden die Ausrüstungsart des Rezepts. Eine direkte
Rezeptauswahl einschließlich globaler Suchtreffer synchronisiert den
Browserbereich und die Ausrüstungsart mit der gewählten Rezeptdefinition.
Zusätzlich begrenzt die konfigurierte Marktfraktion die Rezeptliste, globale
Suchtreffer und auswählbare Unterrezepte. Neutrale Rezepte bleiben sichtbar;
eine noch unbekannte Fraktion schränkt den Browser nicht ein. Der Dienst weist
inkompatible direkte Rezept-/Quellenwahlen ab. Nach einem Fraktionswechsel
ersetzt er ein unpassendes Ziel bevorzugt durch dasselbe Ausgabeitem im aktuellen
Bereich/Slot, andernfalls durch ein passendes Basisrezept oder eine leere Auswahl.
Ungültige Herstellerwahlen werden entfernt; manuelle Bestände desselben Profils
bleiben erhalten. Die passive Diagnose nennt Einstellungen- und Rezeptfraktion
sowie die Zahl kompatibler Basisrezepte.
`TransferCraftingCalculator` berechnet ausschließlich Bruttoherstellungskosten
und Einkaufsmengen. Intern berechnete Gesamtquantile werden nicht als
90-%-Budgetkachel angezeigt. Es gibt keine Verkaufsbewertung von
Transferausgaben oder Marktpreisabfragen für deren Ausrüstungsvorstufen.

Eine exakt ausgewählte Transferausrüstung kann als vorhandene einzelne Kopie
eingeplant werden. Fehlende Kopien und gewöhnliche Ausrüstungsmaterialien werden
über exakte Herstellerrezepte beschafft; mehrere Wege benötigen eine Auswahl,
fehlende Wege bleiben unbekannt. Die bestätigte 100-%-Transferregel ist ein eigener
Nutzerbeleg und ersetzt keine fehlenden nativen Wahrscheinlichkeitskurven.
Normale Vorrezepte behalten ihre Proficiency-, Erfolgs- und COMBO-Regeln.

Alle Materialbestände gelten in einem gemeinsamen endlichen Planinventar pro
Durchlauf. Nur nachzukaufende Fehlmengen erzeugen Kosten; Überbestände erhalten
keine Gutschrift. Geschützte Transferausrüstung wird vor sonstiger Ausrüstung und
Rohstoffen beschafft. Bereits neu erzeugte zulässige COMBO-Alternativen oder höhere
Zielausgaben können weitere Transfers und Materialkäufe ersparen. Ungewählte
vorhandene Ausrüstung wird dabei nicht als Alternative verbraucht.

Besitzangaben gehören zum gespeicherten Szenario und werden beim Profilwechsel
durch die Angaben des gewählten Profils ersetzt. Normales
Crafting verwendet denselben endlichen Materialbestand. Die UI ersetzt den
Kauf-Dropdown durch Mengenfelder; Unterrezepte bleiben ausdrücklich auswählbar.
Hintergrundaktualisierungen erhalten das bisherige Ergebnis, während fachliche
Eingabe-/Quellenwechsel es verwerfen. Die passive Diagnose unterscheidet Kosten-
und Profitmodus, Garantiertheitsquelle und vorhandene Material-/Geararten.

Der Calculator verwendet eine Arbeitsansicht mit unabhängig scrollender
Rezeptauswahl links und Herstellungsplan rechts. Globale Bereichs-/Kategoriewahl
und die kompakte Proficiency-Zeile bleiben sichtbar; Kennzahlen haften im Plan
oben. Schema und Einkaufsliste teilen einen Scrollbereich. Unter 1100 Pixeln
steht der Einkauf unter dem Schema, unter 900 Pixeln auch der Plan unter der
Rezeptauswahl. UI-Struktur und CSS enthalten keine fachliche Berechnung.

## Lösung

- `Soulcrest.Core` (`net10.0`): Pet-Daten, Fortschrittsmodelle, begrenzte Netzwerkparser,
  Karten-/Textmodelle, Boss-/Alertregeln; unabhängig von Windows und UI.
- `Soulcrest.Ocr` (`net10.0-windows`): bestehender Assemblyname für OpenCV-Kartenabgleich,
  Bildkonvertierung und Pixeltracking. Seit 2026-10-12 keine Windows-OCR oder Pet-/Listen-Scans.
- `Soulcrest.App` (`net10.0-windows`): WinForms + Blazor Hybrid/WebView2, WGC, passive Npcap-
  Erfassung, native Overlays, lokale Persistenz und DE/EN-Lokalisierung.
- Entsprechende Core-/Ocr-/App-Testprojekte. `App → Ocr, Core`; `Ocr → Core`.

## Laufzeitfluss seit 2026-10-12

```text
Npcap (eigener Serverstrom)
  → TcpReassembler → GameMessageStream (Frames/LZ4)
  → begrenzte Core-Decoder → NetworkLootService (Identität/Generation/Sequenz)
  → ProgressService / ExplorationService / BossRushService
  → UI, Overlays, Farmplanung

WGC (Spielfenster) → OpenCV-Kartenabgleich / MapTrackingService
  → bestätigte Kartenposition → Navigation / Kartenoverlays
```

Pet-/Exploration-OCR, Scantimer, Scanfenster/-Hotkeys und Sprachpaketinstallation sind entfernt.
Der vorhandene passive Mitschnitt ist die einzige Fortschrittsquelle neben manuellen Korrekturen.
Nur ein durch eigenen Welteintritt bestätigter Strom darf Charakterfortschritt speichern.
Ein früher unbestätigter Login-Kandidat suspendiert die alte Zuordnung; streng geprüfte volle
Startlisten werden nur bis genau einer Prepare und der passenden Own-Identität vorgemerkt.
Charakter-ID und Entity-Key müssen übereinstimmen, Server/Name kommen ausschließlich aus Own.
Unverankerte Exploration-Prefixe und vor Own empfangene Erkundungsdeltas werden verworfen. TCP-Lücken,
Formatfehler, beschädigte Bundles und Verbindungswechsel entwerten den Kontext; Altstände bleiben.
Netzwerk-Speicherfehler werden geloggt und unterbrechen den restlichen Capture-Batch nicht.
`NavigationArrivalService` verarbeitet nur Wegpunkte und Leveling-Stationen. Annäherungsring,
Dungeon-/Stronghold-Clear-Aktion und frühere OCR-Erkundungshelfer sind entfernt. Der kompatibel
übernommene `TargetArrivalRadius` hat keine Verbindung zum Erkundungsfortschritt.

Services liefern Snapshots und Changed-Events. UI liest deren Zustände; WGC/OpenCV für Navigation
ist unabhängig von Netzwerkfortschritt. Leaflet und Kartenkacheln bleiben lokal.
Die Hauptnavigation bleibt einzeilig und hat bei Platzmangel eine sichtbare horizontale
Scrollleiste. Mausradbewegungen über den Tabs scrollen seitlich; horizontale Touchpad-
Gesten, Tastaturfokus und Browserzoom behalten ihre Standardfunktion.

## Fortschritt und Profile

Pet-Stufe und verfügbare Souls sind getrennt; ausreichende Balance behauptet keinen erworbenen
Levelaufstieg. Farm-Gesamtwerte bleiben für Planung erhalten. Nur echte Gewinne erzeugen
Ledger-/Sitzungsereignisse; Snapshots, Erwerb und Verbrauch zählen keine neuen Drops.
Nutzerentscheidung 2026-10-13: eine globale Pet-Sammlung ohne Server-/Charakterbindung.
Eigene Pet-Vollstände, Gewinne, Verbrauch und absolute Aufstiege werden direkt verarbeitet;
9009 benötigt wegen Broadcast weiterhin den eigenen Entity-Key. Pet-Generation/Sequenz sind
von Exploration getrennt. Reentrante Meldungen werden begrenzt in Empfangsreihenfolge verarbeitet.
Echte Lücken machen gespeicherte Werte unbestätigt; neue vollständige Meldungen zählen weiter.
Schema3 übernimmt die aktive bisherige Sammlung ohne Addition. Vollständige Altserverdaten und
unbekannte Felder bleiben als LegacyServerSnapshot und bytegenaues Migrationsbackup erhalten.

Exploration gilt pro Charakter. Bestehende GUIDs, Labels, Namen, Orts-IDs, Haken und Leveling-
Routen bleiben bestehen; stabile eigene Charakter-ID und Server-ID ergänzen die Zuordnung.
Die Oberfläche zeigt für Dungeons/Strongholds Abschluss-Haken (Nutzerpräzisierung 2026-10-13).
Die bisherige Dreistufen-Enum bleibt speicherkompatibel; fehlende Belege bleiben intern unbekannt.
Eigene direkte Erfolge sowie das belegte positive Muster einer neuen eigenen Questliste
(Status4, Stage1, [1,0,0,0,0,0]) bestätigen Abschlüsse. Positive eigene Kibelisk-Listen bestätigen
Bindungen. Absenz setzt keine Haken zurück; Beobachtungsaktualität ist ein separates Feld.
Radius/Instanzbetreten setzt keine Haken.
Manuelle Korrekturen bleiben geschützt. Details: [EXPLORATION.md](EXPLORATION.md).

## Lokale Speicherung

`%LOCALAPPDATA%\Soulcrest`: `settings.json`, `progress.json` (gemeinsame Pets), `exploration.json`
(Charakterprofile), `events.jsonl` (append-only), `targets.json`, `routes.soulroute`,
`leveling-progress.json`, `alerts.json`, `cache/`, `learned/`, `captures/`, `webview2/`.
Schreiben ist atomar. Vor erstem Schema-2-Schreiben entsteht eine unveränderte Altdatei-Sicherung.
Tests leiten Nutzerdaten in temporäre Ordner um und laufen wegen gemeinsamem Prozesszustand
(Sprache, Env, Datenpfad) seriell. Die echte Nutzer-App wird für Entwicklung nicht parallel gestartet.

## Alerts (2026-10-12)

`Core.Alerts.AlertSchedules` berechnet aus einer expliziten Zeitzone die nächste UTC-Instanz
stündlicher, täglicher, wöchentlicher, N-Kalendertage-, Intervall- und Einmalregeln. Der
`AlertsService` wird im Host unabhängig vom aktiven Tab gestartet, liefert Snapshots und
dedupliziert Auslösungen in einem atomaren Schema-1-Store `alerts.json`. Die Uhr ist injizierbar.
Boss-Hinweise konsumieren die vorhandenen `BossRushService.Alerts` einschließlich stabiler
Karte/Spawn-ID; die vorhandene BossAlertPolicy bleibt die einzige Spawn-Triggerquelle.

`AlertsView` verwaltet Zeitpläne; die gemeinsame `LiveBossTimers`-Liste integriert die individuellen
Boss-Erinnerungen und Anzeigedauern. Die separate Boss-Alerts-Kachel ist entfernt. `AlertsOverlayForm` zeichnet die sichtbaren Countdowns ohne Fokuswechsel und übernimmt
Schloss, Alt und Capture-Sichtbarkeit. Die gemeinsame `OverlayVisibilityPolicy` wertet für diese
Leiste (Panel `alerts`) die positiven AppSettings-Freigaben für Weltkarte, Menü und Instanzen aus den vorhandenen
Trackingzuständen aus. Ohne laufendes Tracking gibt es keine Kontextunterdrückung. Ton läuft im MainForm über
`NoticeRaised` unabhängig von Rendern und Sichtbarkeit. Das Boss-Rush-Overlay zeichnet im Host
nur noch seine Liste; es erzeugt keine zweite Alarmanzeige. Overlay-Einstellungen und Vorschau stehen im Overlay-Tab; Erinnerungsregeln bleiben unter Alerts.

`EventCatalog` hält sechs lokale Nichtboss-EU-Zeitpläne nach der gewählten Community-Referenz.
`Core.Alerts.EventWindows` trennt Kalenderstart, Eventlaufzeit und Portalphase und liefert begrenzte
Vorkommen für die Zeitachse. `AlertsService.EventTimers` ist unabhängig von aktivierten Erinnerungen.
Stabile `catalog:<id>`-Erinnerungen nutzen denselben Scheduler; optionale `EventPresentation`-Werte
in Schema 1 steuern die native Anzeige getrennt. Katalogpläne bleiben fest, eigene Alerts editierbar.
Der native Countdown kann mittels `CountdownLabel` auf Portal-/Eventende zeigen; bestätigte
Bossmeldungen behalten ihre bisherige Darstellung. Dashboard und erfasste Bosskarten sind getrennt. Im Workspace stehen die vier Events als
unabhängig aufklappbare Zeilen mit gemeinsamen lokalen Icons und eingebetteten `EventTimerDetails`.

`TimerOverlayRows` ist seit 2026-10-18 die unabhängige Projektion für die neue native Timer-Leiste.
`TimerOverlayChoices` liefert stabile Schlüssel, Auswahl, Icon-ID und Verfügbarkeit. Eine explizite
`TimerOverlayTimerIds`-Liste in AppSettings ersetzt die alte Widget-Auswahl ausschließlich für
die Leiste; `null` übernimmt alte Präferenzen, `[]` bleibt leer. `TimerOverlayPresentation` formatiert
den gemeinsamen nativen/Razor-Countdown. `EventTimerDisplay` projiziert Rift ausschließlich auf
die zehnminütige Öffnung und anschließend den nächsten Start. Tab, Kartenliste und native Leiste
verwenden dieselbe Projektion; `EventWindows`, Kalenderdauer und Reminder bleiben unverändert.
Der optionale `AlertDisplayRow.InlineLabel` reserviert während der Öffnung einen eigenen Namensslot
neben dem Icon, unabhängig vom festen Countdownslot. Die frühere globale Vorfensteroption wird
beim Laden auf 0 zurückgesetzt. `TimerStripRenderer` zeichnet 28-px-Originalicons und
18-px-Noto-Sans-Bold-Text in 32-px-Zeilen auf eine echte Alpha-Fläche ohne Hintergrund.
`TimerOverlayIcons` löst gemeinsame Symbol-IDs auf gebündelte Dateien oder validierte lokale
Kartenpfade auf. `TimerIconCache` lädt abgelöste Bilder, gibt sie mit dem Fenster frei und merkt sich
fehlende Bilder nicht dauerhaft. Die Menüvorschau nutzt den bestehenden lokalen Karten-VirtualHost.
`TimerStripTypeface` lädt die mitgelieferte Schrift privat; installierte Fonts sind nicht nötig. Automatischer Umbruch statt fester Breite;
die Nutzerskalierung wirkt genau einmal. `CountdownRows` bleibt als Kompatibilitätsprojektion.

`NotificationRows` enthält zeitlich aktive
Banner unabhängig von Minimap-/Menü-Unterdrückung. `AlertNotificationsForm` zeichnet diese in einem
separaten Fenster mit eigener gespeicherter Position; gesperrt ist es klickdurchlässig. `AlertsOverlayForm` bleibt das
ausblendbare, interaktive Timer-HUD. `AlertSoundPlayer` liefert asynchrone lokale MP3-Wiedergabe über
Windows MediaPlayer, mit Dateiauswahl, Abbruch bei neuer Wiedergabe/Dispose und Systemton-Fallback.
`SoundFile` ist ein optionales Schema-1-Feld; neue Laufzeit-Netzwerkzugriffe entstehen nicht.


Boss-Timer verwenden in Alerts und Boss Rush dieselbe durchsuchbare `LiveBossTimers`-Komponente
mit vollständigem Katalog. Die reine UI-Sortierung trennt bestätigte Spawns, positive Countdowns
und wartende/unbestätigte Einträge; Navigation und Quellenauswahl bleiben unverändert. Individuelle
`BossReminderPreference`-Overrides in AppSettings steuern Enabled und die optionale LeadSecondsList;
EventAlert besitzt analog LeadMinutesList. Fehlende Listen nutzen die bisherigen Einzelwerte,
ausdrücklich leere Listen erzeugen keine zusätzliche Vorwarnung. Snapshots kopieren die Arrays.
Bosse ohne Override erben die globale Vorwarnzeitenliste beziehungsweise den alten Einzelwert. `BossAlertDurationSeconds` und
`BossAlertUntilSpawn` gelten global für alle Boss-Erinnerungen, bedienbar über der Suche.
Jeder ausgewählte aktive Reminder erzeugt eine sichtbare Notification ohne zusätzliche
Banner-/Minutenfenster-Gates. Legacy-ShowOverlay/AlwaysShowOverlay/OverlayLeadMinutes bleiben
für Store- und Timer-Leisten-Kompatibilität erhalten, beeinflussen jedoch echte Bossnotices und
Tests nicht. Die UI bietet daher mehrere Vorwarnzeiten und optionalen Ton. Bereits zulässig erzeugte
feste Notices überstehen Map-/Streamwechsel bis Expires; Metadatenänderungen löschen sie nicht.
Seit Nutzerauftrag 2026-11-09 wertet BossRushService dieselben CapturedSnapshots wie die Timer-
Projektion aus: alle vollständig bekannten Kartenlisten, einschließlich geladener und nach
Quelllücken erhaltener Stände. BossAlertPolicy.EvaluateMany erlaubt über allowCachedWarnings
explizit Vorwarnungen aus gespeicherten positiven Deadlines, jedoch niemals bestätigte Spawns
oder neue Zyklen aus abgelaufenen Zeiten. Der Standard der Core-API bleibt ohne diese Freigabe
streng. BossNotice.Cached trägt die Herkunft bis zum erklärenden Bannertext. Der periodische
Timer läuft unabhängig von einer geöffneten Karten-/Bossansicht; Navigation bleibt getrennt.
AlertSoundDelivery verbindet NoticeRaised schon vor dem Aufbau der MainForm-Steuerelemente
mit dem eigenen Worker von AlertSoundPlayer. Die automatische Audioausgabe benötigt weder
ein Fensterhandle noch BeginInvoke; die bisherige UI-Weiterleitung konnte frühe oder während
einer UI-Blockade fällige Töne verwerfen. Smoke-Modus, explizite Stummschaltung, abgelaufene
Notices und Shutdown bleiben gesperrt; manuelle Tontests teilen nur den Audioplayer.
Der Vorwarnfenstermodus verwendet eine endliche Frist und einen monotonen Restzeitanker.
Gültige frische Uhrkorrekturen passen nur den ursprünglichen Zyklus an. Bei Restzeit 0
entfernt BossRushService die Quelle; UntilSpawnNotice liefert null und AlertsService entfernt
gebundene Bannerkopien auch beim direkten Lesen ohne weiteren Tick. Ein Karten-/Quellenwechsel
beendet ein positives Fenster nicht vorzeitig; ein neuer anderer Zyklus verlängert es nicht.
Die frühere AwaitingConfirmation-Sonderanzeige entfällt. Ein abgelaufener Timer erzeugt weiterhin
keinen Spawnbeleg; normale Bossliste und Kartenprojektion dürfen den Quellenzustand unbekannt
anzeigen. Manuelles Deaktivieren/Abwählen bleibt möglich. Tests, feste Anzeigedauer und tatsächlich
bestätigte Spawnmeldungen behalten ihre Sekundendauer. Stille Korrekturen erzeugen keinen neuen
Ton. Die native Begrenzung auf sechs Zeilen mit Überlaufhinweis bleibt; Fensterquellen blockieren
nicht die feste Queue. Alte per-Boss-Dauerfelder werden beim JSON-Lesen ignoriert, ohne daraus
einen globalen Modus zu erraten.
`AdvanceWarningPicker` wird von Bosskarten, Eventdetails, Resets, eigenen Alerts und den
Boss-Standardeinstellungen verwendet. Die Bosskarten beider Tabs aktivieren außerdem die vorhandene
unabhängige Timer-Leisten-Auswahl. `AdvanceWarnings` normalisiert positive, eindeutige Offsets.
`BossAlertPolicy.EvaluateMany` trägt die Vorwarnstufe über BossAlertTrigger und BossNotice bis in
die persistenten Auslieferungsbelege. Bei mehreren überschrittenen Stufen wird nur die zuletzt
fällige ausgeliefert; ältere Stufen werden konsumiert. Alte Boss-lead-Belege ohne Stufe verhindern
eine erneute Sofortwarnung, erlauben aber spätere neue Stufen. AlertsService behandelt Kalender-
Vorwarnungen ebenso und holt nur den nächsten zukünftigen Termin nach. Die bisherige 30-Sekunden-
Toleranz für Startmeldungen bleibt. Weitere Stufen ersetzen die Banner-Identität desselben Bosses,
behalten jedoch den monotonen Fensteranker und dessen Ablauf. Die Tonausgabe erfolgt je Stufe.
`BossAlertPolicy` löst weiterhin allein aus; ein per-key Resolver kann einen Boss pausieren,
ohne die Timerauswahl zu verändern. Queue- und AlertsService-Prüfungen verwerfen deaktivierte
echte Hinweise. Bestehende Zyklusidentität und persistente Stufenbelege verhindern Doppelmeldungen beim Wechsel
zwischen gespeichertem und frisch beobachtetem Stand sowie bei wiederholtem Neustart.
Spawnbestätigungen bleiben an die frische Quelle gebunden. BossTimerCache behält sein atomisches
Schema 1 mit Serverzeit-/UTC-Speicheranker; vollständige Listen ersetzen nur ihre jeweilige Karte.
Monotone In-Session-Fortschreibung, Prüfung beschädigter Dateien und Rückwärtsuhr-Gate bleiben erhalten.

`EventTimelinePresentation` berechnet eine reine Timeline-Projektion aus dem lokalen Katalog
und erfassten Bossdaten; keine erfundenen Bosszyklen. `EventTimelineOverlayForm` ist ein eigenes
natives, per Schloss/Alt bedienbares Fenster mit 12/24/48 Stunden. AppSettings speichert Position,
Skalierung, Zeitraum, separaten Sichtbarkeitsschalter und optionale Kopplung an die Minimap.
Der Host wendet die HUD-Unterdrückung nur bei aktivierter Kopplung an; kurze Banner bleiben separat.

### Kompakte Alert-Overlay-Größen (2026-10-12)

AppSettings hält separate logische Breite/Skalierung/Position für Timer-HUD, Kurzbanner und
Timeline. Revision 4 übernimmt beim ersten Laden die frühere gemeinsame HUD-Position/Skalierung
für Banner; spätere Anpassungen bleiben unabhängig. Größenregler verwenden die physische Breite;
Monitoranpassung ist vorübergehend. Native Resize/Drag speichert beim Abschluss. Details stehen
in ALERTS.md; die Scheduler-/Deduplizierungslogik bleibt unverändert.

## Verworfenes Feature: DPS-Meter (2026-10-19)

Der Nutzer hat den DPS-Meter vollständig verworfen. Die zugehörigen Komponenten,
Datenverträge, Erfassungsabnehmer, Historie und Katalogressourcen wurden entfernt.
Die bestehende Pet-, Fortschritts- und Boss-Timer-Erfassung bleibt eigenständig erhalten.



## Overlay-Verwaltung und Platzierung (2026-10-20)

`OverlayRegistry` inventarisiert acht Overlayarten und adaptiert die bestehenden absoluten
Layoutfelder. `OverlayVisibilityPolicy` entscheidet je freiem Panel über vier überlappende
Kontextfreigaben; Settingsrevision 5 übernimmt die bisherigen Präferenzen und gibt Boss-Spawns
auf Nutzerwunsch in allen Kontexten frei. Fehlende Minimap und Menü bleiben getrennte Bedingungen.

`OverlayPlacementService` liefert Monitor-/Moduszustand und Änderungen. Der native Host
verbirgt die echten Fenster mit `SetPlacementHidden`, statt deren Fachzustand zurückzusetzen.
`OverlayPlacementForm` zeichnet Dimmung, Raster, Auswahl und reine Vorschauinhalte in einem
aktivierbaren Layered Window. `OverlayPreviewRenderer` nutzt die Produktions-Zeichenmethoden;
keine Ereignisse werden zur Vorschau ausgelöst. Persistenz ausschließlich über bestehende
Settingsfelder und atomare Updates am Geste-Ende. Physische Desktopkoordinaten einschließlich
negativer Monitorursprünge; ein Layout, keine Monitorprofile. Monitoränderungen beenden sicher.


Die gemeinsame Einstellung `OverlayShowMode` ergänzt die Kontextpolicies um ein globales
Vordergrundgate (foreground standardmäßig, optional always). Der Host verteilt dafür einen
eigenen Präsentationszustand `SetForegroundSuppressed` an alle acht Fenster; Placement und
Vordergrund sind unabhängige Bedingungen. Händlerprüfungen wählen bei always die sichtbare
Spielgeometrie ohne Vordergrundzwang, behalten aber Fensterbindung, Frische und Ankerprüfung.


## Daevanion (2026-10-20)

`Core.Daevanion` enthält den lokal gebündelten Darstellungskatalog mit sechs Boardfamilien für acht
Klassen. Statboni und Skillstufenboni bleiben unterschiedliche Effekte; Nodes können zwei Statboni
enthalten. Die optionale Klasse der eigenen Netzwerkidentität und ihre Generation bilden den
aktuellen Daevanion-Kontext, ohne die Gültigkeit einer Own-Identität an Klassenunterstützung zu binden.

`Ocr.Daevanion.DaevanionBoardLocator` erkennt native Rahmen, Startkontur und Rastermaske.
`DaevanionService` nutzt die gemeinsame WGC-Spielaufnahme, bestätigt zwei unterschiedliche Frames
und verbindet Klasse, Board, Geometrie, Sprache und Frische in einem Snapshot. Unmittelbar vor
Darstellung prüft `DaevanionOverlayForm` den Kontext erneut. Der gemeinsame Produktionsrenderer
zeichnet rund maskierte Inhalte und eine qualitätsfarbene Stat-Fassung außerhalb nativer Rahmen.
Die Fassung passt ihre Erweiterung an den erkannten Rasterabstand an; Menüvorschau und
Offline-Bildprüfung verwenden denselben Painter.

Frische Node-Anker tragen zusätzlich ihre beobachtete aktive, inaktive oder unbekannte
Erscheinung. Sie beeinflusst den runden Stat-Hintergrund, keine gespeicherten Fortschrittsdaten.
Node-Grafiken werden begrenzt nach Inhalt, Größe, Sprache, Erscheinung und Rasterabstand
zwischengespeichert. Während des Zeichnens eintreffende Tooltipmasken entfernen nur nicht mehr
belegte Grafiken und ergänzen neue; die verbleibende bestätigte Teilfläche wird atomar präsentiert.

`DaevanionNodeContentVisible` ist eine dauerhafte reine Anzeigepräferenz, getrennt von
`DaevanionOverlayEnabled`. Ein unabhängiges, nicht aktivierendes Toggle-Fenster bleibt bei
verborgener Node-Fläche erreichbar. Seine Freigabe ist an aktuelle Board-/Charakterevidenz gebunden;
der 100-ms-Sichtbarkeitstick prüft sie auch ohne gezeichnete Nodes. Umschalten invalidiert weder
Raster noch Snapshot und leert keinen Grafikcache. Hauptschalter und Hotkey behalten ihre vollständige
Abschaltfunktion. Der gemeinsame Toggle-Painter und die vollständige Rastergeometrie erzeugen auch
die Offline-Vorschauen beider Zustände.

`DaevanionHighlightedStatIds` ergänzt eine globale, persistente Stat-Auswahl in Katalogreihenfolge.
Das unabhängige Stat-Rail-Fenster bindet NoActivate-Klicks an den aktuellen Boardkontext und nutzt
vollständige Geometrie für Position/Paging. Platzmangel ist reines Präsentationsfeedback im
`DaevanionService`, ohne Quellen- oder Revisionsänderung. Der Highlight-Produktionsrenderer zeichnet
auf einer separaten klickdurchlässigen Fläche einen umlaufenden Lichtzug. Ein Timer mit 33–66 ms Abstand läuft nur
bei sichtbaren Treffern; jeder Tick prüft aktuelle Auswahl und bestätigte Quelle. Reconcile entfernt
auch bei kleinen Nodes das vollständige Highlight-Envelope. Node-Grafikcache und optionale
Node-Fläche bleiben davon unabhängig; Preview und Live teilen Layout und beide Renderer.
Die animierte Kontur wird je Größe/Abstand/Phase einmal pro Renderaufruf gerastert und auf alle
Treffer kopiert, weiterhin unter individuellen Welt-/Nachbarclips. Temporäre Stamps werden entsorgt.


## Ereignisgesteuerte Kartenruhe (2026-10-21)

Der Kartentracker leitet seine Arbeitsweise direkt aus Netzwerkschalter und Weltkontext ab.
Mit aktivierter Netzwerkquelle erlaubt nur eine bestätigte unterstützte offene Karte visuelle
Minimaparbeit gegen genau diese Kartenreferenz. Dungeons/Eventinstanzen, Übergänge und noch
unbestätigte Zustände warten ohne Map-Capture, UI-Probes oder Recovery auf eine Änderung.
Der visuelle Instanz-Fallback ist ausschließlich bei ausgeschaltetem Netzwerkschalter aktiv.
Die vorhandenen Quellen-/Kartengenerationen schützen weiterhin Positionen und Zielmutationen.
Öffentliche Builds enthalten keinen Instanzkatalog (Nutzerentscheidung 2026-10-21). Ohne Ortsfakten
kann der Netzwerkstrom kein Gebiet bestätigen; der Kartentracker und die HUD-Kontexte arbeiten dann wie
bei ausgeschaltetem Netzwerk-Tracking, und die Ortsanzeige im Map-Tab entfällt.
Eine flüchtige Revision des Netzwerkschalters entwertet alte Bildarbeit auch nach einem schnellen
Aus-/Ein-Rundlauf mit demselben Endwert; sie wird nicht als neue Profileinstellung gespeichert.

HUD-Kontextfreigaben werden bei Karten-, Weltkontext- und Einstellungsänderungen angewendet.
Der Interaktionstimer behält Tastatur-/Vordergrundaufgaben. Bei ausgesetzter visueller Beobachtung
wirkt die bestätigte Instanzfreigabe ohne zusätzliche Menü-/Minimapannahme. Die gemeinsame
Bildaufnahme wird nicht global beendet; Händler- und Daevanionverbraucher bleiben unabhängig.

## Gespeicherte Kartenansichten (2026-10-21)

AppSettings.MapViewports hält native X/Y-Mitte und Leaflet-Zoom je Karte; alte Einstellungen
starten mit leerem Bestand ohne Migration. MapViewport validiert finite Werte. Die interaktive
Karte puffert Bewegungen und meldet ausschließlich endgültige Ansichten mit einer kartenbezogenen
Präsentationsrevision zurück. MapView akzeptiert nur bekannte, von ihm angeforderte Karten und
die jeweils neueste Revision. Der Zustand bleibt von Netzwerk- und Positionsepochen getrennt:
Eine Ansicht ist kein Aufenthalts- oder Spielerbeleg.

MapView wählt anhand des bestätigten Netzwerkorts die unterstützte Karte vor dem ersten visuellen
Spielerfund, unabhängig von Follow-/Progressionsschaltern. Diese steuern weiterhin Zentrierung
und Wegführung. Manuelles Browsen bleibt bis zum nächsten tatsächlichen Netzwerk-Kartenwechsel erhalten.
Erneutes Own derselben nativen Karte sowie reine Inhaltsverfeinerungen erzwingen keinen Wechsel.

## Loot-Sessions: Statistik und synchroner Replay (2026-10-27)

`UnifiedLootTrackerService` ist der einzige aktive Writer für neue Loot-Sessions.
Er serialisiert die bestehenden qualifizierten Farm-, Kina-, Sammel- und Abyss-Fakten
unter einer Session-ID und einer monotonen Sessionuhr. Die bisherigen Farm- und
Lifeskill-Sessionwriter sowie die alte Abyss-Sessionengine sind entfernt.
`AbyssProgressService` pflegt ausschließlich den unabhängigen absoluten Punktecache
und dessen Rangfortschritt. Er abonniert keine Einkommensereignisse, kennt keine
Sessionjournale oder Routen und erwirbt keine Recorder-Lease. Der Rankadapter
`AbyssRankOverlaySource` projiziert Punktecache, aktuelle gemeinsame Session und
flüchtigen Killfeed; das Loot-HUD verwendet allein `UnifiedLootOverlaySource`.

Neue Open-World-Sessions tragen `Kind=Mixed`, Abyss-Aufenthalte `Kind=Abyss` und
alle neuen Sessions `UnifiedVersion=1`. Das Feld `Kind` hält den Weltkontext fest. Jeder
Timelinefakt trägt `Origin=Pve`, `Lifeskill` oder `Abyss`; auch im Abyss behalten
Sammelversuche `Lifeskill` als Herkunft. Gemeinsame Items werden über `Item:{id}`
zusammengeführt, nicht über quellenspezifische Anzeige-Keys.

Unveränderliche Snapshots tragen Sessiongesamtwerte, exakte Ereignisse und getrennte
Bewegungssegmente. `LootTrackerSessionProjection` bildet Gesamt-, PvE-, Lifeskill-
und Abyss-Ansichten aus demselben Parent: ID, Weltkontext und gemeinsame Dauer bleiben
erhalten, nur passende Fakten und daraus berechnete Werte wechseln. Projektionen
schreiben keine Journale. Ältere unvollständig trennbare Ansichten werden kenntlich
gemacht; fehlende Belege bleiben unbekannt. Sortierung, Gruppierung, Minutenwerte
und Scrubben verändern weder Speicherung noch Beute- oder Sessionzählung.

`TimelineVersion`/`TimelineEvents` ergänzen den Sessionvertrag. Jede bestätigte AP-/RP-/Kina-
Änderung und jeder vollständige Drop beziehungsweise Sammelreward erzeugt genau ein Ereignis
an derselben bereits deduplizierten Mutation wie seine Mengen. Eigene Kampfmarker bleiben
ein separater Ereignisstrom; anonyme validierte Klasse und tatsächlicher Gegner-Ranggrad
werden darin eingefroren, niemals Namen oder native Kennungen. Ein Punkte-Stufenmarker
entsteht ausschließlich aus kontinuierlichen absoluten eigenen Rangfakten und bucht keine
weiteren RP. Erste Bestände, Wiederanbindung und Quellenlücken erzeugen kein Einkommen.
Verfügbarkeitswechsel unterscheiden unqualifizierte Kanäle von bestätigten Nullwerten.

`UnifiedLootSessionStore` schreibt ein separates Journal unter
`loot-tracker/unified-sessions/<id>/`: Schema 1 mit absoluten Sessionwerten und
angehängten Timeline-, Routen-, Kampf- und Sammeldeltas in `events-0001.jsonl`,
dazu `summary-v1.json` als atomarer Checkpoint. Der letzte vollständige Journalrecord
ist maßgeblich; Checkpointtotals werden nie dazuaddiert. Der Validator prüft Mengen
und bekannte Kanal-/Versuchszähler gegen die exakten Fakten. Recovery erhält die
committete monotone Dauer und Fakten auch bei kleinen Abweichungen zur UTC-Spanne;
fehlende Daten werden nicht ergänzt. Ein unklarer Schreibzustand unterbricht die
Aufzeichnung, statt halbe Rewards oder weitere Fundhinweise zu veröffentlichen.

`UnifiedLootLegacyHistory` lädt die bisherigen Farm-/Abyss-Journale bis Schema 5 und
Lifeskill-Journale bis Schema 4 im Hintergrund als Historie. Es gibt keine Migration
oder neuen Appends in diese Journale. Historische echte Erntezeilen bleiben nutzbar,
alte Minutenbins ausdrücklich aggregiert; fehlende Wege, Einzelereignisse und
Gegnerkategorien werden nicht rekonstruiert. Manuelle und seit 2026-11-09 ausdrücklich
beauftragte automatische Kleinsessionlöschungen verwenden den zuständigen alten Store.
Tages- und absolute Charaktercaches bleiben getrennt
von der Sessionlöschung und liefern keine nachgetragenen Sessionereignisse.

Frische Positionsfakten werden nach Karten-/Bindungs-/Beobachtungslücken segmentiert.
Open-World-Bewegung seit dem letzten positiven Loot bleibt vorläufig; Pause, Ende
und Recovery verwerfen unproduktiven Nachlauf. Der hochfrequente Recorder puffert
höchstens 4.096 Proben und 20 Sekunden bis zur persistierten Beutebuchung. Pufferverluste
erzeugen echte Lücken. `SessionPaused` und `StartsAfterPause` trennen HF-/Fallbackspuren
ohne durchgezogene oder geschätzte Verbindung über die Pause. Bewegung verlängert
keinen Timeout. Der Replay
zeigt den beobachteten Weg; die Kartenübernahme verwendet ausschließlich die aus
bestätigten Lifeskill-Sammelstellen gebildete optimierte Route, keine Laufspur,
Kampfpositionen oder Monsterdropstellen.

Die gemeinsame Replayachse ist die gespeicherte monotone Sessiondauer; außerhalb
des Abyss wird das gesamte Intervall zwischen letztem Gewinn und Wiederaufnahme nach
einer Pause abgezogen. Ursprüngliche UTC-Faktzeiten bleiben unverändert erhalten.
Zukunft
wird am Playhead entfernt, fehlende Zeiten liefern keine synchrone Position. Karte und
Spielerpunkt folgen nur bestätigten Positionen; nach einer Beobachtungslücke bleibt die
Kamera am letzten Ort und der frische Spielerpunkt erlischt. Punkte-Stufen, Gegnergrade,
Walletbestand, Farm-Cap und Sessioneinkommen bleiben getrennte Größen.

Nutzerergänzung 2026-10-27: Gelaufene Route und Session-Timeline bilden ein
zusammenhängendes Replay-Modul mit gemeinsamer Mediensteuerung: Abspielen/Pause,
Vorwärts-/Rückwärtswiedergabe, Zehn-Sekunden-Sprünge sowie Anfang/aktuelles Ende.
Geschwindigkeiten 0,25x, 0,5x, 1x, 2x, 4x und 8x; Start mit 1x. Wiedergabezustand
und Tempo bleiben lokal im geöffneten Modul, Sessionwechsel pausiert und setzt
zurück. Die monotone Wiedergabeuhr verändert ausschließlich den Playhead,
keine Statistik, gespeicherten Ereignisse oder Sessiontimer.

Nutzerergänzung 2026-10-27: Routenmarker zeigen die Originalicons ihrer tatsächlich
zugeordneten Beute mit der Katalog-Seltenheitsfarbe und bestätigten Mengen. Ernten
werden nur über ihre exakte Sammelstellenidentität zusammengefasst, Drops nur an
identischen gespeicherten Kartenkoordinaten. Der Playhead begrenzt Icons, Mengen
und Besuchszeiten gemeinsam. Neue Drop-Fakten speichern die bereits qualifizierte
frische Minimap-Position optional atomar mit der Beute; historische oder unplatzierte
Beute erhält keine nachträglich geschätzte Position. Killmarker zeigen wahlweise
das eingefrorene Gegner-Rang- oder Klassenicon, ausgegraut mit Totenkopf. Die globale
Auswahl bleibt gespeichert, unabhängig von Replayartefakt und Playhead; fehlende
Gegnermetadaten verwenden einen neutralen Platzhalter mit Totenkopf. Das Laden
alter Journale und Replayartefakte ohne Drop-Position bleibt kompatibel.

### Gemeinsame Lebensdauer und Overlay-Publikation

Außerhalb von Abyss startet bestätigte Beute beziehungsweise ein bestätigter
Sammelversuch die gemeinsame Session. Seit 2026-11-09 erneuern nur positive Gewinne
(Items, Souls, Kina, AP/RP) die Frist. Nach 20 Sekunden ohne Gewinn bleibt die Session
`Live`, wird aber mit `IsPaused=true` und auf den letzten Gewinn zurückgerechneter
Dauer gespeichert. Neuer Loot vor 120 Sekunden entfernt die gesamte Zwischenzeit
einmalig aus der monotonen Uhr und setzt dieselbe ID fort. Bei 120 Sekunden, Teleport
oder bestätigtem Kartenwechsel endet die Session. Fehlversuche und Verluste bleiben
am letzten produktiven Zeitpunkt gespeichert, ohne die Frist zu verlängern. Ihre
echte UTC-Zeit bleibt als Faktgrenze erhalten. Der 07:00-UTC-Farmtagwechsel trennt
Open World. Das Kina-Cap markiert lediglich dieselbe Session als Limited Kina; es startet keine zweite
Session und setzt weder Dauer noch Mengen zurück. Abyss beginnt beim bestätigten
Eintritt, endet beim Austritt und hat keinen Aktivitätstimeout. Normale Übergänge,
Tod sowie Daily-/Weekly-Reset teilen den Aufenthalt nicht; fehlende aktuelle
Own-/Profilbindung außerhalb erlaubter Übergänge und echte Quellenlücken unterbrechen.

Die automatische Kleinsessionbereinigung läuft serialisiert im selben Worker und
lässt wartende Lootbefehle vor. Standard ist eine gespeicherte Itemmengenschwelle von
20; 0 schaltet sie ab. Abgeschlossene/unterbrochene Nicht-Abyss-Sessions darunter
werden einschließlich alter Historie über den vorhandenen Löschpfad entfernt, mit
Replayende, Tombstone und Bewertungsabbruch. Live-/pausierte Sessions, unbekannte
Itemmengen und beschädigt gelesene Historie bleiben geschützt. Einstellungen werden
unter dem Speicherschloss gelesen; ein fehlgeschlagener Settings-Save kann keine
vorübergehende Löschfreigabe erteilen. Fehler werden sichtbar, ohne Tick-IO-Schleife.
Der bestehende Diagnosebeitrag `loot-sessions` projiziert Pausenzahl, feste Fristen,
Schwelle, Löschzähler und unvollständige Bereinigung anonym aus vorhandenen Diensten.
Snapshotalter und Zeitrichtung werden mitgeprüft; höchstens fünf Sekunden alte
Beobachtungen dürfen bei verfügbarer Quelle Healthy sein.

Jeder Kanal behält seine eigene Own-/Generations-/Sequenzdeduplikation. Quelle,
Welt und Faktzeit werden beim Empfang und vor der seriellen Mutation erneut
geprüft. Eine alte Beobachtung kann keinen neueren Weltkontext zurückrollen.
Zähler werden nur bei qualifizierter Verfügbarkeit auf bekannte Null gesetzt.
Keine Sessionlogik ersetzt die bestehenden Producer- oder Transportqualifikationen.

`UnifiedLootOverlayState` veröffentlicht aktive qualifizierte Session, Verfügbarkeit,
eigenen Tagesfakt, profilgebundenen gespeicherten Tagesstand und Bestätigungsstatus
als ein unveränderliches Objekt. Der Overlayadapter liest diese Instanz atomar;
Charakterwechsel kombinieren daher keine Session mit einem fremden Tagescache.
Ein gespeicherter Stand ersetzt keine aktuelle Netzbestätigung. Quellenverlust
blendet die aktive Sessionprojektion aus, ohne historische Werte zu löschen.

## Exchange-Bewertungen im Loot-Tracker (2026-11-01)

Neue bestätigte Loot-/Erntefakten speisen zusätzlich einen unabhängigen
Bewertungsdienst. Der Exchange-Client lädt explizit serverbezogene Angebote,
führt einen lokalen Cache und verwendet weder Spielzugangsdaten noch Charakterdaten.
Je lokalem Profil werden Markt und Gebühren ausgewählt. Diese Angaben werden
bereits beim Loot zusammen mit Session-, Fakt- und Itembezug festgehalten.

Unveränderliche Bewertungstranchen liegen getrennt unter
`loot-tracker/market-valuations/`. Die originale Sessiontransaktion und ihre
Timeline bleiben unverändert. Preisantworten lösen keine Beute, Timer oder Alerts
aus. UI, Tagesansichten und Overlay summieren dieselben Tranchen; unbewertete
Mengen und der Preisstand bleiben sichtbar. Bestehende eigene Marktbelege behalten
ihre ursprüngliche Quelle und Speicherung.


## Main-/Sub-Stats aus dem eigenen Netzwerkstand (2026-11-03)

`CharacterDisplayStatProjection` ergänzt eine versionierte Anzeigeprojektion neben dem
Combat-Score-Modell. Die vollständige eigene Statliste und absolute Änderungen liefern
Komponenten; vollständig validierte eigene Eintrittsdaten und Ressourcenupdates liefern
HP-/MP-Maxima direkt. `NetworkLootService` bindet diese ausschließlich an den aktuellen
eigenen Transport und die Entity. Beide Maxima behalten ihre eigene Faktzeit.

`CharacterCombatStateService` veröffentlicht `DisplayStats` synchron im unveränderlichen
RAM-Snapshot. Die Oberfläche trennt direkte Werte, geprüfte Ableitungen, Datenlücken und
Widersprüche; Rohkomponenten und Modellrechnung bleiben separat aufklappbar. 16 Haupt-/
Primärwertzeilen und 73 Nebenwertzeilen bilden den aktuellen Zuordnungsscope. Intelligenz,
weitere Stat-IDs und nicht qualifizierte Gottheitszuordnungen bleiben offen; Absenz ist kein
Nullwert. Die acht Hauptwertformeln sind zunächst für Kleriker qualifiziert. Referenzstand
ist Build 26262663; die Anzeigezuordnung erweitert keine allgemeinen Effektverträge der CP-Bewertung.

Die Projektion verwendet exakte Dezimalrechnung. Prozentanzeigen besitzen zusätzlich ihren
ungerundeten Wert. Empfangslücken, widersprüchliche Formate und Charakterwechsel entwerten
betroffene Eingaben. Direkt beobachtete Maxima haben Vorrang; eine abweichende Vorwärtsrechnung
bleibt sichtbar. Maschinenlesbare DOM-Felder tragen Wert, Status, Belegstufe, Version,
Generation und Faktzeit für lokale Debugprüfungen ohne visuelle Spielauswertung.


## Gemeinsamer Combat-Score-Arbeitsbereich (2026-11-05)

`CombatScoreWorkspaceView` ersetzt die beiden bisherigen Hauptnavigationseinträge.
`UiState.CombatScoreSection` hält ausschließlich im RAM den aktiven Unterbereich;
`Navigate("daevanion-calculator")` bleibt als Alias für den Untertab Daevanion Board
bestehen. Ein direkter Aufruf von combat-score öffnet Stat Overview. Die Route-
und Entwurfsformate sowie die Overlay-Anbindung des Boardrechners bleiben erhalten.

`CombatStatOverviewCatalog` enthält reine Anzeigedefinitionen: 28 Main-/Attribut-
zeilen und 169 Sub-Statzeilen in neun Kategorien. `CombatStatOverviewView` übernimmt
Werte nur aus der bestehenden qualifizierten `CharacterDisplayStats`-Projektion.
Nicht gebundene God-/Ragewerte bleiben unbekannt; fehlende Einträge bedeuten keine
Nullwerte. Metadaten und Icons erweitern keine Wire- oder CP-Bewertungsverträge.

`CombatSourceCategoryView` trennt beobachtete Quellenstände von Modellbeiträgen.
Gear, Pets, Genus, Titel und Erfolge verwenden ihre eigenen Domänen und Frische-
prüfungen. Flügel- und Pantheon-Lücken bleiben sichtbar. Pantheon wird weder aus
Monolith- noch aus Daevanion-Effekten rekonstruiert. Modellverluste bleiben ausdrücklich
Schätzwerte und nicht addierbar. Die bisherige detaillierte Modell-, Quellen-,
Rohdaten- und Skillansicht liegt im aufklappbaren Modellbereich von Stat Overview.

`CombatWorkspaceCatalog` hält die Präsentationsrouten und relativen Original-Iconpfade.
Neutrale Kategoriesymbole sind reine UI-Auswahl; sie begründen keine Statzuordnung.

Der Achievement-Browser verwendet `CombatAchievementSearchIndex` für deutsche und
englische Namen, Belohnungen und exakt zugeordnete Titelbelohnungen aller Stufen.
Ein genauer Titelname öffnet die zugehörige Katalogstufe. Der reine
`CombatAchievementFactionCatalog` filtert Familien anhand von `AppSettings.Faction`;
gemeinsame und unbekannte Zuordnungen bleiben sichtbar. Die Zuordnung ist an den
Metadatenstand gebunden. Diese Browserfilter verändern weder Charakterhistorie
noch den eigenen Overlay-Datenstrom. Gewöhnliche Belohnungen erscheinen als Icons
mit Menge und Tastaturtooltip, zugeordnete Titel zusätzlich mit ihrem Namen.

## Gemeinsamer Marktpreis-Cache (2026-11-06)

Lootbewertung und aktuelle Overlay-Marktwerte verwenden denselben Cache-Abrufpfad.
Wie in Grindcrest wird ein laufender Abruf geteilt: Nach dem gemeinsamen Gate wird
der Cache erneut geprüft. Überlappende Nachfragen laden nur noch fehlende Items,
in höchstens 100er-Batches und über den vorhandenen fünfsekündigen HTTP-Abstand.
Positive Preise gelten zehn Minuten. Erfolgreiche Lookup-Quittungen ohne zulässigen
Preis sperren weitere Abrufe desselben Items gemeinsam für eine Minute. Damit
bleibt die bestehende zehnminütige Erstbewertungsfrist nutzbar; neue Beutefakten
umgehen die Sperre nicht. Die Serverliste wird ebenfalls zehn Minuten gecacht. Server- und Item-IDs trennen die Preisstände;
Profilgebühren gehören weiterhin ausschließlich zur Wertberechnung.

Erfolgreiche Lookup-Quittungen werden zusätzlich zu Quotes anonym und begrenzt in
der bestehenden Schema-1-Cachedatei gespeichert. Alte Dateien ohne dieses optionale
Feld bleiben lesbar. Ein vollständig erhaltener Batch wird sofort übernommen;
spätere Fehler verwerfen seine Preise nicht. Netzwerk-, Timeout- und Antwortfehler
teilen eine wachsende Pause von 30 bis 600 Sekunden; längeres Retry-After gilt als
Mindestpause. Abbruch durch einen Verbraucher gilt nicht als Anbieterfehler.

Ein erfolgreicher Batch ändert Preise, Fehlpreise und Abrufzeiten zusammen und
speichert einmal atomar. Dateischreiben ist separat serialisiert und hält den
kurzen Leselock nicht über Flush/Replace. RAM-Werte bleiben bei Speicherfehlern
nutzbar. HTTP-Fehler löschen keine vorher zulässigen Preise. Erfolgreich bestätigte
Fehlpreise bleiben unbekannt; das bisherige Quellenhöchstalter von zwei Stunden,
Offline-Schalter und eingefrorene Bewertungen bleiben unverändert.

Die Overlay-Präsentation zeigt seit Nutzerentscheidung vom 08.11.2026 einen
vorhandenen bekannten Teilwert als `N0 + ?`, wenn bewertete und unbekannte
Itemarten gemeinsam vorliegen. Total und Stundenrate verwenden denselben
qualifizierten Snapshot; `NetKina` bleibt bei unvollständiger Abdeckung null.
Der Zusatz ist Teil des dargestellten Werts und unabhängig vom Label-Schalter.
Ohne bewertete Zeile, bei ungültiger Session-/Profilbindung oder fehlender
Ratenlaufzeit entsteht keine Ersatzanzeige. Preise, Cache und Belege werden
durch diese reine Formatierung nicht verändert.

### Historische Exchange-Referenzen (08.11.2026)

Nach expliziter Freigabe werden `last_price` und `last_seen_at` aus derselben
öffentlichen Items-Antwort getrennt von aktuellen Quotes übernommen. Die optionale
Details-Capability erweitert den vorhandenen Client, während sein alter
`GetQuotesAsync`-Vertrag ausschließlich aktuelle Quotes liefert. Der gemeinsame
Cache ruft je Batch genau eine Variante auf; Mindestabstand, Abbrüche, Negative-TTL
und Backoff bleiben gemeinsam. Historie unterdrückt keine aktuellen Wiederholungen.

Historische DTOs und ihr optionaler Schema-1-Persistenzblock enthalten nur
Server-/Item-ID, Preis und Zeitpunkte. Rückläufige LastSeenAt-Werte ersetzen keine
jüngeren Beobachtungen. Die Historie ist mengen-/bytebegrenzt; ungültige optionale
Historienblöcke verwerfen keine gültigen aktuellen Cachewerte. Das bisherige
`GetQuote` und alle eingefrorenen Bewertungen bleiben historyfrei.

Die Editorprojektion nutzt pro Item genau einmal einen gültigen aktuellen Preis
oder die historische Referenz mit denselben Mengen, Gebühren und Rundungsregeln.
`NetKina`, `PartialNetKina` und aktuelle Abdeckung behalten ihre ursprüngliche
Bedeutung. `KnownEstimateNetKina`, historische Abdeckung und LastSeenAt-Grenzen
beschreiben die ergänzende Schätzung. Ein immer sichtbares `≈` plus Alterszeile
kennzeichnet sie; `+ ?` beschreibt nur den weiterhin unbewerteten Rest. Der Renderer
reserviert die Alterszeile auch bei ausgeschalteten Labels und hoher Schriftgröße.

## Stabile Overlay-Bildausgabe (2026-11-06)

Die eigene Offscreen-WebView liefert PNGs über Chromium `Page.captureScreenshot`
mit `fromSurface:true` und `captureBeyondViewport:false`. Dieser Oberflächenabzug
erhält Alpha und liefert den aktuellen Rasterstand; `fromSurface:false` wäre ein
opaker Viewabzug. DOM-/Assetbereitschaft, genau eine laufende Captureoperation,
Abbruch-Epochen, die bisherige Drossel und die logische Pixeldichte bleiben erhalten.
Spät abgeschlossene Abzüge werden nach Abbruch nicht präsentiert.

Ein separater Decoder prüft JSON/Base64, PNG-Signatur, IHDR und erwartete Maße vor
GDI sowie ein von der Pixelzahl abgeleitetes Größenbudget. Die Ausgabe bleibt neu
kopiertes `32bppPArgb`. Transparente gültige Frames werden weiterhin unterstützt;
kein Einfrieren alter Inhalte und keine Lockerung der Sichtbarkeitsprüfung.

Die native Livefläche setzt die Aufnahmesichtbarkeit nur bei neuem HWND oder
geänderter Policy. Ausschließlich erfolgreiche Win32-Aufrufe werden gecacht;
Fehler bleiben erneut versuchbar und Handlezerstörung verwirft den Zustand auch
bei Wiederverwendung derselben Handlezahl. Der Aufnahmeausschluss bleibt unverändert.

Ein alter nativer Abzug bleibt nach Auftragsabbruch eine Serialisierungsbarriere,
sein späterer Fehler oder Abbruch gehört aber nicht zum Folgeauftrag. Auch der
Livekoordinator darf einen Fehler einer widerrufenen Epoche weder als aktuellen
Fensterfehler markieren noch eine weiterhin zulässige Fläche verstecken.

Änderungen der reinen Abyss-Weltsichtbarkeit widerrufen nur Fenster mit Killfeed
oder Rangfortschritt. Session-, Tracking-, Profil- und Bindungswechsel bleiben
globale Grenzen; die Fenstertrigger und die fachliche Quellenprüfung gelten weiter.

Nur bei ausdrücklich gesetztem gültigem `SOULCREST_WEBVIEW_DEBUG_PORT` sammelt ein
RAM-Ringpuffer die letzten 128 anonymen Policy- und Lebenszyklusereignisse. Der
lokale DOM-Diagnoseblock enthält boolesche/nullable Beobachtungen, Filter, Ursachen,
Fensterordnungen und monotone Zeiten. Keine Namen, IDs, Positionen, Fachwerte,
Fehlermeldungstexte oder Dateizugriffe. Die Diagnose startet keine Quelle; ohne
Debug-Port laufen weder Sammlung noch DOM-Timer.

### Aktualisierung während der nativen Bildausgabe

Ein `Changed` kann während `ShowFrame` eintreffen. Die reine Änderung der
Renderrevision widerruft dann keine weiterhin gültige Anzeige: Der Koordinator
gleicht einen noch nicht angewandten Stand synchron ab und prüft danach die
ursprüngliche Slotidentität, aktuelle Einstellungen, Quellenautorität und Policy.
Bei gleicher Autorität bleibt der letzte Frame sichtbar, während der Folgeframe
gerendert wird. Echte Session-/Tracking-/Charakter- oder Triggerverluste verbergen
weiterhin sofort; entfernte/disposete Slots werden nicht erneut angesprochen.

Der synchrone Abgleich hat höchstens zwei Versuche. Bleibt der Stand instabil,
wird konservativ ausgeblendet; es gibt keine unbegrenzte Schleife oder Freigabe
auf Basis eines ungeprüften alten Stands. Bereits angewandte Revisionen werden
auch durch nachlaufende UI-Callbacks nicht noch einmal angewandt. Ein während
des Lesens verworfener Snapshot wird nicht zum Autoritätsvergleich übernommen.
Die optionale RAM-Diagnose protokolliert erfolgreiche Überbrückungen als
`retain / same-authority-refresh`, ohne zusätzliche Fachdaten zu lesen.

### Theme-Painter für die Editorfläche

`OverlayEditorThemes` projiziert die sechs bestehenden `OverlayThemeDefinition`-
Paletten und Schriftfamilien in ausschließlich auf die Overlayfläche begrenzte
CSS-Variablen. `OverlayEditorCanvasFrames` ruft denselben nativen Panel-Painter
für transparente PNGs auf. Ein synchronisierter LRU-Cache hält höchstens 64
Einträge beziehungsweise 8 MiB URI-Zeichenspeicher; Sessiondaten sind kein Schlüssel.

`OverlayCanvasFrame` liegt einmal hinter dem Modulinhalt in Editor, App-Vorschau,
Presetminiatur und nativer Renderfläche. Hintergrunddeckkraft verändert nur die
Panelfüllung, `ShowBorder=false` unterdrückt Rahmen und Ornamente. Modulgeometrie,
Canvasbeschnitt und gleichmäßige Skalierung bleiben unverändert. Allgemeine
Modulboxen entfallen; fachliche Balken, Itemzellen und Animationen bleiben Inhalt.

Die Theme-ID kommt aus der vorhandenen globalen Einstellung. `SetOverlayTheme`
speichert unter dem Settings-Lock und rollt einen Fehler zurück, bevor `Changed`
ausgelöst wird. Editor und lokale Beispielansichten beobachten Themeänderungen
auch ohne Live-Consumer. Layouts und Vorlagen erhalten keine neuen Datenfelder.

### Portable Editorstandards und horizontale Bibliothek

`OverlayEditorDefaults` enthält ausschließlich neutrale Layoutdefinitionen für
Open World und Abyss. Der öffentliche `OverlayService` verwendet sie nur als
Fallback bei fehlender Datei. Die bisherigen leeren Modellstandards bleiben
erhalten; ein interner Konstruktor erlaubt einen expliziten Missing-Fallback für
isolierte Store-/CRUD-Tests. Die erste Mutation bewahrt die zuvor geladenen IDs.

Vorlagen werden über `OverlayTemplate.ApplyTo` angewendet. Damit bleiben Position,
Monitor, Aktivierung, Sichtbarkeitsregeln und Quellenkontext des Zielfensters erhalten.
Weder Layoutdefinitionen noch Suche lesen Nutzerdateien oder Livequellen zur Laufzeit.

Die Bibliothek ist ein eigener Bereich vor dem zweispaltigen Canvas/Inspector.
Lokale Suche und Kategorien filtern den unveränderten Modulkatalog; der Canvas
bleibt montiert. Der native Document-Click-Handler nimmt nur die im selben Editor
liegenden Bibliothekscontrols vom Abwählen aus. Der dokumentierte CSS-Override
und diese exakte JS-Anpassung sind bei der Original-Kit-Provenienz separat erfasst.

## Funktionsübergreifende Diagnose (2026-11-09)

`DiagnosticFeatureCatalog` definiert stabile Feature-IDs, Gruppen und notwendige
Quellen. `IDiagnosticContributor` liefert je Funktion Status, Prüfzeit, festen Hinweis
und explizit erlaubte skalare Fakten. `DiagnosticServiceRegistry` beobachtet die
reguläre Erzeugung der Host-Singletons; Diagnose-Lookups liefern ausschließlich bereits
erzeugte Instanzen. Kein Diagnosezugriff darf einen Dienst konstruieren oder starten.

`DiagnosticsService` führt ausgewählte Beiträge samt Quellen mit begrenzter Wartezeit
aus. Hängende Beiträge bleiben einzeln in Bearbeitung und erzeugen bei Wiederholung
keine zusätzlichen Wartethreads. Die leichte Hintergrundprüfung überschreibt keine
manuell gewählte Diagnose. Pakete enthalten `diagnose.txt`, ein versioniertes Manifest,
begrenzte Featureprojektionen und gegebenenfalls die vorhandene Kartenvorschau.
Es gibt keinen pauschalen Export vollständiger Einstellungen oder sämtlicher Logs.

`overlay-merchant` bindet die Händlerquelle und Capture ein. Ein atomarer, erlaubnisgefilterter
`MerchantOverlayHealth`-Snapshot liefert eigene Analyse-/Bildfrische, Worker- und
Präsentationszustand, aktive Sperren, Zeilen-/Badgezahlen und begrenzte Zustandswechsel.
Die generische Host-Fensterbeobachtung ersetzt diese Funktionsbelege nicht: ein aktuell
beobachtetes verborgenes Händlerfenster ist allein kein Healthy-Nachweis. Die Projektion
startet keine Quelle und enthält weder Angebots-/Petkennungen noch Ausnahmetexte.

`NetworkDiagnosticRecorder` ist ein separat gestarteter, zeit-/größenbegrenzter Tap der
laufenden produktiven Quelle. Exact-Ownership, Capturegeneration und Gerätezuordnung
werden vor der Aufnahme geprüft; unbekannte eingehende Verbindungen erst nach dem
bestehenden nativen Ownership-Nachweis. Ausgehende Pakete werden ausschließlich
aufgezeichnet, nicht an Decoder geliefert. Dateischreiben läuft über eine begrenzte
Queue unabhängig vom Tracker. PCAP-Dateien bewahren Paketbytes und Originalzeiten;
Manifest und Status nennen IPv4/TCP, aktiven Quelladapter, Grenzen und Unterbrechungen.

## Startkontext und automatische Exchange-Zuordnung (2026-11-10)

`UnifiedLootOverlayState.WaitingForWorldContext` stammt aus derselben atomaren
Veröffentlichung wie Quelle und aktive Session. Es unterscheidet fehlende aktuelle
Own-Kartenbestätigung von einer Pause, fehlenden Drops, einem nicht unterstützten
Gebiet und ausgeschalteten Quellen. Die Präsentationsprojektion gibt das Flag nur
bei gültiger Beobachtungszeit weiter; ausschließlich die Liveansicht des Editors
zeigt den Hinweis. `loot-sessions`/`loot-pve`/`loot-lifeskill` diagnostizieren ihn
anonym ohne zusätzlichen Bindungs-, Erfassungs- oder Aufnahmeaufruf.

`ExchangeMarketAutoSelectionService` liest die vorhandene eigene Netzwerkbindung
und `ExplorationService.TryGetBoundCharacter`, unabhängig vom manuell betrachteten
Profil. Nur ein exakter eindeutiger Join der eigenen Serverkennung mit dem
expliziten `game_server_key` des gemeinsamen Exchange-Serverkatalogs liefert
Region, Fraktion und dessen separate Anbieter-ID `server_id`. Beide Kennungen
werden atomar in der automatischen Profilzuordnung gespeichert. Die Vorabprüfung
neuer Beutebelege und der Liveprojektion vergleicht ausschließlich den nativen
Schlüssel mit der eigenen Serverkennung; API-Anfragen und Preisbelege verwenden
weiterhin die Anbieter-ID. Kein Offset-, Namens- oder anderer Ersatzabgleich.
Alte Katalogcaches ohne native Schlüssel behalten Preise und Lookupfristen, aber
ihr Serverlistenzeitpunkt wird ungültig, damit der gemeinsame Client sie einmal
regulär erneuert. Mehrdeutige oder fehlende Schlüssel bleiben unbekannt.
Onlinepreise werden beim Start automatisch aktiviert; der UI-Schalter entfällt.
Cache, Abrufabstände und Rückstau bleiben gemeinsam genutzt.
Keine Charakter-/Profilkennung und kein Charaktername gelangen in HTTP-Anfragen.

`AutoDetectServer` ist ein additives nullable Profilfeld: Ohne expliziten Wert
werden alle Profile automatisch zugeordnet; nur explizit false bleibt manuell.
Atomare Profilupdates bewahren aktuelle Gebühren
und explizite manuelle Änderungen. Gebührenspeicherung ändert ausschließlich die
Gebührenfelder. Vor dem Commit wird die aktuelle Own-Autorität erneut geprüft;
der Netzwerkadapter publiziert dafür einen lockfreien unveränderlichen Own-Snapshot.
Damit entsteht keine Settings-/Netzwerk-Sperrumkehr. Fehler werden sichtbar,
eine ausdrückliche Wiederholung hebt die Fehlersperre auf. Quellenwechsel,
interne Sperren und Dispose widerrufen ausstehende Ergebnisse. Der bestehende
`loot-market`-Diagnosebeitrag ergänzt ausschließlich anonyme Zuordnungsflags.

Die Zuordnung wird vor künftigen Beutebewertungen im bestehenden Profilmodell
gespeichert. Liveprojektion und eingefrorene Bewertungen verwenden weiterhin ihre
bisherigen Dienste; alte Preisbelege, Gebührenbasen, Beutefakten und Sessionzeiten
werden nicht umgeschrieben. Fehlende oder nicht eindeutige Server bleiben unbekannt.

### Live-Marktwert aus 24h-Beobachtungsmitteln (10.11.2026)

Für Market Kina und Market Kina/h haben nach der Nutzerpräzisierung vom 10.11.2026
aktuelle gültige niedrigste Stückpreise Vorrang. Sie werden gemeinsam in 100er-Batches
abgerufen und mit der eingefrorenen Lootbewertung sowie weiteren Verbrauchern geteilt.
Nur Items ohne aktuellen Preis verwenden das arithmetische Mittel der niedrigsten
Stückpreise im vorangehenden 24h-Fenster. Einzelne letzte historische Angebote sind
kein Ersatz. Bereits verfügbare aktuelle Preise lösen keinen Verlaufsabruf aus.

Die zusätzliche Average-Capability des gemeinsamen Clients fragt die dokumentierte
Prices-Schnittstelle mit auf die UTC-Stunde abgerundetem Start und bucket=raw ab.
Ein exaktes from/to erzeugt beim Anbieter HTTP 308; die kanonische URL vermeidet
Weiterleitungen, während automatische Redirects weiterhin gesperrt bleiben.
Antworten können im Anbietercache liegen: Das tatsächliche, höchstens zehn Minuten
alte Bereichsende begrenzt das exakte 24h-Fenster, bei kleiner Zukunftsabweichung
höchstens die eigene Anfragezeit. Der Anbieter muss den gesamten Tag abdecken.
Ausschließlich echte Rohzeitpunkte innerhalb dieses [Start,Ende)-Fensters liefern
Summe und Anzahl; der zusätzliche Stundenrand und spätere Punkte bleiben draußen.
Einzelbeobachtungen, Server/Item, Rohpunktform, Zeitrahmen und JSON werden validiert;
unbrauchbare Daten bleiben unbekannt. Es gibt keine Median-, Verkaufsvolumen- oder
Ausreißerheuristik.

AveragePrices und AverageLookups sind optionale, anonyme Erweiterungen derselben
Schema-1-Cachedatei. Positive und negative Mittelwerttreffer unterdrücken erneute
Anfragen für zehn Minuten ab erfolgreichem Abruf, auch nach Neustart und bei einem
bereits älteren Anbietercachefenster. Die Datenfrische bleibt davon getrennt sichtbar.
Fehlende aktuelle Preise werden während eines frischen Mittelwert-Lookups durch
Editorinstanzen nicht erneut gepollt; die einminütige Frist für eingefrorene Belege
bleibt unabhängig erhalten. Ältere noch zulässige Cachewerte sind als veraltet markiert.
Gemeinsamer Fetch-Gate,
HTTP-Mindestabstand und Fehlerbackoff bleiben unverändert. Einzelne Ergebnisse
werden inkrementell veröffentlicht und über Fenster/Module geteilt. Abbruch,
Quellenwechsel und unqualifizierte Sessions können keine alten Ergebnisse als
neue Livewerte übernehmen. Layouts und Vorlagen enthalten weiterhin keine Preise.

Die Marktdiagnose zeigt Basis, Anzahl aktueller Preiszeilen und 24h-Ersatzwerte,
anonyme Abdeckung, Beobachtungsanzahl, Ladezustand und Zeitraum. Durchschnittsfenster
und Samples stammen ausschließlich aus tatsächlich verwendeten Ersatzwerten.
Vollständige veraltete Preise ergeben Warning statt Healthy.
