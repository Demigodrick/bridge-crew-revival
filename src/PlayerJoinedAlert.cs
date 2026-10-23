using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Rooms] PlayerJoinedAlert: when someone joins your crew, play the game's "objective complete" chime
    /// and flash the game's taskbar entry (if the window isn't in front). For waiting in a briefing room
    /// with the headset off. Watches the room's player count; the first reading in each room is just noted.
    /// </summary>
    internal static class PlayerJoinedAlert
    {
        private const string Chime = "Play_UI_Tutorial_Objective_Complete";
        private static string room;
        private static int players;
        private static float nextCheck;

        internal static void Update()
        {
            if (!Plugin.PlayerJoinedAlert.Value || Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.5f;
            var current = GameState.RoomName();
            if (current == null || GameState.Loading())
            {
                room = null;
                return;
            }
            GameState.RoomCounts(out int count, out _, out _);
            if (current != room)
            {
                room = current;
                players = count;
                return;
            }
            if (count > players)
            {
                Plugin.Log.LogInfo($"Player joined your crew ({count} aboard)");
                Alert();
            }
            players = count;
        }

        private static void Alert()
        {
            try { WwiseSoundUtil.PostEvent(Chime, null); }
            catch (Exception e) { Plugin.Log.LogInfo("Player joined alert: no chime: " + e.Message); }
            try { FlashGameWindow(); }
            catch (Exception e) { Plugin.Log.LogInfo("Player joined alert: no flash: " + e.Message); }
        }

        // Windows (and Wine, which turns it into the window manager's "needs attention" hint).
        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        private const uint FlashAll = 3, FlashUntilForeground = 12;

        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        private static void FlashGameWindow()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return;
            var window = FindWindow("UnityWndClass", null);  // Unity's game window class
            if (window == IntPtr.Zero) return;
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != GetCurrentProcessId() || GetForegroundWindow() == window) return;
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                Window = window,
                Flags = FlashAll | FlashUntilForeground,
            };
            FlashWindowEx(ref info);
        }
    }
}
