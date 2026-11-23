// [Voice] Microphone: which microphone voice chat records from (and so Revival voice and voice commands, which use
// that recording). The game takes the Rift's own microphone under the Oculus runtime and otherwise the first one in
// Unity's list (WwiseMicrophoneInputDevice.CheckDevices: Microphone.devices[0]), never the one chosen in Windows: a
// Quest through Virtual Desktop or Steam Link often records from a webcam or desk mic across the room (issue 44).
//  - Auto: Windows' default communications microphone (else its default microphone), when Unity lists it; else the
//    game's choice.
//  - Game: the game's own choice, as without the mod.
//  - A device's name, as Unity lists it: that one, while it's plugged in; else the game's choice.
// Checked twice a second: the game picks again whenever a microphone comes or goes, and this puts the chosen one back.
// Main thread only (Unity's Microphone).
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace StbcRevival
{
    internal static class MicChoice
    {
        internal const string Auto = "Auto", Game = "Game";
        private const float CheckSeconds = 0.5f;
        private static float nextCheck;
        private static string told;

        /// <summary>The microphone to record from now (a name in Microphone.devices), or null for the game's choice.</summary>
        internal static string Wanted()
        {
            string choice = Plugin.VoiceMicrophone.Value ?? Auto;
            var devices = Microphone.devices;
            if (devices.Length == 0 || string.Equals(choice, Game, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrEmpty(choice) || string.Equals(choice, Auto, StringComparison.OrdinalIgnoreCase))
                return Match(WindowsDefault(), devices);
            foreach (var d in devices) if (d == choice) return d;
            return null; // not plugged in
        }

        internal static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + CheckSeconds;
            var input = MicTap.ChatInput();
            if (input == null || !MicTap.ChatDevice(input, out var current)) return;
            string want = Wanted();
            if (want == null || want == current) return;
            var type = input.GetType();
            if (!string.IsNullOrEmpty(current) && Microphone.IsRecording(current))
            {
                // Recording: switched over (the game stops the old recording and starts the new one).
                var change = type.GetMethod("ChangeMicrophoneDevice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (change == null) return;
                change.Invoke(input, new object[] { want });
            }
            else
            {
                // Not recording (muted, or not in a session): only the name, so recording doesn't start; it's used
                // when it does. DaikonForge's input starts from its DefaultMicrophone, so that too.
                if (!MicTap.SetChatDevice(input, want)) return;
                var fallback = type.GetField("DefaultMicrophone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fallback != null && fallback.FieldType == typeof(string)) fallback.SetValue(null, want);
            }
            string line = $"Microphone: {want} (was {(string.IsNullOrEmpty(current) ? "none" : current)}; setting {Plugin.VoiceMicrophone.Value})";
            if (line != told) { told = line; Plugin.Log.LogInfo(line); }
        }

        /// <summary>The settings menu's next choice after the current one: Auto, Game, then each microphone Unity lists.</summary>
        internal static string Next()
        {
            var devices = Microphone.devices;
            string current = Plugin.VoiceMicrophone.Value ?? Auto;
            if (string.Equals(current, Auto, StringComparison.OrdinalIgnoreCase)) return Game;
            if (string.Equals(current, Game, StringComparison.OrdinalIgnoreCase)) return devices.Length > 0 ? devices[0] : Auto;
            int at = Array.IndexOf(devices, current);
            return at >= 0 && at + 1 < devices.Length ? devices[at + 1] : Auto;
        }

        private static string described;
        private static float describedUntil;

        /// <summary>What the settings menu shows (worked out at most once a second: it's asked every frame).</summary>
        internal static string Describe()
        {
            if (described != null && Time.realtimeSinceStartup < describedUntil) return described;
            describedUntil = Time.realtimeSinceStartup + 1f;
            return described = DescribeNow();
        }

        /// <summary>The settings menu's text is worked out again at once (after a click).</summary>
        internal static void Changed() => described = null;

        private static string DescribeNow()
        {
            string current = Plugin.VoiceMicrophone.Value ?? Auto;
            if (string.Equals(current, Auto, StringComparison.OrdinalIgnoreCase))
            {
                string found = Match(WindowsDefault(), Microphone.devices);
                return "Microphone: WINDOWS DEFAULT" + (found != null ? " (" + Short(found) + ")" : "");
            }
            if (string.Equals(current, Game, StringComparison.OrdinalIgnoreCase)) return "Microphone: THE GAME'S CHOICE";
            return "Microphone: " + Short(current) + (Array.IndexOf(Microphone.devices, current) < 0 ? " (not plugged in)" : "");
        }

        private static string Short(string name) => name.Length <= 32 ? name : name.Substring(0, 31) + "…";

        // A Windows device name to Unity's: WinMM gives at most 31 characters ("Microphone (Virtual Desktop Au"),
        // Unity the whole name, so the first that starts with it.
        private static string Match(string windows, string[] devices)
        {
            if (string.IsNullOrEmpty(windows)) return null;
            foreach (var d in devices) if (d == windows) return d;
            foreach (var d in devices) if (d.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || windows.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        // ---- Windows' default microphone, through WinMM (also under Wine): asked of the wave mapper ------------------

        private const uint DrvmMapperPreferredGet = 0x2015, DrvmMapperConsoleVoiceComGet = 0x2017;
        private static readonly IntPtr WaveMapper = new IntPtr(0xFFFFFFFFL);
        private static bool winmmMissing;
        private static string lastDefault;
        private static float defaultUntil;

        [DllImport("winmm.dll")]
        private static extern int waveInMessage(IntPtr hwi, uint msg, ref uint device, ref uint flags);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveInGetDevCapsW(UIntPtr device, ref WaveInCaps caps, uint size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveInCaps
        {
            public ushort Mid, Pid;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
        }

        /// <summary>Windows' default communications microphone's name (else its default microphone's), or null. Asked at most once a second.</summary>
        internal static string WindowsDefault()
        {
            if (winmmMissing) return null;
            if (Time.realtimeSinceStartup < defaultUntil) return lastDefault;
            defaultUntil = Time.realtimeSinceStartup + 1f;
            try
            {
                lastDefault = NameOf(DrvmMapperConsoleVoiceComGet) ?? NameOf(DrvmMapperPreferredGet);
            }
            catch (DllNotFoundException e) { NoWinmm(e); }
            catch (EntryPointNotFoundException e) { NoWinmm(e); }
            return lastDefault;
        }

        private static void NoWinmm(Exception e)
        {
            winmmMissing = true;
            lastDefault = null;
            Plugin.Log.LogInfo("Microphone: Windows' default can't be read here (" + e.GetType().Name + "); the game's choice instead");
        }

        private static string NameOf(uint message)
        {
            uint device = 0xFFFFFFFF, flags = 0;
            if (waveInMessage(WaveMapper, message, ref device, ref flags) != 0 || device == 0xFFFFFFFF) return null;
            var caps = new WaveInCaps();
            if (waveInGetDevCapsW(new UIntPtr(device), ref caps, (uint)Marshal.SizeOf(typeof(WaveInCaps))) != 0) return null;
            return string.IsNullOrEmpty(caps.Name) ? null : caps.Name.TrimEnd();
        }
    }
}
