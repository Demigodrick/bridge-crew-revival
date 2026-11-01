# Sprachbefehle auf Deutsch

Die Sprachbefehle des Captains gibt es auch auf Deutsch. Die Erkennung läuft wie die englische offline mit Vosk, nur mit dem deutschen Modell.

## Einrichten mit dem Installer

Der Installer fragt nach der Sprache der Sprachbefehle. Mit `G` lädt er das deutsche Modell (ca. 45 MB, geprüft) und stellt alles um. Ohne Nachfrage geht es mit `-VoiceLanguage German` (Windows) bzw. `--voice-language=german` (Linux), bei `irm | iex` vorher mit `$env:STBC_VOICE_LANGUAGE = "German"`.

## Von Hand einrichten

1. Den Installer wie gewohnt laufen lassen (er richtet Vosk und die englische Erkennung ein).
2. [vosk-model-small-de-0.15](https://alphacephei.com/vosk/models/vosk-model-small-de-0.15.zip) (ca. 45 MB) herunterladen und entpacken. Der Ordner mit `am`, `conf`, `graph` und den anderen Unterordnern wird zu `BepInEx/vosk/model-de` im Spieleordner. Die großen Modelle (`vosk-model-de-0.21` und andere) sind nur geeignet, wenn ihr Ordner `graph` die Dateien `HCLr.fst` und `Gr.fst` enthält, weil die Wortliste sonst nicht greift. Sie brauchen außerdem viel Speicher und laden beim ersten Tastendruck lange.
3. In `BepInEx/config/stbc.revival.cfg` unter `[Voice]` eintragen:

```
VoiceLanguage = German
```

4. Spiel neu starten. Im Log steht dann `Voice commands: offline recognition ready (German)`.

Mit `VoiceLanguage = English` (Standard) ändert sich nichts.

## Bedienung

Wie bisher als Captain V (oder den Sprachknopf) halten, den Befehl sagen und loslassen. Es werden Schlüsselwörter erkannt, die Reihenfolge ist egal, die Beispiele sind keine festen Sätze.

| Bereich | Beispiele |
| --- | --- |
| Geschwindigkeit | "Volle Kraft voraus", "Halbe Impulskraft", "Viertel Impuls", "Stopp", "Rückwärts" |
| Warp und Impuls | "Warp vorbereiten", "Impuls vorbereiten", "Kurs setzen"; "Energie", "Machen Sie es so", "Warp aktivieren"; "Warp neun", "Warpgeschwindigkeit"; "Warp verlassen" |
| Ziel ansteuern | "Kurs auf Theta Novae setzen", "Warp zum nächsten System" (mit "Energie" oder "Machen Sie es so" am Ende gleich los) |
| Annähern, Abstand | "Dem Ziel annähern", "Abfangen", "Kurs auf das Ziel"; "Rückzug", "Flucht", "Ausweichmanöver" |
| Feuer | "Phaser feuern", "Torpedos abfeuern", "Feuer frei" (beides); "Feuer einstellen", "Zielerfassung aufheben" |
| Torpedos | "Torpedos scharf machen", "Torpedos entschärfen" |
| Schilde | "Schilde hoch", "Schilde runter", "Schilde modulieren" |
| Ziel | "Triebwerke anvisieren" (auch Schilde, Waffen), "Phaser auf das Ziel richten", "Ziel analysieren", "Ziel scannen" |
| Stören | "Triebwerke stören" (auch Schilde, Waffen, Kommunikation) |
| Transporter | "Überlebende an Bord beamen", "Überlebende retten", "Hochbeamen" |
| Tarnung | "Getarnte Schiffe jagen", "Signatur reduzieren" |
| Energie | "Energie auf die Schilde", "Energie auf Triebwerke umleiten", "Energie ausgleichen" |
| Reparatur | "Schilde reparieren", "Warpkern reparieren", "Reparieren" (gleichmäßig) |
| Optimieren | "Offensive optimieren", "Schilde optimieren", "Optimieren" |
| Alarm | "Roter Alarm", "Gelber Alarm", "Alarm aufheben" |
| Brücke | "Auf den Schirm", "Kanal öffnen", "Statusbericht" |

Die Systemnamen der Warpkarte sind englisch. Wenn das Modell einen Namen nicht kennt, wird nach der Schreibweise zugeordnet.

## Deutsche Texte im Spiel

Ist die Menüsprache des Spiels Deutsch, erscheinen auch die Texte des Mods auf Deutsch: Revival-Missionen (Titel, Beschreibungen, Aufträge, Meldungen), Revival-Einstellungen, Crew-Passwort, Raumliste, Hinweise und Popups. Bei jeder anderen Sprache bleibt alles Englisch. Es gibt dafür nichts einzustellen; jeder Spieler sieht die Texte in seiner eigenen Sprache, auch wenn der Host eine andere hat.

## Andere Formulierungen

Es gibt zu jedem Befehl mehrere Möglichkeiten, auch aus der deutschen Synchronisation. Einige Beispiele:

- **Schilde:** "Schilde hochfahren / aufbauen / aktivieren", "Schutzschilde runter / senken / abschalten".
- **Alarm:** "Roter Alarm", "Alarmstufe Rot", "Alle Mann auf Gefechtsstation", "Gelber Alarm"; "Alarm aufheben / beenden", "Entwarnung".
- **Feuer:** "Feuer frei", "Phaser feuern / abfeuern", "Feuer eröffnen"; "Feuer einstellen", "Nicht schießen", "Feuerpause", "Waffenruhe".
- **Torpedos:** "Torpedos laden / scharf machen / klarmachen", "Waffen entsichern"; "Torpedos entschärfen / sichern".
- **Geschwindigkeit:** "Beschleunigen", "Langsamer", "Bremsen", "Alle Maschinen stopp", "Bring uns hier raus".
- **Warp:** "Gehe auf Warp", "Warp einleiten", "Warp zwo", "Machen Sie es so", "Tun Sie es", "Energie!", "Aus dem Warp fallen".
- **Transporter:** "Überlebende bergen / retten", "Hochbeamen", "Transporter, Energie".
- **Brücke:** "Grußfrequenzen öffnen", "Kanal öffnen", "Lagebericht".

Zusammengesetzte Wörter wie "Warpkern", "Photonentorpedos" oder "Statusbericht" kennt das kleine Modell nicht als Ganzes; es erkennt sie als "Warp Kern", "Photonen Torpedos" und "Status Bericht", und der Mod versteht das.

Alltagswörter wie "los", "weg", "voll", "halt", "näher" oder "zurück" lösen allein nichts aus, damit nicht versehentlich ein Befehl gegeben wird. Allgemeine Verben wie "verteilen" oder "verbessern" zählen nur zusammen mit "Energie" oder einem Bordsystem ("Schilde verbessern").

## Mehrere Befehle hintereinander

Mach zwischen zwei Befehlen eine kurze Pause (etwa eine halbe Sekunde), dann werden beide ausgeführt: "Schilde hoch ... Torpedos scharf" oder "Schilde hoch ... Roter Alarm". Ohne Pause wird wie bisher nur ein Befehl erkannt. Eine Pause mitten in einem Befehl ("Schilde ... runter") schadet nicht; es zählt nur, wenn danach wirklich ein zweiter Befehl folgt. Es sind bis zu vier Befehle pro Tastendruck möglich.
