using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Voice] VoiceCommands (prototype): the captain's spoken orders, back without IBM Watson.
    /// The game's voice orders streamed the microphone to Watson's speech-to-text and conversation services
    /// (through Ubisoft's backend), then turned the answer into a crew command (CommandManager.TriggerCommand).
    /// Here the recognition runs offline on this PC (Vosk, listening only for known phrases) and the phrase
    /// maps straight to the command:
    ///   - The game's own push-to-talk (the VoiceCommand button at the captain's console) raises its "voice
    ///     button" event (NKEPKEMCLNH) only while VoiceRecognitionManager.UsingVoiceRecognition is true, so
    ///     that reports true; the Watson side of VoiceRecognitionManager (never set up) is skipped.
    ///   - Button down: record the microphone (Unity, 16 kHz). Button up: recognise on a background thread.
    ///   - Key words pick the command (Parse), as Watson's intents did; nothing usable: the crew's "didn't catch that".
    /// Needs BepInEx/vosk/libvosk.dll (and its three runtime DLLs) and a model folder,
    /// BepInEx/vosk/model (e.g. vosk-model-small-en-us-0.15).
    /// </summary>
    internal static class VoiceCommands
    {
        private const int SampleRate = 16000;
        private const int MaxSeconds = 10;

        // The words listened for (Vosk recognises any sequence of them; anything else comes back as "[unk]"):
        // everything the game's Watson commands covered (VoiceRecognitionManager's intent/entity table).
        private static readonly string[] Vocabulary =
        {
            // helm
            "speed", "full", "half", "quarter", "impulse", "ahead", "stop", "reverse", "maximum", "go", "warp", "prepare",
            "ready", "plot", "course", "set", "engage", "punch", "jump", "make", "it", "so", "approach", "intercept",
            "close", "move", "away", "evade", "retreat", "avoid", "toward", "towards", "head", "fly",
            // tactical
            "fire", "phasers", "phaser", "torpedoes", "torpedo", "photon", "arm", "disarm", "cease", "hold", "target",
            "targeting", "clear", "engines", "engine", "weapons", "shields", "shield", "comms", "communications",
            "disrupt", "hack", "scan", "probe", "analyze", "analyse", "analysis", "examine", "identify", "beam", "transport", "survivors", "crew",
            "aboard", "bring", "get", "evacuate", "people", "lifeforms", "rescue", "save",
            "stealth", "signature", "reduce", "quiet", "silent", "running", "cloak", "cloaked", "hunt",
            // shields
            "up", "down", "raise", "lower", "drop", "on", "off", "modulate",
            // engineering
            "power", "divert", "reroute", "balance", "balanced", "repair", "fix", "optimize", "optimise", "offense",
            "offensive", "defense", "defensive", "rescue", "sensors", "scanners", "core", "warpcore", "computer",
            "sickbay", "medical", "science", "lab", "transporter", "transporters", "everything", "systems",
            // fillers
            "the", "their", "them", "all", "enemy", "ship", "to", "our", "of",
            // Star Trek phrases (see Lore)
            "alert", "red", "yellow", "battle", "stations", "general", "quarters", "stand", "cancel", "secure",
            "evasive", "maneuvers", "manoeuvres", "pattern", "delta", "will", "at", "energize", "factor", "nine",
            "eight", "seven", "six", "five", "four", "three", "two", "one", "hit", "take", "us", "out", "lets",
            "let's", "lock", "away",
        };

        private static string lastPrepared; // "Warp" or "Impulse": what a bare "engage" / "make it so" engages

        // Words → the game's crew command (AI.BHPPPGKNAHK names), by key words as Watson's intents did:
        // "lower the shields", "shields down" and "drop shields" all lower them. Checked in an order that keeps
        // "repair shields", "power to shields" and "target shields" from being plain shield orders. Null when
        // nothing fits.
        internal static string Parse(string text)
        {
            var w = new HashSet<string>((text ?? "").Split(' ').Where(x => x.Length > 0 && x != "[unk]"));
            if (w.Count == 0) return null;
            Func<string[], bool> any = list => list.Any(w.Contains);
            if (Lore(w, any) is string lore) return lore;
            bool shields = any(new[] { "shields", "shield" }), engines = any(new[] { "engines", "engine" });
            bool weapons = any(new[] { "weapons", "phasers", "phaser" }), torpedoes = any(new[] { "torpedoes", "torpedo", "photon" });
            bool warp = any(new[] { "warp" }) && !w.Contains("core"), impulse = w.Contains("impulse");

            if (any(new[] { "cease", "hold" })) return any(new[] { "target", "targeting" }) ? "CeaseTargeting" : "CeaseFire";
            if (w.Contains("clear") && any(new[] { "target", "targeting" })) return "CeaseTargeting";

            // Helm travel: prepare / engage warp or impulse.
            if (any(new[] { "prepare", "ready", "plot", "course", "set" }) && (warp || impulse))
                return Remember(warp ? "PrepareWarp" : "PrepareImpulse");
            if (any(new[] { "engage", "punch", "jump" }) || (w.Contains("so") && any(new[] { "make", "it" }))) // "make it so", even if "make" is lost
            {
                if (warp) return "EngageWarp";
                if (impulse) return "EngageImpulse";
                if (any(new[] { "target", "enemy" })) return "EngageTarget";
                return lastPrepared != null ? "Engage" + lastPrepared : "EngageWarp"; // Picard's "Engage": go to warp
            }
            if (warp && !any(new[] { "repair", "fix", "power", "optimize", "optimise" })) return Remember("PrepareWarp");

            // Engineering.
            if (any(new[] { "repair", "fix" }))
                return engines ? "RepairEngines" : shields ? "RepairShields" : torpedoes ? "RepairTorpedoes" : weapons ? "RepairPhasers"
                    : any(new[] { "warpcore", "core" }) || w.Contains("warp") ? "RepairWarpcore" : any(new[] { "sensors", "scanners" }) ? "RepairSensors"
                    : any(new[] { "science", "lab" }) ? "RepairScience" : any(new[] { "transporter", "transporters" }) ? "RepairTransporter"
                    : w.Contains("computer") ? "RepairComputerCore" : any(new[] { "sickbay", "medical" }) ? "RepairSickbay" : "RepairBalanced";
            if (any(new[] { "power", "divert", "reroute" }))
                return engines ? "PowerToEngines" : shields ? "PowerToShields" : weapons ? "PowerToPhasers" : "PowerBalanced";
            if (any(new[] { "optimize", "optimise" }))
                return any(new[] { "offense", "offensive" }) || weapons || torpedoes ? "OptimizeWeapons"
                    : any(new[] { "defense", "defensive" }) || shields ? "OptimizeShields" : engines ? "OptimizeEngines"
                    : any(new[] { "rescue", "transporter", "transporters" }) ? "OptimizeTransporter"
                    : any(new[] { "sensors", "scanners" }) ? "OptimizeScanners" : w.Contains("computer") ? "OptimzeComputerCore" : "OptimizeBalanced";
            if (any(new[] { "balance", "balanced" })) return "PowerBalanced";

            // Science and special orders.
            if (any(new[] { "disrupt", "hack" }))
                return engines ? "DisruptEngines" : shields ? "DisruptShields" : weapons ? "DisruptWeapons"
                    : any(new[] { "comms", "communications" }) ? "DisruptComms" : null;
            if (any(new[] { "analyze", "analyse", "analysis", "examine", "identify" })) return "AnalyseTarget";
            bool target = any(new[] { "target", "targeting" });
            if (target && (engines || shields || weapons)) return engines ? "TargetEngines" : shields ? "TargetShields" : "TargetWeapons";
            // Beaming: "beam the survivors aboard", "transport them", "evacuate the crew", "bring them aboard"...
            if (any(new[] { "beam", "transport", "evacuate", "energize" }) || (any(new[] { "survivors", "crew", "people", "lifeforms" })
                && any(new[] { "rescue", "bring", "get", "aboard", "up", "save" })) || (w.Contains("aboard") && any(new[] { "bring", "get", "them" })))
                return "TransportFromTarget";
            if (any(new[] { "scan", "probe" })) return "ScanTarget";
            if (any(new[] { "cloak", "cloaked", "hunt" })) return "CloakHunt";
            if (any(new[] { "stealth", "signature", "quiet", "silent" })) return "ReduceSignature";

            // Tactical.
            if (shields)
                return w.Contains("modulate") ? "ModulateShields" : any(new[] { "down", "lower", "drop", "off" }) ? "LowerShields" : "RaiseShields";
            if (w.Contains("disarm")) return "DisarmTorpedoes";
            if (w.Contains("arm")) return "ArmTorpedoes";
            if (w.Contains("fire")) return torpedoes ? "FireTorpedoes" : "FirePhasers";

            // Helm movement and speed.
            if (any(new[] { "approach", "intercept", "close", "toward", "towards" })) return "MoveTo";
            if (any(new[] { "away", "evade", "retreat", "avoid" })) return "MoveAwayFrom";
            if (target && any(new[] { "move", "head", "fly", "go" })) return "MoveTo"; // "move to the target", "head for the target"
            if (w.Contains("stop")) return "SetSpeedStop";
            if (w.Contains("reverse")) return "SetSpeedReverse";
            if (w.Contains("half")) return "SetSpeedHalf";
            if (w.Contains("quarter")) return "SetSpeedQuarter";
            if (any(new[] { "full", "maximum", "ahead", "speed", "go" }) || impulse) return "SetSpeedFull";
            // A bare "target" (or "analyse the target", with "analyse" lost): analyse it. Last, so "approach the
            // target", "scan the target" and the like get their own orders.
            return target ? "AnalyseTarget" : null;
        }

        // Orders from Star Trek that the game's Watson commands never had, as one or more crew commands
        // (comma-separated): checked before the regular orders. Null: not one of these.
        private static string Lore(HashSet<string> w, Func<string[], bool> any)
        {
            bool alert = w.Contains("alert") || (w.Contains("battle") && w.Contains("stations")) || (w.Contains("general") && w.Contains("quarters"));
            if (any(new[] { "cancel", "secure" }) && alert || (w.Contains("stand") && w.Contains("down")))
                return "RedAlertOff,LowerShields,DisarmTorpedoes";               // "Stand down red alert"
            if (alert && w.Contains("yellow")) return "RaiseShields";            // "Yellow alert"
            if (alert) return "RedAlertOn,RaiseShields,ArmTorpedoes";            // "Red alert", "Battle stations"
            if (w.Contains("fire") && w.Contains("will")) return "FirePhasers,FireTorpedoes"; // "Fire at will"
            if (w.Contains("evasive")) return "MoveAwayFrom";                    // "Evasive maneuvers, pattern delta"
            if (w.Contains("energize")) return "TransportFromTarget";            // "Energize"
            if (w.Contains("warp") && w.Contains("out") && any(new[] { "drop", "take" })) return "SetSpeedStop"; // "Drop out of warp"
            if (w.Contains("warp") && (any(new[] { "factor", "maximum", "speed", "go", "nine", "eight", "seven", "six", "five", "four",
                                                    "three", "two", "one" })))
                return "EngageWarp";                                             // "Maximum warp", "Warp factor nine", "Go to warp"
            if (w.Contains("hit") && w.Contains("it")) return lastPrepared != null ? "Engage" + lastPrepared : "EngageWarp"; // "Hit it"
            if (w.Contains("take") && w.Contains("us") && w.Contains("out")) return "SetSpeedFull"; // "Take us out"
            if (w.Contains("lock") && any(new[] { "phasers", "phaser", "torpedoes", "weapons", "target" })) return "EngageTarget"; // "Lock phasers"
            return null;
        }

        private static string Remember(string prepare)
        {
            lastPrepared = prepare.Substring("Prepare".Length);
            return prepare;
        }

        private static bool ready, failed, listening;
        private static IntPtr model;
        private static string micDevice;
        private static AudioClip clip;
        private static string pendingCommand; // set by the recogniser thread, used on the main thread
        private static bool pendingResult;

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.VoiceCommands.Value) return;
            var vrm = typeof(VoiceRecognitionManager);
            var using_ = AccessTools.PropertyGetter(vrm, "UsingVoiceRecognition") ?? AccessTools.Method(vrm, "get_UsingVoiceRecognition");
            var update = AccessTools.Method(vrm, "Update");
            var button = AccessTools.Constructor(AccessTools.TypeByName("NKEPKEMCLNH"), new[] { typeof(bool) });
            if (using_ == null || update == null || button == null)
            {
                Plugin.Log.LogWarning("Voice commands: the game's voice hooks weren't found; voice commands off");
                return;
            }
            harmony.Patch(using_, postfix: new HarmonyMethod(typeof(VoiceCommands), nameof(Using_Postfix)));
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(VoiceCommands), nameof(SkipWatson)));
            harmony.Patch(button, postfix: new HarmonyMethod(typeof(VoiceCommands), nameof(Button_Postfix)));
            // Our own push-to-talk key too: the game's voice button depends on the controller mapping.
            var getAction = AccessTools.Method(AccessTools.TypeByName("SVR.InputManager"), "GetAction");
            voiceAction = Enum.Parse(AccessTools.TypeByName("SVR.LONCAPHEAPA"), "VoiceCommand");
            if (getAction != null) harmony.Patch(getAction, postfix: new HarmonyMethod(typeof(VoiceCommands), nameof(GetAction_Postfix)));
            Plugin.Log.LogInfo("Voice commands: push-to-talk hooked (prototype, offline recognition)");
        }

        private static void Using_Postfix(ref bool __result) => __result = !failed;

        private static object voiceAction;
        private static bool keyHeld; // read once a frame (Update): GetAction is called a lot

        private static void GetAction_Postfix(object __0, ref bool __result)
        {
            if (keyHeld && !__result && Equals(__0, voiceAction)) __result = true;
        }

        private static bool SkipWatson() => false; // Watson's speech-to-text and token handling: never set up

        // The console raised the voice button event: down starts recording, up recognises.
        private static void Button_Postfix(bool __0)
        {
            try
            {
                if (__0) Start();
                else Stop();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice commands: " + e.Message);
            }
        }

        // ---- Recording --------------------------------------------------------------------------------

        private static void Start()
        {
            if (listening || !IsCaptain() || !EnsureRecogniser()) return;
            micDevice = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
            if (micDevice == null)
            {
                Plugin.Log.LogWarning("Voice commands: no microphone");
                return;
            }
            clip = Microphone.Start(micDevice, false, MaxSeconds, SampleRate);
            listening = true;
            SendUiEvent("GEJIEIDGDBO"); // the game's "listening" indicator
            Plugin.Log.LogInfo($"Voice commands: listening ({micDevice})");
        }

        private static void Stop()
        {
            if (!listening) return;
            listening = false;
            int samples = Microphone.GetPosition(micDevice);
            Microphone.End(micDevice);
            SendUiEvent("MJNNGHIKMIO"); // "processing"
            if (clip == null || samples <= SampleRate / 10)
            {
                Finish(null);
                return;
            }
            var data = new float[samples * clip.channels];
            clip.GetData(data, 0);
            var pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                short s = (short)Mathf.Clamp(data[i * clip.channels] * 32767f, -32768f, 32767f);
                pcm[2 * i] = (byte)s;
                pcm[2 * i + 1] = (byte)(s >> 8);
            }
            new Thread(() => Recognise(pcm)) { IsBackground = true, Name = "Voice commands" }.Start();
        }

        // Main thread, every frame: act on a finished recognition.
        internal static void Update()
        {
            keyHeld = Plugin.VoiceCommands.Value && Input.GetKey(Plugin.PushToTalkKey.Value);
            if (!pendingResult) return;
            pendingResult = false;
            Finish(pendingCommand);
        }

        private static void Finish(string command)
        {
            SendUiEvent("DCDOEBCMNHK"); // done
            var manager = AccessTools.TypeByName("AI.CommandManager");
            var commands = AccessTools.TypeByName("AI.BHPPPGKNAHK");
            if (manager == null || commands == null) return;
            if (command == null)
            {
                AccessTools.Method(manager, "PlayInvalidCommandVO")?.Invoke(null, null);
                return;
            }
            // As after a Watson answer: refresh the crew's simple orders, then the command(s).
            var roles = AccessTools.TypeByName("FLCHMJJDKDL");
            var update = AccessTools.Method(manager, "UpdateSimpleOrders");
            var trigger = AccessTools.Method(manager, "TriggerCommand");
            foreach (var one in command.Split(','))
            {
                // Red alert isn't a crew command: it's the captain's own switch, which Watson's "red_alert" intents
                // flipped through VoiceRecognitionManager.IPEGCHGIGBI(on) (the button's ToggleRedAlert, if needed).
                if (one == "RedAlertOn" || one == "RedAlertOff")
                {
                    AccessTools.Method(typeof(VoiceRecognitionManager), "IPEGCHGIGBI")
                        ?.Invoke(VoiceRecognitionManager.IEFBKOMBFGO, new object[] { one == "RedAlertOn" });
                    continue;
                }
                foreach (var role in new[] { "Engineering", "Tactical", "Helm" })
                    update?.Invoke(null, new[] { Enum.Parse(roles, role) });
                // The helm menu has several Move To orders (to the captain's target, the closest contact, a cloaked
                // one); TriggerCommand takes the first, so "approach target" goes to the captain's-target one.
                if (one == "MoveTo" && ApproachTarget(manager)) continue;
                trigger?.Invoke(null, new[] { Enum.Parse(commands, one) });
            }
            Plugin.Log.LogInfo($"Voice commands: {command}");
        }

        private static bool loggedHelmMenu;

        // Runs the helm menu's "Approach Target" (Move To, label 7809|Approach Target) as its button does
        // (CommandManager.TriggerCommand: OnUpdateMenuState with the crew's roles, then OnExecute). False if
        // it isn't in the menu, for TriggerCommand to try.
        private static bool ApproachTarget(Type manager)
        {
            try
            {
                var instance = AccessTools.Field(manager, "IEFBKOMBFGO")?.GetValue(null) ?? AccessTools.Property(manager, "IEFBKOMBFGO")?.GetValue(null, null);
                var menus = instance == null ? null : AccessTools.Field(manager, "ENLHIAIPDMD")?.GetValue(instance);
                var helm = menus?.GetType().GetMethod("GetHelmMenu")?.Invoke(menus, null);
                if (helm == null) return false;
                var items = new List<object>();
                Collect(helm, items, 0);
                var itemType = AccessTools.TypeByName("AI.CHJEFCBPHGN");
                string Label(object item) => $"{AccessTools.Property(itemType, "ENDEAOOGKOL")?.GetValue(item, null)} {AccessTools.Property(itemType, "OKKPPMGDKAP")?.GetValue(item, null)}";
                string Command(object item) => AccessTools.Property(itemType, "ICJLNIICKNH")?.GetValue(item, null)?.ToString();
                if (!loggedHelmMenu)
                {
                    loggedHelmMenu = true;
                    Plugin.Log.LogInfo("Voice commands: helm menu: " + string.Join("; ", items.Select(i => $"{Command(i)} '{Label(i).Trim()}' ({i.GetType().Name})").ToArray()));
                }
                var moves = items.Where(i => Command(i) == "MoveTo" && !(AccessTools.Property(itemType, "BLFHNLLDICM")?.GetValue(i, null) is true)).ToList();
                var approach = moves.FirstOrDefault(i => Label(i).IndexOf("7809", StringComparison.Ordinal) >= 0 || Label(i).IndexOf("Approach", StringComparison.OrdinalIgnoreCase) >= 0)
                               ?? moves.FirstOrDefault(i => Label(i).IndexOf("My", StringComparison.Ordinal) >= 0);
                if (approach == null) return false;
                var updateState = AccessTools.Method(itemType, "OnUpdateMenuState");
                var roles = AccessTools.Property(manager, "ECNLCPFAEGF")?.GetValue(null, null); // the crew's roles (a List), as TriggerCommand passes them
                var array = roles?.GetType().GetMethod("ToArray")?.Invoke(roles, null)
                            ?? Array.CreateInstance(updateState.GetParameters()[0].ParameterType.GetElementType(), 0);
                updateState.Invoke(approach, new object[] { array, false });
                AccessTools.Method(itemType, "OnExecute").Invoke(approach, null);
                Plugin.Log.LogInfo($"Voice commands: approach target ('{Label(approach).Trim()}')");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice commands: approach target: " + (e.InnerException ?? e).Message);
                return false;
            }
        }

        private static void Collect(object item, List<object> into, int depth)
        {
            if (item == null || depth > 6) return;
            into.Add(item);
            if (AccessTools.Property(item.GetType(), "LLMAEPCPLGP")?.GetValue(item, null) is Array children)
                foreach (var child in children) Collect(child, into, depth + 1);
        }

        // ---- Recognition (Vosk, offline) --------------------------------------------------------------

        private static void Recognise(byte[] pcm)
        {
            string command = null;
            try
            {
                var grammar = "[" + string.Join(",", Vocabulary.Distinct().Select(w => "\"" + w + "\"").ToArray()) + ",\"[unk]\"]";
                var recogniser = Vosk.RecognizerNewGrm(model, SampleRate, Vosk.Str(grammar));
                if (recogniser == IntPtr.Zero) throw new Exception("the recogniser didn't start");
                Vosk.AcceptWaveform(recogniser, pcm, pcm.Length);
                var json = Utf8(Vosk.FinalResult(recogniser));
                Vosk.RecognizerFree(recogniser);
                var text = Field(json, "text");
                command = Parse(text);
                Plugin.Log.LogInfo($"Voice commands: heard \"{text}\" → {command ?? "nothing known"}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice commands: recognition failed: " + e.Message);
            }
            pendingCommand = command;
            pendingResult = true;
        }

        private static bool EnsureRecogniser()
        {
            if (ready) return true;
            if (failed) return false;
            try
            {
                var folder = Path.Combine(Paths.BepInExRootPath, "vosk"); // not under plugins: BepInEx scans that for mods
                var modelPath = Path.Combine(folder, "model");
                if (!File.Exists(Path.Combine(folder, "libvosk.dll")) || !Directory.Exists(modelPath))
                    throw new FileNotFoundException("BepInEx/vosk (libvosk.dll and model) is missing");
                // Load the native DLLs by full path (dependencies first), then call libvosk through pointers.
                IntPtr module = IntPtr.Zero;
                foreach (var dll in new[] { "libwinpthread-1.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll", "libvosk.dll" })
                {
                    var path = Path.Combine(folder, dll);
                    module = File.Exists(path) ? Vosk.LoadLibraryW(path) : IntPtr.Zero;
                    if (module == IntPtr.Zero) throw new Exception($"couldn't load {dll} (error {Marshal.GetLastWin32Error()})");
                }
                Vosk.Bind(module); // the last one loaded: libvosk.dll
                Vosk.SetLogLevel(-1);
                model = Vosk.ModelNew(Vosk.Str(modelPath));
                if (model == IntPtr.Zero) throw new Exception("couldn't load the model in " + modelPath);
                ready = true;
                Plugin.Log.LogInfo("Voice commands: offline recognition ready");
                return true;
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning("Voice commands: off: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // libvosk, called through function pointers from the module loaded by full path: the game's Unity
        // runtime only looks for [DllImport] libraries in its own folders (stbc_Data/Mono, /Plugins).
        private static class Vosk
        {
            [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr LoadLibraryW(string path);

            [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string name);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SetLogLevelFn(int level);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ModelNewFn(byte[] path);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr RecognizerNewGrmFn(IntPtr model, float sampleRate, byte[] grammar);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AcceptWaveformFn(IntPtr recogniser, byte[] data, int length);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr FinalResultFn(IntPtr recogniser);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void RecognizerFreeFn(IntPtr recogniser);

            internal static SetLogLevelFn SetLogLevel;
            internal static ModelNewFn ModelNew;
            internal static RecognizerNewGrmFn RecognizerNewGrm;
            internal static AcceptWaveformFn AcceptWaveform;
            internal static FinalResultFn FinalResult;
            internal static RecognizerFreeFn RecognizerFree;

            internal static void Bind(IntPtr module)
            {
                SetLogLevel = Fn<SetLogLevelFn>(module, "vosk_set_log_level");
                ModelNew = Fn<ModelNewFn>(module, "vosk_model_new");
                RecognizerNewGrm = Fn<RecognizerNewGrmFn>(module, "vosk_recognizer_new_grm");
                AcceptWaveform = Fn<AcceptWaveformFn>(module, "vosk_recognizer_accept_waveform");
                FinalResult = Fn<FinalResultFn>(module, "vosk_recognizer_final_result");
                RecognizerFree = Fn<RecognizerFreeFn>(module, "vosk_recognizer_free");
            }

            private static T Fn<T>(IntPtr module, string name) where T : class
            {
                var address = GetProcAddress(module, name);
                if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
                return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }

            // C strings: UTF-8, null-terminated.
            internal static byte[] Str(string s) => Encoding.UTF8.GetBytes(s + "\0");
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        private static bool IsCaptain()
        {
            var vrm = VoiceRecognitionManager.IEFBKOMBFGO;
            return vrm != null && vrm.CurrentRoleIsCaptain();
        }

        private static void SendUiEvent(string type)
        {
            try
            {
                var t = AccessTools.TypeByName(type);
                var bus = AccessTools.TypeByName("CIFDOFLFGEA");
                if (t != null && bus != null) AccessTools.Method(bus, "Send")?.Invoke(null, new[] { Activator.CreateInstance(t, true) });
            }
            catch { /* the indicator is a nicety */ }
        }

        private static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            return Encoding.UTF8.GetString(bytes);
        }

        // {"text" : "shields up"} → shields up (Vosk's result is that simple)
        private static string Field(string json, string key)
        {
            if (json == null) return null;
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int a = json.IndexOf('"', json.IndexOf(':', k) + 1), b = a < 0 ? -1 : json.IndexOf('"', a + 1);
            return a < 0 || b < 0 ? null : json.Substring(a + 1, b - a - 1).Trim();
        }
    }
}
