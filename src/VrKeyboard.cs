using System;
using System.Text;
using HarmonyLib;
using UnityEngine;
using OpenVR = Valve.VR.MHCAEAFAOGC;      // Valve.VR.OpenVR (obfuscated): OJDNOLJLHHE = Overlay
using VRSystem = Valve.VR.PBMLFENDPMG;    // CVRSystem
using VREvent = Valve.VR.PJLFDHPDDMC;     // VREvent_t

namespace StbcRevival
{
    /// <summary>
    /// Typing in the headset: SteamVR's own keyboard (IVROverlay.ShowKeyboard), for joining a crew by room code
    /// (Revival settings, "Join a crew by its room code"). The keyboard reports Done / Closed as VR events; the
    /// game's SteamVR_Render reads every event through CVRSystem.PollNextEvent, so a postfix there sees them
    /// without taking them from anyone (ours carry UserValue). Only with SteamVR: on Oculus's own runtime, or
    /// out of VR, it says to use the desktop's F7 window instead.
    /// </summary>
    internal static class VrKeyboard
    {
        private const ulong UserValue = 0x53544243; // "STBC": our keyboard, not some other overlay's
        private const uint KeyboardClosed = 1200, KeyboardDone = 1202; // EVREventType
        private static Action<string> pending;

        /// <summary>What the keyboard is doing, for the settings page; null when idle.</summary>
        internal static string Status { get; private set; }

        internal static void Patch(Harmony harmony)
        {
            var poll = AccessTools.Method(typeof(VRSystem), "PollNextEvent");
            if (poll == null)
            {
                Plugin.Log.LogWarning("VR keyboard: CVRSystem.PollNextEvent not found; no typing in the headset");
                return;
            }
            harmony.Patch(poll, postfix: new HarmonyMethod(typeof(VrKeyboard), nameof(Poll_Postfix)));
        }

        internal static void Open(string description, uint maxChars, Action<string> done)
        {
            string device = UnityEngine.VR.VRSettings.enabled ? UnityEngine.VR.VRSettings.loadedDeviceName : "";
            if (device != "OpenVR")
            {
                Status = "Needs SteamVR: use F7 on the desktop";
                RoomBrowser.Show(device == ""
                    ? "Not in VR: type the room code in this window (F7)."
                    : "The headset keyboard needs SteamVR. Type the room code in the F7 window on the desktop, or join friends from the shuttle's friends console.");
                return;
            }
            try
            {
                var overlay = OpenVR.OJDNOLJLHHE;
                if (overlay == null) throw new InvalidOperationException("no SteamVR overlay");
                var error = overlay.ShowKeyboard(0 /* normal */, 0 /* single line */, description, maxChars, "", false, UserValue);
                if ((int)(object)error != 0) throw new InvalidOperationException("error " + error);
                pending = done;
                Status = "Type the code on the SteamVR keyboard";
                Plugin.Log.LogInfo("VR keyboard: open");
            }
            catch (Exception e)
            {
                Status = "SteamVR keyboard unavailable";
                Plugin.Log.LogWarning("VR keyboard: " + e.Message);
                RoomBrowser.Show("The SteamVR keyboard didn't open. Type the room code in the F7 window on the desktop instead.");
            }
        }

        private static void Poll_Postfix(bool __result, ref VREvent __0)
        {
            if (!__result || pending == null) return;
            if (__0.eventType != KeyboardDone && __0.eventType != KeyboardClosed) return;
            ulong user = __0.data.keyboard.uUserValue;
            if (user != UserValue && user != 0) return; // someone else's keyboard
            var done = pending;
            pending = null;
            Status = null;
            if (__0.eventType == KeyboardClosed)
            {
                Plugin.Log.LogInfo("VR keyboard: closed without a code");
                return;
            }
            try
            {
                var text = new StringBuilder(64);
                OpenVR.OJDNOLJLHHE.GetKeyboardText(text, 64);
                var typed = text.ToString().Trim();
                Plugin.Log.LogInfo($"VR keyboard: typed '{typed}'");
                if (typed.Length > 0) done(typed);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("VR keyboard: reading the text: " + e.Message);
            }
        }
    }
}
