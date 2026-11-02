namespace StbcRevival
{
    internal static partial class Translation
    {
        private static void LoadGerman()
        {
            Missions();
            Patrol();
            Evacuation();
            Settings();
            Crews();
            Notices();
        }

        // ---- Revival missions -------------------------------------------------------------------------

        private static void Missions()
        {
            Add("LAST STAND", "LETZTES GEFECHT");
            Add("FLEET BATTLE", "FLOTTENSCHLACHT");
            Add("PATROL", "PATROUILLE");
            Add("STELLAR EVACUATION", "STERNEN-EVAKUIERUNG");
            Add("KOBAYASHI MARU", "KOBAYASHI MARU");
            Add("FREE ROAM", "FREIES ERKUNDEN");
            Add("PVP TEST", "PVP-TEST");
            Add("REVIVAL MISSION", "REVIVAL-MISSION");
            Add("REVIVAL MISSIONS", "REVIVAL-MISSIONEN");

            Add("Hold your ground against endless waves of Klingons, pirates and Romulans, each bigger than the last, "
                + "with a flagship every fifth wave. How many waves can your crew survive?",
                "Haltet die Stellung gegen endlose Wellen von Klingonen, Piraten und Romulanern, jede größer als die letzte, "
                + "mit einem Flaggschiff in jeder fünften Welle. Wie viele Wellen übersteht eure Crew?");
            Add("Lead a Starfleet battle group of {0} ships against a Klingon or Romulan fleet just as big. "
                + "Destroy every enemy ship to win; keep your own ship in one piece.",
                "Führt einen Kampfverband der Sternenflotte aus {0} Schiffen gegen eine ebenso große klingonische oder romulanische Flotte. "
                + "Zerstört jedes feindliche Schiff, um zu gewinnen, und haltet euer eigenes Schiff heil.");
            Add("A patrol that never ends: Starfleet sends you from system to system to hunt raiders, defend "
                + "freighters and rescue survivors. Repair and restock at space stations between assignments.",
                "Eine Patrouille ohne Ende: Die Sternenflotte schickt euch von System zu System, um Plünderer zu jagen, Frachter zu verteidigen "
                + "und Überlebende zu retten. Zwischen den Aufträgen wird an Raumstationen repariert und aufgefüllt.");
            Add("Perseph's star is about to erupt. Ferry the people off the station a group at a time to a waiting "
                + "transport, then warp out before the blast. {0} minutes: how many can you save?",
                "Persephs Stern steht kurz vor dem Ausbruch. Bringt die Menschen gruppenweise von der Station zu einem wartenden "
                + "Transporter und geht vor der Explosion in den Warp. {0} Minuten: Wie viele könnt ihr retten?");
            Add("The no-win scenario: the freighter Kobayashi Maru is stranded in the Neutral Zone, and Klingon "
                + "warships are coming. Save her crew if you can. The game's own test from the campaign, in any ship.",
                "Das Szenario ohne Gewinnchance: Der Frachter Kobayashi Maru treibt manövrierunfähig in der Neutralen Zone, und klingonische "
                + "Kriegsschiffe nähern sich. Rettet die Besatzung, wenn ihr könnt. Der Test aus der Kampagne, in jedem Schiff.");
            Add("No hostiles, no objectives: fly, scan and warp around a sector at your own pace "
                + "(the developers' own exploration mission, left in the game). Sector: {0}.",
                "Keine Feinde, keine Ziele: Fliegt, scannt und warpt in eurem Tempo durch einen Sektor "
                + "(die Erkundungsmission der Entwickler, die im Spiel geblieben ist). Sektor: {0}.");
            Add("Testing player vs player: a second player ship, PvP B, appears ahead of yours as an enemy. "
                + "Destroy it to win. The crew is split between the ships in joining order (the host on yours).",
                "Test von Spieler gegen Spieler: Ein zweites Spielerschiff, PvP B, erscheint als Feind vor eurem. "
                + "Zerstört es, um zu gewinnen. Die Crew verteilt sich in der Reihenfolge des Beitritts auf die Schiffe (der Host auf eures).");

            // Last Stand
            Add("Last Stand: hold out against the waves", "Letztes Gefecht: den Wellen standhalten");
            Add("Wave {0}: destroy all {1} hostiles", "Welle {0}: Alle {1} Feinde vernichten");
            Add("Wave {0} cleared. Repair and reroute power: wave {1} is coming", "Welle {0} besiegt. Reparieren und Energie umleiten: Welle {1} naht");
            Add("Waves survived: {0}", "Überlebte Wellen: {0}");

            // Fleet Battle
            Add("Fleet battle: destroy the Klingon fleet", "Flottenschlacht: Die klingonische Flotte vernichten");
            Add("Fleet battle: destroy the Romulan fleet", "Flottenschlacht: Die romulanische Flotte vernichten");
            Add("Klingon reinforcements arriving", "Klingonische Verstärkung trifft ein");
            Add("Romulan reinforcements arriving", "Romulanische Verstärkung trifft ein");
            Add("Starfleet reinforcements arriving", "Verstärkung der Sternenflotte trifft ein");
            Add("Victory: {0} of {1} allied ships survived", "Sieg: {0} von {1} verbündeten Schiffen haben überlebt");
            Add("Enemy ships left: {0}. Allied ships left: {1}", "Verbleibende Feindschiffe: {0}. Verbleibende verbündete Schiffe: {1}");
            Add("Klingon {0}", "Klingonisches Schiff {0}");
            Add("Romulan {0}", "Romulanisches Schiff {0}");

            // PvP test
            Add("PvP test", "PvP-Test");
            Add("Destroy PvP B, the other player ship", "PvP B zerstören, das andere Spielerschiff");
            Add("PvP test: the second ship didn't spawn", "PvP-Test: Das zweite Schiff ist nicht erschienen");
        }

        // ---- Patrol -----------------------------------------------------------------------------------

        private static void Patrol()
        {
            Add("Patrol: carry out Starfleet's assignments", "Patrouille: Aufträge der Sternenflotte ausführen");
            Add("New assignment in the {0} system: {1}. Warp there to accept", "Neuer Auftrag im System {0}: {1}. Zum Annehmen dorthin warpen");
            Add("raiders are attacking shipping", "Plünderer greifen die Handelsschifffahrt an");
            Add("a disabled freighter is under attack", "ein manövrierunfähiger Frachter wird angegriffen");
            Add("a freighter is adrift with survivors aboard", "ein Frachter treibt mit Überlebenden an Bord");
            Add("Until then, repairs and torpedoes at any space station (within 5 km)", "Bis dahin: Reparaturen und Torpedos an jeder Raumstation (im Umkreis von 5 km)");
            Add("Repaired and restocked at {0}", "Repariert und aufgefüllt bei {0}");
            Add("Assignments completed: {0}", "Abgeschlossene Aufträge: {0}");
            Add("Assignment complete. Well done. Assignments completed: {0}", "Auftrag erfüllt. Gut gemacht. Abgeschlossene Aufträge: {0}");
            Add("Assignment abandoned. Assignments completed: {0}", "Auftrag aufgegeben. Abgeschlossene Aufträge: {0}");
            Add("Assignment failed. Assignments completed: {0}", "Auftrag gescheitert. Abgeschlossene Aufträge: {0}");
            Add("Beam the survivors aboard", "Die Überlebenden an Bord beamen");
            Add("Ambush! Destroy the Klingon ships", "Hinterhalt! Die klingonischen Schiffe vernichten");
            Add("the station", "der Station");

            // The lines say where, when the site is known: "...near X (set an impulse course)".
            Near("Destroy the raiders{0}", "Die Plünderer vernichten{0}");
            Near("Protect the disabled freighter{0} from raiders", "Den manövrierunfähigen Frachter{0} vor Plünderern schützen");
            Near("Scan the disabled freighter{0} for life signs, then beam the survivors aboard",
                "Den manövrierunfähigen Frachter{0} auf Lebenszeichen scannen, dann die Überlebenden an Bord beamen");

            Add("Raider {0}", "Plünderer {0}");
            Add("Freighter {0}", "Frachter {0}");
        }

        // One text in its three forms: no site, a marked one, a named one.
        private static void Near(string english, string german)
        {
            Add(english.Replace("{0}", ""), german.Replace("{0}", ""));
            Add(english.Replace("{0}", " (marked; set an impulse course)"), german.Replace("{0}", " (markiert; Impulskurs setzen)"));
            Add(english.Replace("{0}", " near {0} (set an impulse course)"), german.Replace("{0}", " bei {0} (Impulskurs setzen)"));
        }

        // ---- Stellar Evacuation -----------------------------------------------------------------------

        private static void Evacuation()
        {
            Add("Evacuate the station before the star erupts", "Die Station evakuieren, bevor der Stern ausbricht");
            Add("Warp out before the star erupts", "In den Warp gehen, bevor der Stern ausbricht");
            Add("Warp to the {0} system: its star is about to erupt", "Zum System {0} warpen: Sein Stern steht kurz vor dem Ausbruch");
            Add("Stellar eruption imminent: evacuate the station", "Sternenausbruch steht bevor: Station evakuieren");
            Add("At {0}, scan each escape pod and beam the people aboard, then impulse to Quarantine, where a transport takes them",
                "Bei {0} jede Rettungskapsel scannen und die Personen an Bord beamen, dann mit Impuls zur Quarantäne fliegen, wo ein Transporter sie übernimmt");
            Add("At {0}, scan each escape pod and beam the people aboard, then impulse to the transport to drop them off",
                "Bei {0} jede Rettungskapsel scannen und die Personen an Bord beamen, dann mit Impuls zum Transporter fliegen und sie dort absetzen");
            Add("The escape pod was destroyed: {0} people lost", "Die Rettungskapsel wurde zerstört: {0} Personen verloren");
            Add("Raiders inbound", "Plünderer im Anflug");
            Add("A raider is closing in!", "Ein Plünderer nähert sich!");
            Add("{0} raiders closing in!", "{0} Plünderer nähern sich!");
            Add("The USS Raleigh was destroyed. Another transport will meet you at Quarantine",
                "Die USS Raleigh wurde zerstört. Ein weiterer Transporter trifft euch bei der Quarantäne");
            Add("{0} people waiting in an escape pod at {1}: scan it and beam them aboard",
                "{0} Personen warten in einer Rettungskapsel bei {1}: Scannen und an Bord beamen");
            Add("Everyone's off the station. Warp out!", "Alle sind von der Station herunter. In den Warp!");
            Add("{0} people transferred to the USS Raleigh", "{0} Personen auf die USS Raleigh gebracht");
            Add("Stellar core unstable", "Sternenkern instabil");
            Add("Stellar eruption: warp out now", "Sternenausbruch: Jetzt in den Warp!");
            Add("Lost in the eruption. {0} of {1} people were saved", "Im Ausbruch verloren. {0} von {1} Personen wurden gerettet");
            Add("Station evacuated: all {0} people saved", "Station evakuiert: Alle {0} Personen gerettet");
            Add("{0} of {1} people saved", "{0} von {1} Personen gerettet");
            Add("Saved: {0} of {1}", "Gerettet: {0} von {1}");
            Add("Saved: {0} of {1}, {2} lost", "Gerettet: {0} von {1}, {2} verloren");
            Add("Saved: {0} of {1} ({2} aboard, take them to the transport)", "Gerettet: {0} von {1} ({2} an Bord, zum Transporter bringen)");
            Add("Saved: {0} of {1}, {2} lost ({3} aboard, take them to the transport)", "Gerettet: {0} von {1}, {2} verloren ({3} an Bord, zum Transporter bringen)");
            Add("the colony ship", "dem Kolonieschiff");
            Add("Colony ship {0}", "Kolonieschiff {0}");
            Add("Escape pod {0}", "Rettungskapsel {0}");
        }

        // ---- Revival settings, escape menu ------------------------------------------------------------

        private static void Settings()
        {
            Add("Revival settings", "Revival-Einstellungen");
            Add("REVIVAL SETTINGS", "REVIVAL-EINSTELLUNGEN");
            Add("Saved straight away. Arrows pick a setting.", "Wird sofort gespeichert. Mit den Pfeilen wählst du eine Einstellung.");
            Add("Back to the main menu", "Zurück zum Hauptmenü");
            Add("Back", "Zurück");
            Add("Turn on", "Einschalten");
            Add("Turn off", "Ausschalten");
            Add("Type code", "Code eingeben");
            Add("Change", "Ändern");
            Add("Unblock all", "Alle entsperren");
            Add("Join a crew by its room code", "Einer Crew per Raumcode beitreten");
            Add("Room or share code", "Raum- oder Share-Code");
            Add("Needs SteamVR: use F7 on the desktop", "Benötigt SteamVR: F7 am Desktop nutzen");
            Add("Type the code on the SteamVR keyboard", "Code auf der SteamVR-Tastatur eingeben");
            Add("SteamVR keyboard unavailable", "SteamVR-Tastatur nicht verfügbar");

            foreach (var setting in new[]
            {
                new[] { "Skip intro videos", "Intro-Videos überspringen", "restart" },
                new[] { "Skip mic check", "Mikrofon-Check überspringen", "restart" },
                new[] { "Voice commands", "Sprachbefehle", "restart" },
                new[] { "Voice orders from any station", "Sprachbefehle von jeder Station", "" },
                new[] { "Romulan face", "Romulaner-Gesicht", "" },
                new[] { "Chime when someone joins", "Signalton, wenn jemand beitritt", "" },
                new[] { "Public crews in VR", "Öffentliche Crews in VR", "" },
                new[] { "Hide full crews", "Volle Crews ausblenden", "" },
                new[] { "Hide crews on a mission", "Crews in einer Mission ausblenden", "" },
                new[] { "Show my crew in Steam", "Meine Crew in Steam anzeigen", "" },
                new[] { "Check for updates", "Nach Updates suchen", "" },
            })
            {
                string later = setting[2] == "restart" ? " (next launch)" : "", spaeter = setting[2] == "restart" ? " (nächster Start)" : "";
                Add($"{setting[0]}: ON{later}", $"{setting[1]}: AN{spaeter}");
                Add($"{setting[0]}: OFF{later}", $"{setting[1]}: AUS{spaeter}");
            }
            // Creator species rows (SpeciesCreatorOptions): the label follows the case of the row it copies.
            Add("ROMULAN", "ROMULANER");
            Add("KLINGON", "KLINGONE");
            Add("Romulan", "Romulaner");
            Add("Klingon", "Klingone");
            Add("Klingon head: OFF", "Klingonenkopf: AUS");
            Add("Klingon head: CAPTAIN", "Klingonenkopf: KAPITÄN");
            Add("Klingon head: FLAGSHIP CAPTAIN", "Klingonenkopf: FLAGGSCHIFF-KAPITÄN");
            Add("Crews listed: TNG only", "Angezeigte Crews: nur TNG");
            Add("Crews listed: no TNG", "Angezeigte Crews: ohne TNG");
            Add("Crews listed: any", "Angezeigte Crews: alle");
            Add("Blocked players: none (block someone after kicking them)", "Gesperrte Spieler: keine (sperre jemanden, nachdem du ihn rausgeworfen hast)");
            Add("Blocked players: {0}", "Gesperrte Spieler: {0}");

            Add("PUBLIC CREWS", "ÖFFENTLICHE CREWS");
            Add("Steam multiplayer mod", "Steam-Multiplayer-Mod");
            Add("{0} Revival player online", "{0} Revival-Spieler online");
            Add("{0} Revival players online", "{0} Revival-Spieler online");
        }

        // ---- Crews: rooms, passwords, the VR panels, the F7 window ------------------------------------

        private static void Crews()
        {
            // Crew password
            Add("The crew has a password. Remove it?", "Die Crew hat ein Passwort. Entfernen?");
            Add("Set a new password?", "Ein neues Passwort festlegen?");
            Add("Lock the crew with a password?", "Die Crew mit einem Passwort sperren?");
            Add("Lock the crew with a password? Only players you give it to can join.",
                "Die Crew mit einem Passwort sperren? Nur Spieler, denen du es gibst, können beitreten.");
            Add("Crew password", "Crew-Passwort");
            Add("CREW PASSWORD", "CREW-PASSWORT");
            Add("The crew's password is removed: anyone can join.", "Das Passwort der Crew wurde entfernt: Jeder kann beitreten.");
            Add("Crew password set: \"{0}\". Give it to the players you want aboard.", "Crew-Passwort gesetzt: \"{0}\". Gib es den Spielern, die an Bord sollen.");
            Add("This crew has a password. Enter it?", "Diese Crew hat ein Passwort. Jetzt eingeben?");
            Add("Wrong password. Try again?", "Falsches Passwort. Noch einmal versuchen?");
            Add("This crew's host has blocked you.", "Der Host dieser Crew hat dich gesperrt.");
            Add("Too many wrong passwords. Wait a minute, then try again.", "Zu viele falsche Passwörter. Warte eine Minute und versuche es dann erneut.");
            Add("This crew is on a different Bridge Crew Revival version than yours ({0}). Everyone in a crew needs the same version: update from the website.",
                "Diese Crew nutzt eine andere Version von Bridge Crew Revival als du ({0}). Alle in einer Crew brauchen dieselbe Version: Aktualisiere über die Website.");
            Add("This crew runs {0}; you have {1}. Everyone in a crew needs the same version: update from the Bridge Crew Revival website, or ask the host to.",
                "Diese Crew nutzt {0}; du hast {1}. Alle in einer Crew brauchen dieselbe Version: Aktualisiere über die Bridge-Crew-Revival-Website oder bitte den Host darum.");
            Add("an older version of Bridge Crew Revival", "eine ältere Version von Bridge Crew Revival");
            Add("an older version", "eine ältere Version");
            Add("OK", "OK");
            Add("Cancel", "Abbrechen");
            Add("Type it in the window on the desktop ({0}).", "Gib es im Fenster auf dem Desktop ein ({0}).");

            // Joining
            Add("Joining {0}…", "Trete {0} bei…");
            Add("Enter a room code first.", "Gib zuerst einen Raumcode ein.");
            Add("Room {0} not found: it isn't public, and none of your Steam friends are in it. For a private crew, ask for its share code (like {1}-EU-1Q2W3E4, in the F7 window): it works without being Steam friends.",
                "Raum {0} nicht gefunden: Er ist nicht öffentlich und keiner deiner Steam-Freunde ist darin. Frage bei einer privaten Crew nach dem Share-Code (wie {1}-EU-1Q2W3E4, im F7-Fenster): Er funktioniert auch ohne Steam-Freundschaft.");
            Add("Crew {0} is on Bridge Crew Revival {1}; you have {2}. Everyone in a crew needs the same version: update from the website.",
                "Crew {0} nutzt Bridge Crew Revival {1}; du hast {2}. Alle in einer Crew brauchen dieselbe Version: Aktualisiere über die Website.");
            Add("Couldn't reach that crew's lobby on Steam. Try again, or join with their room code.",
                "Die Lobby dieser Crew war über Steam nicht erreichbar. Versuche es erneut oder tritt mit ihrem Raumcode bei.");

            // Host hand-off
            Add("The host dropped. You're the new host: regrouping the crew in the briefing room…", "Der Host ist ausgefallen. Du bist der neue Host: Die Crew sammelt sich im Besprechungsraum…");
            Add("The host dropped. Regrouping with {0} as the new host…", "Der Host ist ausgefallen. Neugruppierung mit {0} als neuem Host…");
            Add("{0} didn't take over. You're the new host: regrouping the crew…", "Der Wechsel zu {0} ist gescheitert. Du bist der neue Host: Die Crew sammelt sich…");
            Add("{0} didn't take over. Regrouping with {1}…", "Der Wechsel zu {0} ist gescheitert. Neugruppierung mit {1}…");
            Add("Crew regrouped in the briefing room.", "Die Crew hat sich im Besprechungsraum gesammelt.");
            Add("Couldn't regroup the crew after the host dropped. Host or join a crew as usual.", "Die Crew konnte nach dem Ausfall des Hosts nicht neu gesammelt werden. Hoste oder tritt wie gewohnt einer Crew bei.");
            Add("another player", "einem anderen Spieler");

            // Friends, invites
            Add("In your crew", "In deiner Crew");
            Add("Playing Star Trek Bridge Crew", "Spielt Star Trek Bridge Crew");
            Add("This friend isn't in a crew you can join right now.", "Dieser Freund ist gerade in keiner Crew, der du beitreten kannst.");
            Add("Your crew isn't open to join right now.", "Deine Crew ist gerade nicht offen für Beitritte.");
            Add("Invite sent to {0}.", "Einladung an {0} gesendet.");
            Add("Steam couldn't send the invite.", "Steam konnte die Einladung nicht senden.");

            // VR panels
            Add("YOUR CREW {0}", "DEINE CREW {0}");
            Add("Public: anyone can join", "Öffentlich: Jeder kann beitreten");
            Add("Private: invite or code only", "Privat: nur per Einladung oder Code");
            Add(" - tap to invite", " - antippen zum Einladen");
            Add(" - full", " - voll");
            Add("any", "beliebig");
            Add("No public crews right now", "Gerade keine öffentlichen Crews");
            Add("Start a Quick match to host another crew", "Starte ein Schnellspiel, um eine weitere Crew zu hosten");
            Add("Start a Quick match to host one", "Starte ein Schnellspiel, um eine zu hosten");
            Add("{0}. Start a Quick match to host one", "{0}. Starte ein Schnellspiel, um eine zu hosten");
            Add("No public crews right now.\nStart a Quick match to host one.", "Gerade keine öffentlichen Crews.\nStarte ein Schnellspiel, um eine zu hosten.");
            Add("No public crews right now.\nStart a Quick match to host one.\n{0}.", "Gerade keine öffentlichen Crews.\nStarte ein Schnellspiel, um eine zu hosten.\n{0}.");
            Add("This is your crew, {0}.\n{1}", "Das ist deine Crew, {0}.\n{1}");
            Add("Your crew {0}: {1}\n\nInvite Steam friends? This opens Steam's invite window (in VR, open the SteamVR dashboard to see it).",
                "Deine Crew {0}: {1}\n\nSteam-Freunde einladen? Das öffnet das Einladungsfenster von Steam (in VR siehst du es im SteamVR-Dashboard).");
            Add("Crew {0} is full or closed.", "Crew {0} ist voll oder geschlossen.");
            Add("Join crew {0}?\n{1}", "Crew {0} beitreten?\n{1}");

            // Room list
            Add("Public", "Öffentlich");
            Add("Solo", "Solo");
            Add("Private", "Privat");
            Add("Tutorial", "Tutorial");
            Add("Unknown", "Unbekannt");
            Add(" · password", " · Passwort");
            Add("Shuttle", "Shuttle");
            Add("Briefing room", "Besprechungsraum");
            Add("Intro", "Intro");
            Add("In mission", "In Mission");
            Add("Hosted by {0} · public", "Gehostet von {0} · öffentlich");
            Add("Hosted by {0} · private", "Gehostet von {0} · privat");
            Add("You're hosting · PRIVATE: only Steam invites, Join Game and your share code get in",
                "Du hostest · PRIVAT: Nur Steam-Einladungen, \"Spiel beitreten\" und dein Share-Code lassen Spieler herein");
            Add("You're hosting · PUBLIC: listed on Steam, Quick match players can join",
                "Du hostest · ÖFFENTLICH: Bei Steam gelistet, Schnellspiel-Spieler können beitreten");
            Add("You're hosting · PUBLIC: publishing to Steam…", "Du hostest · ÖFFENTLICH: Wird bei Steam veröffentlicht…");

            // The F7 window
            Add("Bridge Crew Revival — Rooms", "Bridge Crew Revival — Räume");
            Add("You are in room  {0}   share code: {1}", "Du bist in Raum  {0}   Share-Code: {1}");
            Add("You are in room  {0}   (share this code with your crew)", "Du bist in Raum  {0}   (teile diesen Code mit deiner Crew)");
            Add("Connected. Pick a room or enter a code.", "Verbunden. Wähle einen Raum oder gib einen Code ein.");
            Add("Not connected to the server yet.", "Noch nicht mit dem Server verbunden.");
            Add("Copy share code", "Share-Code kopieren");
            Add("Copy my room code", "Meinen Raumcode kopieren");
            Add("Invite Steam friends", "Steam-Freunde einladen");
            Add("Share code copied: anyone with it can join, Steam friends or not.", "Share-Code kopiert: Jeder damit kann beitreten, ob Steam-Freund oder nicht.");
            Add("Room code copied.", "Raumcode kopiert.");
            Add("In the Steam overlay, right-click a friend and choose \"Invite to Game\".", "Klicke im Steam-Overlay mit rechts auf einen Freund und wähle \"Zum Spiel einladen\".");
            Add("Room code:", "Raumcode:");
            Add("Paste", "Einfügen");
            Add("Join", "Beitreten");
            Add("Full", "Voll");
            Add("Public rooms (Steam, worldwide):", "Öffentliche Räume (Steam, weltweit):");
            Add("Public rooms (Steam, worldwide):   · {0}", "Öffentliche Räume (Steam, weltweit):   · {0}");
            Add("Public rooms on this server:", "Öffentliche Räume auf diesem Server:");
            Add(" Hide full", " Volle ausblenden");
            Add(" Hide in mission", " In Mission ausblenden");
            Add("Content: TNG only", "Inhalt: nur TNG");
            Add("Content: no TNG", "Inhalt: ohne TNG");
            Add("Content: any", "Inhalt: alle");
            Add("  None right now. Start a Quick match to host one.", "  Gerade keine. Starte ein Schnellspiel, um einen zu hosten.");
            Add("  (room list appears once you're connected, on the shuttle)", "  (die Raumliste erscheint, sobald du verbunden bist, im Shuttle)");
            Add("{0} (you)", "{0} (du)");
            Add("Press {0} to close. Public rooms are Steam lobbies, worldwide.", "Drücke {0} zum Schließen. Öffentliche Räume sind Steam-Lobbys, weltweit.");
            Add("Press {0} to close. Also at http://{1}:8080/ on a self-hosted server.", "Drücke {0} zum Schließen. Auf einem selbst gehosteten Server auch unter http://{1}:8080/.");
            Add("Room list unavailable: {0}", "Raumliste nicht verfügbar: {0}");
        }

        // ---- Notices ----------------------------------------------------------------------------------

        private static void Notices()
        {
            Add("Not in VR: type the room code in this window (F7).", "Nicht in VR: Gib den Raumcode in diesem Fenster ein (F7).");
            Add("The headset keyboard needs SteamVR. Type the room code in the F7 window on the desktop, or join friends from the shuttle's friends console.",
                "Die Headset-Tastatur braucht SteamVR. Gib den Raumcode im F7-Fenster am Desktop ein oder tritt Freunden über die Freundekonsole im Shuttle bei.");
            Add("The SteamVR keyboard didn't open. Type the room code in the F7 window on the desktop instead.",
                "Die SteamVR-Tastatur hat sich nicht geöffnet. Gib den Raumcode stattdessen im F7-Fenster am Desktop ein.");

            Add("Bridge Crew Revival {0} is available. You have {1}.\n\nOpen the website to update? This closes the game; run the installer again from there.",
                "Bridge Crew Revival {0} ist verfügbar. Du hast {1}.\n\nDie Website zum Aktualisieren öffnen? Das schließt das Spiel; führe dort den Installer erneut aus.");
            Add("Bridge Crew Revival has been updated to {0}. It starts the next time you launch the game.\n\nClose the game now?",
                "Bridge Crew Revival wurde auf {0} aktualisiert. Es startet beim nächsten Spielstart.\n\nDas Spiel jetzt schließen?");

            Add("Your Ubisoft account has no Bridge Crew save to import.", "Dein Ubisoft-Konto hat keinen Bridge-Crew-Spielstand zum Importieren.");
            Add("Imported {0} saved setting(s) from Ubisoft.", "{0} gespeicherte Einstellung(en) von Ubisoft importiert.");
            Add("The Ubisoft save arrived but couldn't be written to disk; see the log.", "Der Ubisoft-Spielstand ist angekommen, konnte aber nicht gespeichert werden; siehe Log.");
            Add("No answer from Ubisoft within 5 minutes. Is Ubisoft Connect running and signed in?", "Keine Antwort von Ubisoft innerhalb von 5 Minuten. Läuft Ubisoft Connect und bist du angemeldet?");
            Add("{0} The game will close in a few seconds.", "{0} Das Spiel schließt sich in wenigen Sekunden.");
        }
    }
}
