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
    /// word ParseDe looks for is in one of the groups below, and the word list is made from the groups. Words
    /// the model doesn't know are left out of the grammar by Vosk, so several spellings and inflections are
    /// listed for the ones it may have under another form.
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
        private static readonly string[] DeWarp = G("warp", "warpantrieb", "warpgeschwindigkeit", "warpfaktor");
        private static readonly string[] DeCore = G("warpkern", "kern", "reaktor");
        private static readonly string[] DeImpulse = G("impuls", "impulsantrieb", "impulsgeschwindigkeit", "impulskraft");
        private static readonly string[] DeNumbers = G("eins", "ein", "zwei", "zwo", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn");
        private static readonly HashSet<string> DeNumberSet = new HashSet<string>(DeNumbers);
        private static readonly string[] DeNumberNames = { "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun" };
        private static readonly string[] DePrepare = G("vorbereiten", "vorbereitung", "bereit", "bereitmachen", "klarmachen", "klar", "plotten", "plotte",
            "kurs", "setzen", "setze", "legen", "lege", "festlegen", "programmieren");
        private static readonly string[] DeEngage = G("engagieren", "engage", "los", "energie", "ausführen", "starten", "starte", "springen", "springe", "sprung");
        private static readonly string[] DeEngageMild = G("aktivieren", "aktiviere", "einschalten", "zünden");
        private static readonly string[] DeMake = G("machen", "mach", "es", "tun");
        private static readonly string[] DeWarpGo = G("faktor", "maximal", "maximum", "höchste", "höchstgeschwindigkeit", "gehen", "geh", "gehe", "springen",
            "springe", "sprung", "warpgeschwindigkeit", "warpfaktor", "geschwindigkeit");
        private static readonly string[] DeLeaveWarp = G("verlassen", "raus", "heraus", "abbrechen", "beenden", "aus", "fallen", "austreten");
        private static readonly string[] DeUs = G("uns");
        private static readonly string[] DeOut = G("raus", "hinaus", "heraus");
        private static readonly string[] DeCourseWords = G("kurs", "setze", "setzen", "plotte", "plotten", "legen", "lege", "festlegen", "ziel", "zielort", "zielsystem",
            "richtung", "fliege", "fliegen", "flieg", "warp", "gehe", "gehen", "nimm", "nehmen", "steuern", "steuere", "ansteuern", "bringe", "bringen",
            "fahre", "fahren", "reise", "reisen");
        private static readonly string[] DeCourseFor = G("für", "nach", "zu", "zum", "zur", "auf", "richtung");
        private static readonly string[] DeNearest = G("nächste", "nächsten", "nächstes", "nächster", "nächstgelegene", "nächstgelegenen", "nächstgelegenes");
        private static readonly string[] DeApproach = G("annähern", "nähern", "nähere", "abfangen", "abfangkurs", "heran", "näher", "herangehen", "ansteuern",
            "ansteuere", "angreifen", "verfolgen", "verfolge", "verfolgung", "anfliegen");
        private static readonly string[] DeAway = G("weg", "entfernen", "entferne", "fliehen", "flieh", "flucht", "rückzug", "zurückziehen", "meiden", "vermeiden",
            "abdrehen", "abhauen");
        private static readonly string[] DeMove = G("bewegen", "bewege", "fliegen", "flieg", "fliege", "gehen", "geh", "gehe", "steuern", "steuere", "richtung",
            "kurs", "zufliegen");
        private static readonly string[] DeStop = G("stopp", "stop", "anhalten", "halt", "stoppen", "stillstand");
        private static readonly string[] DeReverse = G("rückwärts", "zurück", "rückwärtsgang", "umkehren");
        private static readonly string[] DeHalf = G("halbe", "halben", "halber", "halb", "halbes");
        private static readonly string[] DeQuarter = G("viertel", "viertelkraft");
        private static readonly string[] DeFull = G("voll", "volle", "vollen", "voller", "volles", "maximal", "maximum", "höchstgeschwindigkeit", "voraus", "vorwärts",
            "geschwindigkeit", "gas");

        // tactical
        private static readonly string[] DeShields = G("schilde", "schild", "schilden", "schutzschilde", "schutzschild", "deflektoren", "deflektor");
        private static readonly string[] DeEngines = G("triebwerke", "triebwerk", "triebwerken", "antrieb", "maschinen", "motoren");
        private static readonly string[] DeWeapons = G("waffen", "waffe", "waffensysteme", "phaser", "phasern", "phasers", "phaserbank", "phaserbänke");
        private static readonly string[] DeTorpedoes = G("torpedos", "torpedo", "torpedoes", "photonentorpedos", "photonentorpedo", "photonen");
        private static readonly string[] DeFire = G("feuer", "feuern", "feuere", "feure", "abfeuern", "schießen", "schieß", "schieße", "beschießen", "schuss", "abschießen");
        private static readonly string[] DeCease = G("einstellen", "stopp", "stop", "halt", "halten", "aufhören", "beenden", "abbrechen", "aufheben", "löschen", "ende");
        private static readonly string[] DeAtWill = G("frei", "belieben");
        private static readonly string[] DeTarget = G("ziel", "ziele", "zielen", "zielerfassung", "anvisieren", "visieren", "anpeilen", "peilen");
        private static readonly string[] DeLock = G("aufschalten", "aufschaltung", "ausrichten", "richten", "erfassen", "erfasse", "fixieren", "sperren");
        private static readonly string[] DeEnemy = G("feind", "feindes", "feindliche", "feindlichen", "feindliches", "gegner", "gegners");
        private static readonly string[] DeDown = G("runter", "herunter", "senken", "senke", "absenken", "deaktivieren", "deaktiviere", "abschalten", "ausschalten",
            "aus", "fallen", "unten", "herunterfahren", "runterfahren", "herab");
        private static readonly string[] DeModulate = G("modulieren", "moduliere", "modulation");
        private static readonly string[] DeDisarm = G("entschärfen", "entschärfe", "unscharf", "sichern", "sichere", "abrüsten", "entladen", "entlade", "deaktivieren",
            "deaktiviere");
        private static readonly string[] DeArm = G("scharf", "scharfmachen", "schärfen", "laden", "lade", "bewaffnen", "bewaffne", "aufladen", "aktivieren", "aktiviere",
            "bereitmachen", "bereit");
        private static readonly string[] DeScan = G("scannen", "scanne", "scan", "sondieren", "sondiere", "sonde", "abtasten");
        private static readonly string[] DeAnalyse = G("analysieren", "analysiere", "analyse", "untersuchen", "untersuche", "untersuchung", "identifizieren", "identifiziere");
        private static readonly string[] DeDisrupt = G("stören", "störe", "lahmlegen", "lahm", "hacken", "hacke", "sabotieren", "sabotiere", "manipulieren");
        private static readonly string[] DeComms = G("kommunikation", "kommunikationssysteme", "kommunikationssystem", "funk", "comms");
        private static readonly string[] DeBeam = G("beamen", "beam", "beame", "hochbeamen", "raufbeamen", "herüberbeamen", "hochholen", "transportieren", "transportiere",
            "transport", "evakuieren", "evakuiere", "evakuierung");
        private static readonly string[] DePeople = G("überlebende", "überlebenden", "besatzung", "crew", "leute", "menschen", "lebensformen", "personen", "mannschaft");
        private static readonly string[] DeRescue = G("retten", "rette", "bringen", "bringe", "holen", "hole", "hoch", "rauf", "heraufholen", "aufnehmen", "bord");
        private static readonly string[] DeBring = G("bringen", "bringe", "holen", "hole", "aufnehmen");
        private static readonly string[] DeAboard = G("bord");
        private static readonly string[] DeCloaked = G("getarnt", "getarnte", "getarnten", "getarntes", "getarnter");
        private static readonly string[] DeCloak = G("tarnung", "tarnen", "tarne", "tarnvorrichtung");
        private static readonly string[] DeHunt = G("jagen", "jage", "jagd", "aufspüren", "aufspüre", "orten", "orte");
        private static readonly string[] DeSearch = G("suchen", "suche", "finden", "entdecken");
        private static readonly string[] DeStealth = G("signatur", "signaturen", "leise", "lautlos", "schleichfahrt", "stealth", "unauffällig");

        // engineering
        private static readonly string[] DeRepair = G("reparieren", "repariere", "reparatur", "reparaturen", "beheben", "behebe", "instandsetzen", "fixen", "flicken");
        private static readonly string[] DeOptimize = G("optimieren", "optimiere", "optimierung", "verbessern", "maximieren");
        private static readonly string[] DeOffense = G("offensive", "offensiv", "angriff");
        private static readonly string[] DeDefense = G("defensive", "defensiv", "verteidigung");
        private static readonly string[] DeSensors = G("sensoren", "sensor", "scanner");
        private static readonly string[] DeScience = G("wissenschaft", "labor", "wissenschaftslabor");
        private static readonly string[] DeTransporter = G("transporter", "transporterraum", "rettung");
        private static readonly string[] DeComputer = G("computer", "computerkern", "rechner");
        private static readonly string[] DeSickbay = G("krankenstation", "krankenrevier", "medizin", "medizinische", "lazarett");
        private static readonly string[] DePowerVerbs = G("umleiten", "umlenken", "leiten", "lenken", "verteilen", "umverteilen", "umschalten", "leite", "lenke", "verteile");
        private static readonly string[] DeEnergy = G("energie", "leistung", "strom");
        private static readonly string[] DeBalance = G("ausgleichen", "ausgeglichen", "balancieren", "gleichmäßig", "ausbalancieren", "balance", "gleichverteilen");

        // Star Trek phrases
        private static readonly string[] DeAlert = G("alarm", "alarmstufe", "gefechtsstation", "gefechtsstationen", "kampfstation", "kampfstationen", "gefechtsbereitschaft",
            "kampfbereitschaft");
        private static readonly string[] DeCancel = G("aufheben", "aufgehoben", "beenden", "abbrechen", "aus", "abschalten", "ende", "normal", "abblasen", "deaktivieren");
        private static readonly string[] DeAllClear = G("entwarnung");
        private static readonly string[] DeYellow = G("gelb", "gelber", "gelben");
        private static readonly string[] DeRed = G("rot", "roter", "roten");
        private static readonly string[] DeEvasive = G("ausweichmanöver", "ausweichmanövers", "ausweichen", "ausweichkurs", "ausweichkurse");
        private static readonly string[] DeHail = G("rufen", "rufe", "anruf", "anrufen", "grußfrequenz", "grußfrequenzen", "grüßen", "kanal", "frequenz", "frequenzen");
        private static readonly string[] DeAnswer = G("antworten", "antwort", "beantworten");
        private static readonly string[] DeScreen = G("schirm", "bildschirm");
        private static readonly string[] DeStatus = G("status", "statusbericht", "bericht", "schadensbericht", "lagebericht", "zustandsbericht", "meldung", "melden");

        private static readonly string[] DeFillers = G("der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "bitte", "sie", "uns", "unser", "unsere",
            "unseren", "auf", "an", "zu", "zum", "zur", "nach", "für", "von", "vom", "mit", "im", "in", "aus", "und", "ab", "bei", "alle", "alles", "jetzt", "sofort",
            "schiff", "system", "sektor", "ihre", "ihren", "ihr", "unsere", "mal", "so", "sei", "es");

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
            if (german) return DeEngage.Any(w.Contains) || (w.Contains("so") && DeMake.Any(w.Contains));
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
            bool shields = any(DeShields), engines = any(DeEngines);
            bool weapons = any(DeWeapons), torpedoes = any(DeTorpedoes);
            bool warp = any(DeWarp) && !any(DeCore), impulse = any(DeImpulse);
            bool fire = any(DeFire), target = any(DeTarget);

            if (any(DeCease) && (fire || target || weapons || torpedoes))
                return fire || weapons || torpedoes ? "CeaseFire" : "CeaseTargeting";

            // Engineering: before "energie" can mean "engage".
            if (any(DeRepair))
                return engines ? "RepairEngines" : shields ? "RepairShields" : torpedoes ? "RepairTorpedoes" : weapons ? "RepairPhasers"
                    : any(DeComputer) ? "RepairComputerCore" : any(DeCore) || any(DeWarp) ? "RepairWarpcore" : any(DeSensors) ? "RepairSensors"
                    : any(DeScience) ? "RepairScience" : any(DeTransporter) ? "RepairTransporter"
                    : any(DeSickbay) ? "RepairSickbay" : "RepairBalanced";
            if (any(DeOptimize))
                return any(DeOffense) || weapons || torpedoes ? "OptimizeWeapons"
                    : any(DeDefense) || shields ? "OptimizeShields" : engines ? "OptimizeEngines"
                    : any(DeTransporter) ? "OptimizeTransporter"
                    : any(DeSensors) ? "OptimizeScanners" : any(DeComputer) ? "OptimzeComputerCore" : "OptimizeBalanced";
            if (any(DePowerVerbs) || any(DeBalance) || (any(DeEnergy) && (engines || shields || weapons)))
                return engines ? "PowerToEngines" : shields ? "PowerToShields" : weapons ? "PowerToPhasers" : "PowerBalanced";

            // Helm travel: prepare / engage warp or impulse.
            if (any(DePrepare) && (warp || impulse))
                return Remember(warp ? "PrepareWarp" : "PrepareImpulse");
            if (any(DeEngage) || (w.Contains("so") && any(DeMake)) || (any(DeEngageMild) && (warp || impulse)))
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
            if (any(DeCloaked) || any(DeHunt) || (any(DeCloak) && any(DeSearch))) return "CloakHunt";
            if (any(DeStealth) || any(DeCloak)) return "ReduceSignature";

            // Tactical.
            if (shields)
                return any(DeModulate) ? "ModulateShields" : any(DeDown) ? "LowerShields" : "RaiseShields";
            if (torpedoes || any(new[] { "scharf", "scharfmachen", "schärfen" }))
            {
                if (any(DeDisarm)) return "DisarmTorpedoes";
                if (any(DeArm)) return "ArmTorpedoes";
            }
            if (fire) return torpedoes ? "FireTorpedoes" : "FirePhasers";

            // Helm movement and speed.
            if (any(DeApproach)) return "MoveTo";
            if (any(DeAway)) return "MoveAwayFrom";
            if (target && any(DeMove)) return "MoveTo";
            if (any(DeStop)) return "SetSpeedStop";
            if (any(DeReverse)) return "SetSpeedReverse";
            if (any(DeHalf)) return "SetSpeedHalf";
            if (any(DeQuarter)) return "SetSpeedQuarter";
            if (any(DeFull) || impulse) return "SetSpeedFull";
            return target ? "AnalyseTarget" : null;
        }

        private static string LoreDe(HashSet<string> w, Func<string[], bool> any)
        {
            bool alert = any(DeAlert);
            if ((alert && any(DeCancel)) || any(DeAllClear)) return "RedAlertOff,LowerShields,DisarmTorpedoes";
            if (alert && any(DeYellow)) return "RaiseShields";
            if (alert) return "RedAlertOn,RaiseShields,ArmTorpedoes";
            if (w.Contains("feuer") && any(DeAtWill)) return "FirePhasers,FireTorpedoes";
            if (any(DeEvasive)) return "MoveAwayFrom";
            if (any(DeHail) || any(DeScreen) || (any(DeAnswer) && !any(DeFire) && !any(DeShields)))
                return "AnswerHail";
            if (any(DeStatus) && !any(DeScan) && !any(DeAnalyse))
                return "StatusReport";
            bool warp = any(DeWarp) && !any(DeCore);
            if (warp && any(DeLeaveWarp)) return "SetSpeedStop";
            if (warp && !any(DePrepare) && (any(DeWarpGo) || any(DeNumbers))) return "EngageWarp";
            if (any(DeUs) && any(DeOut) && !warp) return "SetSpeedFull";
            if (any(DeLock) && (any(DeWeapons) || any(DeTorpedoes) || any(DeTarget))) return "EngageTarget";
            return null;
        }
    }
}
