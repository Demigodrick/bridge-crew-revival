using System;
using System.Collections.Generic;
using System.Linq;

namespace StbcRevival
{
    internal enum VoiceLanguageChoice { English, German }

    /// <summary>
    /// [Voice] VoiceLanguage = German: the captain's orders in German. Same recording and Vosk recognition as the
    /// English ones, with the German model (BepInEx/vosk/model-de), a German word list and the German key words
    /// in ParseDe. The commands that come out are the same crew commands.
    /// The recogniser only ever returns words from the list, so the key words are matched as whole words: every
    /// word ParseDe looks for is in one of the groups below, and the word list is made from the groups. Vosk
    /// leaves out words its model doesn't know, and the small German model knows no compounds of its own ("warpkern",
    /// "photonentorpedos", "gefechtsstation"): they come out as their parts ("warp kern"), so the groups hold
    /// parts. Every word here is in vosk-model-small-de-0.15's word list.
    /// </summary>
    internal static partial class VoiceCommands
    {
        private static bool german => Plugin.VoiceLanguage.Value == VoiceLanguageChoice.German;

        private static readonly List<string> DeAll = new List<string>();

        private static string[] G(params string[] words)
        {
            DeAll.AddRange(words);
            return words;
        }

        // helm
        private static readonly string[] DeWarp = G("warp");
        private static readonly string[] DeCore = G("kern", "gondeln");
        private static readonly string[] DeImpulse = G("impuls", "impulse");
        private static readonly string[] DeNumbers = G("eins", "ein", "zwei", "zwo", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn");
        private static readonly HashSet<string> DeNumberSet = new HashSet<string>(DeNumbers);
        private static readonly string[] DeNumberNames = { "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun" };
        private static readonly string[] DePrepare = G("vorbereiten", "vorbereitung", "bereit", "klarmachen", "klar", "plotten", "kurs", "setzen", "setze", "legen",
            "lege", "festlegen", "programmieren");
        private static readonly string[] DeEngage = G("engagieren", "ausführen");
        private static readonly string[] DeEngageMild = G("aktivieren", "aktiviere", "einschalten", "einleiten", "starten", "starte", "springen", "sprung"); // only with warp or impulse
        private static readonly string[] DeMake = G("machen", "mach");
        private static readonly string[] DeWarpGo = G("faktor", "maximal", "maximale", "maximum", "höchste", "höchstgeschwindigkeit", "gehen", "geh", "gehe", "geschwindigkeit", "eintreten");
        private static readonly string[] DeLeaveWarp = G("verlassen", "raus", "heraus", "abbrechen", "beenden", "aus", "fallen");
        private static readonly string[] DeUs = G("uns");
        private static readonly string[] DeOut = G("raus", "hinaus", "heraus");
        private static readonly string[] DeCourseWords = G("kurs", "setze", "setzen", "plotten", "legen", "lege", "festlegen", "ziel", "richtung", "fliege", "fliegen",
            "flieg", "warp", "gehe", "gehen", "nimm", "nehmen", "steuern", "ansteuern", "bringe", "bringen", "bring", "fahre", "fahren", "fahr", "reise", "reisen",
            "navigieren");
        private static readonly string[] DeCourseFor = G("für", "nach", "zu", "zum", "zur", "auf", "richtung");
        private static readonly string[] DeNearest = G("nächste", "nächsten", "nächstes", "nächster");
        private static readonly string[] DeApproach = G("annähern", "nähern", "nähere", "abfangen", "herangehen", "ansteuern", "angreifen",
            "verfolgen", "anfliegen");
        private static readonly string[] DeAway = G("entfernen", "fliehen", "flieh", "flüchten", "flucht", "rückzug", "zurückziehen", "meiden", "vermeiden", "abdrehen");
        private static readonly string[] DeMove = G("bewegen", "fliegen", "flieg", "fliege", "gehen", "geh", "gehe", "steuern", "richtung", "kurs");
        private static readonly string[] DeStop = G("stopp", "stop", "anhalten", "stoppen", "stillstand", "bremsen", "abbremsen");
        private static readonly string[] DeReverse = G("rückwärts", "umkehren");
        private static readonly string[] DeHalf = G("halbe", "halben", "halber", "halbes", "langsamer", "verlangsamen");
        private static readonly string[] DeQuarter = G("viertel");
        private static readonly string[] DeFull = G("volle", "vollen", "voller", "maximum", "höchstgeschwindigkeit", "voraus", "vorwärts", "beschleunigen", "schneller");

        // tactical
        private static readonly string[] DeShields = G("schilde", "schild", "schilden", "schutzschilde", "schutzschild");
        private static readonly string[] DeEngines = G("triebwerke", "triebwerk", "triebwerken", "maschinen", "motoren");
        private static readonly string[] DeDrive = G("antrieb");
        private static readonly string[] DeWeapons = G("waffen", "waffe", "waffensysteme", "phaser", "geschütze", "geschütz");
        private static readonly string[] DeTorpedoes = G("torpedos", "torpedo", "photonen");
        private static readonly string[] DeFire = G("feuer", "feuern", "feuere", "abfeuern", "schießen", "schieß", "schieße", "beschießen", "schuss", "abschießen");
        private static readonly string[] DeCease = G("einstellen", "stopp", "stop", "halt", "halten", "aufhören", "beenden", "abbrechen", "aufheben", "löschen", "ende");
        private static readonly string[] DeCeaseFire = G("feuerpause", "waffenruhe", "waffenstillstand");
        private static readonly string[] DeNot = G("nicht", "kein", "keine");
        private static readonly string[] DeAtWill = G("frei", "belieben");
        private static readonly string[] DeTarget = G("ziel", "ziele", "zielen", "erfassung", "anvisieren", "visieren", "peilen");
        private static readonly string[] DeLock = G("schalten", "schalte", "ausrichten", "richten", "erfassen", "erfasse", "fixieren", "sperren");
        private static readonly string[] DeEnemy = G("feind", "feindes", "feindliche", "feindlichen", "feindliches", "gegner", "gegners");
        private static readonly string[] DeDown = G("runter", "herunter", "senken", "senke", "absenken", "deaktivieren", "deaktiviere", "abschalten", "ausschalten",
            "aus", "fallen", "unten", "herunterfahren", "herab", "abbauen", "einfahren", "herablassen", "herunterlassen", "fallenlassen");
        private static readonly string[] DeModulate = G("modulieren", "modulation");
        private static readonly string[] DeDisarm = G("entschärfen", "entschärfe", "unscharf", "sichern", "abrüsten", "entladen", "entlade", "deaktivieren", "deaktiviere");
        private static readonly string[] DeArm = G("scharf", "schärfen", "laden", "lade", "bewaffnen", "aufladen", "aktivieren", "aktiviere", "klarmachen", "bereit",
            "entsichern", "feuerbereit", "nachladen");
        private static readonly string[] DeScan = G("scannen", "scanne", "scan", "sondieren", "sondiere", "sonde", "abtasten", "absuchen", "durchsuchen");
        private static readonly string[] DeAnalyse = G("analysieren", "analysiere", "analyse", "untersuchen", "untersuche", "untersuchung", "identifizieren", "identifiziere");
        private static readonly string[] DeDisrupt = G("stören", "störe", "lahmlegen", "hacken", "hacke", "sabotieren", "manipulieren");
        private static readonly string[] DeComms = G("kommunikation", "kommunikationssystem", "funk");
        private static readonly string[] DeBeam = G("beamen", "beam", "transportieren", "transportiere", "transport", "evakuieren", "evakuierung");
        private static readonly string[] DePeople = G("überlebende", "überlebenden", "besatzung", "crew", "leute", "menschen", "lebensformen", "personen", "mannschaft");
        private static readonly string[] DeRescue = G("retten", "rette", "bringen", "bringe", "holen", "hole", "hoch", "rauf", "herauf", "aufnehmen", "bord", "bergen",
            "berge", "bergung", "aufsammeln", "einsammeln");
        private static readonly string[] DeBring = G("bringen", "bringe", "holen", "hole", "aufnehmen");
        private static readonly string[] DeAboard = G("bord");
        private static readonly string[] DeCloaked = G("getarnt", "getarnte", "getarnten", "getarntes", "getarnter");
        private static readonly string[] DeCloak = G("tarnung", "tarnen", "tarnvorrichtung");
        private static readonly string[] DeHunt = G("jagen", "jage", "jagd", "aufspüren", "orten");
        private static readonly string[] DeStealth = G("signatur", "signaturen", "leise", "lautlos", "schleichfahrt", "unauffällig");

        // engineering
        private static readonly string[] DeRepair = G("reparieren", "repariere", "reparatur", "reparaturen", "beheben", "behebe", "instandsetzen", "fixen", "flicken",
            "ausbessern");
        private static readonly string[] DeOptimize = G("optimieren", "optimiere", "optimierung");
        private static readonly string[] DeImprove = G("verbessern", "maximieren", "steigern", "verstärken"); // only for a named system
        private static readonly string[] DeOffense = G("offensive", "offensiv", "angriff");
        private static readonly string[] DeDefense = G("defensive", "defensiv", "verteidigung");
        private static readonly string[] DeSensors = G("sensoren", "sensor", "scanner");
        private static readonly string[] DeScience = G("wissenschaft", "labor");
        private static readonly string[] DeTransporter = G("transporter", "rettung");
        private static readonly string[] DeComputer = G("computer", "rechner");
        private static readonly string[] DeSickbay = G("krankenstation", "krankenrevier", "medizin", "medizinische", "lazarett");
        private static readonly string[] DePowerVerbs = G("umleiten", "umlenken", "umverteilen", "verlagern");
        private static readonly string[] DePowerWays = G("leiten", "lenken", "verteilen", "leite", "lenke", "verteile", "verschieben", "ausgleichen", "ausgeglichen",
            "balancieren", "gleichmäßig"); // only with "energie"
        private static readonly string[] DeEnergy = G("energie", "leistung");

        // Star Trek phrases
        private static readonly string[] DeAlert = G("alarm", "alarmstufe", "alarmbereitschaft", "gefechtsbereitschaft", "kampfbereitschaft");
        private static readonly string[] DeBattle = G("gefecht", "gefechts", "kampf");
        private static readonly string[] DeStation = G("station", "stationen");
        private static readonly string[] DeCancel = G("aufheben", "aufgehoben", "beenden", "abbrechen", "aus", "abschalten", "ende", "normal", "normalzustand", "abblasen",
            "deaktivieren");
        private static readonly string[] DeAllClear = G("entwarnung");
        private static readonly string[] DeYellow = G("gelb", "gelber", "gelben");
        private static readonly string[] DeRed = G("rot", "roter", "roten");
        private static readonly string[] DeEvasive = G("ausweichmanöver", "ausweichen");
        private static readonly string[] DeHail = G("anruf", "gruß", "kanal", "frequenzen");
        private static readonly string[] DeAnswer = G("antworten", "beantworten");
        private static readonly string[] DeScreen = G("schirm");
        private static readonly string[] DeStatus = G("status", "bericht", "lagebericht", "statusmeldung");

        // Words that only carry the sentence along: the recogniser has them to fit speech to instead of a key word.
        private static readonly string[] DeCarry = G("der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "bitte", "sie", "uns", "unser", "unsere",
            "unseren", "auf", "an", "zu", "zum", "zur", "nach", "für", "von", "vom", "mit", "im", "in", "aus", "und", "ab", "bei", "alle", "alles", "jetzt", "sofort",
            "schnell", "gleich", "dringend", "schiff", "raumschiff", "system", "sektor", "ihre", "ihren", "ihr", "mal", "doch", "so", "sei", "es", "mir", "mich",
            "hier", "dort", "da", "dann", "auch", "noch", "mann", "posten", "kapitän", "nummer", "raum", "kraft", "muster", "delta", "tun", "weg", "davon", "zurück", "halt", "langsam", "voll",
            "hochfahren", "hochfahre", "aufbauen", "baue", "ausfahren", "gib", "stell", "stelle", "lass", "schiffe", "schiffen",
            "eröffnen", "sicher", "sichere", "hoch", "oben", "unten", "schäden");

        private static string[] deWords;

        // Every word the recogniser listens for.
        private static string[] WordsDe => deWords ?? (deWords = DeAll.Distinct().ToArray());

        // Wording that differs by language, for the code both share (destinations, "engage").
        private static string[] WordsNow => german ? WordsDe : Vocabulary;
        private static string[] CourseWordsNow => german ? DeCourseWords : CourseWords;
        private static string[] CourseForNow => german ? DeCourseFor : new[] { "for" };
        private static string[] CourseToNow => german ? DeCourseFor : new[] { "for", "to" };
        private static string[] NearestNow => german ? DeNearest : new[] { "nearest", "closest" };
        private static string[] NumberNamesNow => german ? DeNumberNames : new[] { "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
        private static HashSet<string> NumbersNow => german ? DeNumberSet : Numbers;

        private static string ParseNow(string text) => german ? ParseDe(text) : Parse(text);

        private static bool WantsEngage(HashSet<string> w)
        {
            if (german) return DeEngage.Any(w.Contains) || w.Contains("energie") || (w.Contains("so") && DeMake.Any(w.Contains)) || (w.Contains("tun") && w.Contains("es"));
            return new[] { "engage", "punch", "jump" }.Any(w.Contains) || (w.Contains("so") && new[] { "make", "it" }.Any(w.Contains));
        }

        // The spoken order as the game's crew command(s), like Parse; null when nothing fits.
        internal static string ParseDe(string text)
        {
            var w = new HashSet<string>((text ?? "").Split(' ').Where(x => x.Length > 0 && x != "[unk]"));
            if (w.Count == 0) return null;
            Func<string[], bool> any = list => list.Any(w.Contains);
            if (Course(w, any) is string course) return course;
            if (LoreDe(w, any) is string lore) return lore;
            bool shields = any(DeShields);
            bool weapons = any(DeWeapons), torpedoes = any(DeTorpedoes);
            bool warp = any(DeWarp) && !any(DeCore), impulse = any(DeImpulse);
            bool engines = any(DeEngines) || (any(DeDrive) && !warp && !impulse); // "warp antrieb" is the warp drive, not the engines
            bool fire = any(DeFire), target = any(DeTarget);

            if (any(DeCeaseFire) || (fire && any(DeNot))) return "CeaseFire";
            if (any(DeCease) && (fire || target || weapons || torpedoes))
                return fire || weapons || torpedoes ? "CeaseFire" : "CeaseTargeting";

            // Engineering: before "energie" can mean "engage".
            if (any(DeRepair))
                return engines ? "RepairEngines" : shields ? "RepairShields" : torpedoes ? "RepairTorpedoes" : weapons ? "RepairPhasers"
                    : any(DeComputer) ? "RepairComputerCore" : any(DeCore) || any(DeWarp) ? "RepairWarpcore" : any(DeSensors) ? "RepairSensors"
                    : any(DeScience) ? "RepairScience" : any(DeTransporter) ? "RepairTransporter"
                    : any(DeSickbay) ? "RepairSickbay" : "RepairBalanced";
            bool named = engines || shields || weapons || torpedoes || any(DeOffense) || any(DeDefense) || any(DeSensors) || any(DeComputer) || any(DeTransporter);
            if (any(DeOptimize) || (any(DeImprove) && named))
                return any(DeOffense) || weapons || torpedoes ? "OptimizeWeapons"
                    : any(DeDefense) || shields ? "OptimizeShields" : engines ? "OptimizeEngines"
                    : any(DeTransporter) ? "OptimizeTransporter"
                    : any(DeSensors) ? "OptimizeScanners" : any(DeComputer) ? "OptimzeComputerCore" : "OptimizeBalanced";
            if (any(DePowerVerbs) || (any(DeEnergy) && (engines || shields || weapons || any(DePowerWays))))
                return engines ? "PowerToEngines" : shields ? "PowerToShields" : weapons ? "PowerToPhasers" : "PowerBalanced";

            // Helm travel: prepare / engage warp or impulse.
            if (any(DePrepare) && (warp || impulse))
                return Remember(warp ? "PrepareWarp" : "PrepareImpulse");
            bool energyOnly = w.Contains("energie") && w.All(x => x == "energie" || DeCarry.Contains(x)); // "Energie!", not "Energie ..." with something else
            if (any(DeEngage) || energyOnly || (w.Contains("so") && any(DeMake)) || (w.Contains("tun") && w.Contains("es")) || (any(DeEngageMild) && (warp || impulse)))
            {
                if (warp) return "EngageWarp";
                if (impulse) return "EngageImpulse";
                if (target || any(DeEnemy)) return "EngageTarget";
                return lastPrepared != null ? "Engage" + lastPrepared : "EngageWarp";
            }
            if (warp) return Remember("PrepareWarp");

            // Science and special orders.
            if (any(DeDisrupt))
                return engines ? "DisruptEngines" : shields ? "DisruptShields" : weapons ? "DisruptWeapons"
                    : any(DeComms) ? "DisruptComms" : null;
            if (any(DeAnalyse)) return "AnalyseTarget";
            if (target && (engines || shields || weapons)) return engines ? "TargetEngines" : shields ? "TargetShields" : "TargetWeapons";
            if (any(DeBeam) || (any(DePeople) && any(DeRescue)) || (any(DeAboard) && any(DeBring)))
                return "TransportFromTarget";
            if (any(DeScan)) return "ScanTarget";
            if (any(DeCloaked) || any(DeHunt) || any(DeCloak)) return "CloakHunt";
            if (any(DeStealth)) return "ReduceSignature";

            // Tactical.
            if (shields)
                return any(DeModulate) ? "ModulateShields" : any(DeDown) ? "LowerShields" : "RaiseShields";
            if (torpedoes || any(new[] { "scharf", "schärfen" }) || (weapons && (any(DeDisarm) || any(DeArm))))
            {
                if (any(DeDisarm)) return "DisarmTorpedoes";
                if (any(DeArm)) return "ArmTorpedoes";
            }
            if (fire) return torpedoes ? "FireTorpedoes" : "FirePhasers";

            // Helm movement and speed.
            if (any(DeApproach)) return "MoveTo";
            if (any(DeAway) || ((w.Contains("weg") || w.Contains("davon")) && any(DeMove))) return "MoveAwayFrom";
            if (target && any(DeMove)) return "MoveTo";
            if (any(DeStop)) return "SetSpeedStop";
            if (any(DeReverse) || (w.Contains("zurück") && any(DeMove))) return "SetSpeedReverse";
            if (any(DeHalf)) return "SetSpeedHalf";
            if (any(DeQuarter)) return "SetSpeedQuarter";
            if (any(DeFull) || impulse) return "SetSpeedFull";
            return target ? "AnalyseTarget" : null;
        }

        private static string LoreDe(HashSet<string> w, Func<string[], bool> any)
        {
            bool alert = any(DeAlert) || (any(DeBattle) && any(DeStation)) || w.Contains("roter") || w.Contains("gelber"); // "roter" alone: "alarm" is often cut off
            if ((alert && any(DeCancel)) || any(DeAllClear)) return "RedAlertOff,LowerShields,DisarmTorpedoes";
            if (alert && any(DeYellow)) return "RaiseShields";
            if (alert) return "RedAlertOn,RaiseShields,ArmTorpedoes";
            if (w.Contains("feuer") && any(DeAtWill)) return "FirePhasers,FireTorpedoes";
            if (any(DeEvasive)) return "MoveAwayFrom";
            if (any(DeHail) || any(DeScreen) || (any(DeAnswer) && !any(DeFire) && !any(DeShields)))
                return "AnswerHail";
            if (any(DeStatus) && !any(DeScan) && !any(DeAnalyse))
                return "StatusReport";
            if (any(DeEnergy) && any(DeTransporter) && !any(DeRepair) && !any(DeOptimize)) return "TransportFromTarget"; // "Transporter, Energie"
            bool warp = any(DeWarp) && !any(DeCore);
            if (warp && any(DeLeaveWarp)) return "SetSpeedStop";
            if (warp && !any(DePrepare) && (any(DeWarpGo) || any(DeNumbers))) return "EngageWarp";
            if (any(DeUs) && any(DeOut) && !warp) return "SetSpeedFull";
            if (any(DeLock) && !w.Contains("scharf") && (any(DeWeapons) || any(DeTorpedoes) || any(DeTarget))) return "EngageTarget"; // "scharf schalten" arms
            return null;
        }
    }
}
