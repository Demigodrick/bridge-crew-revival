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
    ///   - A known phrase: the command, as Watson's answers did; anything else: the crew's "didn't catch that".
    /// Needs BepInEx/vosk/libvosk.dll (and its three runtime DLLs) and a model folder,
    /// BepInEx/vosk/model (e.g. vosk-model-small-en-us-0.15).
    /// </summary>
    internal static class VoiceCommands
    {
        private const int SampleRate = 16000;
        private const int MaxSeconds = 10;

        // Prototype phrase list: phrase → the game's crew command (AI.BHPPPGKNAHK names).
        private static readonly Dictionary<string, string> Phrases = new Dictionary<string, string>
        {
            ["shields up"] = "RaiseShields", ["raise shields"] = "RaiseShields",
            ["full impulse"] = "SetSpeedFull", ["full speed"] = "SetSpeedFull", ["all ahead full"] = "SetSpeedFull",
            ["fire phasers"] = "FirePhasers", ["fire"] = "FirePhasers",
            ["fire torpedoes"] = "FireTorpedoes", ["fire photon torpedoes"] = "FireTorpedoes",
            ["target engines"] = "TargetEngines", ["target their engines"] = "TargetEngines",
        };

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
            // As after a Watson answer: refresh the crew's simple orders, then the command.
            var roles = AccessTools.TypeByName("FLCHMJJDKDL");
            var update = AccessTools.Method(manager, "UpdateSimpleOrders");
            foreach (var role in new[] { "Engineering", "Tactical", "Helm" })
                update?.Invoke(null, new[] { Enum.Parse(roles, role) });
            AccessTools.Method(manager, "TriggerCommand")?.Invoke(null, new[] { Enum.Parse(commands, command) });
            Plugin.Log.LogInfo($"Voice commands: {command}");
        }

        // ---- Recognition (Vosk, offline) --------------------------------------------------------------

        private static void Recognise(byte[] pcm)
        {
            string command = null;
            try
            {
                var grammar = "[" + string.Join(",", Phrases.Keys.Select(p => "\"" + p + "\"").ToArray()) + ",\"[unk]\"]";
                var recogniser = Vosk.RecognizerNewGrm(model, SampleRate, Vosk.Str(grammar));
                if (recogniser == IntPtr.Zero) throw new Exception("the recogniser didn't start");
                Vosk.AcceptWaveform(recogniser, pcm, pcm.Length);
                var json = Utf8(Vosk.FinalResult(recogniser));
                Vosk.RecognizerFree(recogniser);
                var text = Field(json, "text");
                Phrases.TryGetValue(text ?? "", out command);
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
