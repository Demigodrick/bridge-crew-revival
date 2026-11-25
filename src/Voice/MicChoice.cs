// [Voice] Microphone: which microphone voice chat records from (and so Revival voice and voice commands, which use
// that recording). The game takes the Rift's own microphone under the Oculus runtime and otherwise the first one in
// Unity's list (WwiseMicrophoneInputDevice.CheckDevices: Microphone.devices[0]), never the one chosen in Windows: a
// Quest through Virtual Desktop or Steam Link often records from a webcam or desk mic across the room (issue 44).
//  - Game (the default): the game's own choice, as without the mod; nothing here touches voice chat.
//  - A device's name, as Unity lists it: that one, while it's plugged in; else the game's choice.
//  - Auto: Windows' default communications microphone (else its default microphone), when Unity lists it; else the
//    game's choice. Not the default: under Wine the device Windows calls default (PulseAudio Input) needn't be the one
//    that carries the voice, and 0.8.2, which had it as the default, left some players silent.
// Going back to Game puts back what the game had chosen (and asks the game to choose again).
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
            string choice = Plugin.VoiceMicrophone.Value;
            if (string.IsNullOrEmpty(choice) || string.Equals(choice, Game, StringComparison.OrdinalIgnoreCase)) return null;
            var devices = Microphone.devices;
            if (devices.Length == 0) return null;
            if (string.Equals(choice, Auto, StringComparison.OrdinalIgnoreCase)) return Match(WindowsDefault(), devices);
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
            if (want == null)
            {
                if (changed) PutBack(input, current);
                return;
            }
            if (want == current) return;
            var type = input.GetType();
            if (!changed)
            {
                // What the game chose, to put back when the setting goes back to Game.
                changed = true;
                gameDevice = current;
                var dm = DefaultMicrophone(type);
                gameDefault = dm?.GetValue(null) as string;
            }
            if (!Switch(input, current, want)) return;
            string line = $"Microphone: {want} (was {(string.IsNullOrEmpty(current) ? "none" : current)}; setting {Plugin.VoiceMicrophone.Value})";
            if (line != told) { told = line; Plugin.Log.LogInfo(line); }
        }

        private static bool changed;              // voice chat's microphone was changed here
        private static string gameDevice, gameDefault;

        // To another microphone: if it's recording, through the game's own switch (it stops the old recording and starts
        // the new one); if not (muted, or not in a session), only the name, so recording doesn't start, and DaikonForge's
        // input starts from its DefaultMicrophone, so that too.
        private static bool Switch(DaikonForge.VoIP.AudioInputDeviceBase input, string current, string to)
        {
            var type = input.GetType();
            if (!string.IsNullOrEmpty(current) && Microphone.IsRecording(current) && !string.IsNullOrEmpty(to))
            {
                var change = type.GetMethod("ChangeMicrophoneDevice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (change == null) return false;
                change.Invoke(input, new object[] { to });
                return true;
            }
            if (!MicTap.SetChatDevice(input, to)) return false;
            var dm = DefaultMicrophone(type);
            if (dm != null && !string.IsNullOrEmpty(to)) dm.SetValue(null, to);
            return true;
        }

        // Back to the game's choice: what it had, and it's asked to choose again (ForceDeviceRecheck).
        private static void PutBack(DaikonForge.VoIP.AudioInputDeviceBase input, string current)
        {
            changed = false;
            try
            {
                var dm = DefaultMicrophone(input.GetType());
                if (dm != null) dm.SetValue(null, gameDefault);
                if (current != gameDevice) Switch(input, current, gameDevice ?? "");
                var recheck = typeof(DaikonForge.VoIP.AudioInputDeviceBase).GetProperty("JMHIEDEJOFL", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (recheck != null && recheck.CanWrite) recheck.SetValue(input, true, null);
                Plugin.Log.LogInfo($"Microphone: back to the game's choice ({(string.IsNullOrEmpty(gameDevice) ? "none yet" : gameDevice)})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Microphone: couldn't put back the game's choice: " + e.Message); }
        }

        private static FieldInfo DefaultMicrophone(Type type)
        {
            var f = type.GetField("DefaultMicrophone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return f != null && f.FieldType == typeof(string) ? f : null;
        }

        /// <summary>The settings menu's next choice after the current one: Game, then each microphone Unity lists, then Windows' default.</summary>
        internal static string Next()
        {
            var devices = Microphone.devices;
            string current = Plugin.VoiceMicrophone.Value;
            if (string.IsNullOrEmpty(current) || string.Equals(current, Game, StringComparison.OrdinalIgnoreCase)) return devices.Length > 0 ? devices[0] : Auto;
            if (string.Equals(current, Auto, StringComparison.OrdinalIgnoreCase)) return Game;
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
            string current = Plugin.VoiceMicrophone.Value;
            if (string.IsNullOrEmpty(current) || string.Equals(current, Game, StringComparison.OrdinalIgnoreCase)) return "Microphone: THE GAME'S CHOICE";
            if (string.Equals(current, Auto, StringComparison.OrdinalIgnoreCase))
            {
                string found = Match(WindowsDefault(), Microphone.devices);
                return "Microphone: WINDOWS DEFAULT" + (found != null ? " (" + Short(found) + ")" : "");
            }
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
