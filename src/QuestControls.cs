using System;
using System.Text;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using OpenVR = Valve.VR.MHCAEAFAOGC;               // Valve.VR.OpenVR (obfuscated): PBCKCOJGAEI = System
using ControllerState = Valve.VR.JLCPFAINHMF;      // VRControllerState_t
using ControllerRole = Valve.VR.NKGBNPGDCLH;       // ETrackedControllerRole
using DeviceProperty = Valve.VR.LJALAHBNBKF;       // ETrackedDeviceProperty
using PropertyError = Valve.VR.OBBEJANHPDI;        // ETrackedPropertyError

namespace StbcRevival
{
    /// <summary>
    /// [VR] QuestControls (off by default; the installer's Quest question turns it on): Meta Quest / Oculus Touch
    /// controllers through SteamVR (Virtual Desktop, Steam Link) get Touch-style buttons, as when playing through Meta
    /// Link, instead of being treated as Vive wands. From BamaSuperman's Quest Controller Mapping add-on (MIT).
    ///
    /// It needs SteamVR to use the Touch binding that comes with the mod (BepInEx/vr/quest-binding.json,
    /// selected in SteamVR's settings by the installer). Only with SteamVR running and a Meta/Oculus headset; anything
    /// else keeps the game's own controls. The game fills its actions and axes from SteamVR's controllers each frame
    /// (SVR.GJDABPJJLBF: HFMELMLOJPG for buttons, KGKMIOOCKLC for axes, the Vive wands' layout): those two are
    /// replaced with the Touch layout:
    ///   A: confirm, skip the captain's log; hold for voice and crew commands.   B: back, close menus.
    ///   X: help.   Y: exterior view.   Right stick click: pause.   Left stick held 1 s: recentre.
    ///   Sticks, triggers and grips: as the game's own Touch controls; a stick flicked sideways changes the spectator camera.
    /// The game may still show Vive controllers and prompts.
    /// </summary>
    internal static class QuestControls
    {
        /// <summary>The Touch layout is in use right now (SteamVR, a Meta/Oculus headset, both controllers).</summary>
        internal static bool Active;

        private static readonly uint stateSize = (uint)Marshal.SizeOf(typeof(ControllerState));
        private static readonly Mapper mapper = new Mapper();
        private static Hand left, right;
        private static int sampledFrame = -1;
        private static bool sampled, failed, headsetAccepted;
        private static float nextHeadsetCheck;

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.QuestControlsOn.Value) return;
            try
            {
                var map = AccessTools.TypeByName("SVR.GJDABPJJLBF");
                var buttons = map == null ? null : AccessTools.Method(map, "HFMELMLOJPG", new[] { typeof(bool[]) });
                var axes = map == null ? null : AccessTools.Method(map, "KGKMIOOCKLC", new[] { typeof(float[]) });
                if (buttons == null || axes == null) { Plugin.Log.LogWarning("Quest controls: the game's SteamVR controls weren't found; off"); return; }
                harmony.Patch(buttons, prefix: new HarmonyMethod(typeof(QuestControls), nameof(Buttons)));
                harmony.Patch(axes, prefix: new HarmonyMethod(typeof(QuestControls), nameof(Axes)));
                Plugin.Log.LogInfo("Quest controls: on (Touch-style buttons with a Meta/Oculus headset through SteamVR)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Quest controls: " + e.Message); }
        }

        private static void Fail(Exception e)
        {
            failed = true; SetActive(false);
            Plugin.Log.LogWarning("Quest controls: off for this session, the game's own controls back: " + e);
        }

        private static void SetActive(bool on)
        {
            if (Active == on) return;
            Active = on;
            if (!on) mapper.Reset();
            Plugin.Log.LogInfo(on ? "Quest controls: in use (SteamVR, Meta/Oculus headset)" : "Quest controls: not in use; the game's own controls");
        }

        /// <summary>Both controllers read for this frame; false (the game's own controls) if it isn't a Quest on SteamVR.</summary>
        private static bool Sample()
        {
            if (failed || !CameraManager.EDPLGHHIGNF) { SetActive(false); return false; } // IsVREnabled
            if (sampledFrame == Time.frameCount) return sampled;
            sampledFrame = Time.frameCount; sampled = false;
            if (!IEDCLJPAJKO.IsSteamVRDeviceActive()) { SetActive(false); return false; }
            var system = OpenVR.PBCKCOJGAEI;
            if (system == null) { SetActive(false); return false; }
            if (Time.unscaledTime >= nextHeadsetCheck)
            {
                nextHeadsetCheck = Time.unscaledTime + 5f;
                var error = (PropertyError)0;
                var text = new StringBuilder(256);
                system.GetStringTrackedDeviceProperty(0, (DeviceProperty)1005, text, 256, ref error); // ManufacturerName
                string maker = text.ToString().ToLowerInvariant();
                headsetAccepted = maker.Contains("oculus") || maker.Contains("meta");
            }
            if (!headsetAccepted) { SetActive(false); return false; }
            uint li = system.GetTrackedDeviceIndexForControllerRole((ControllerRole)1);
            uint ri = system.GetTrackedDeviceIndexForControllerRole((ControllerRole)2);
            ControllerState ls = new ControllerState(), rs = new ControllerState();
            if (li == uint.MaxValue || ri == uint.MaxValue || !system.GetControllerState(li, ref ls, stateSize) || !system.GetControllerState(ri, ref rs, stateSize))
            { SetActive(false); return false; }
            left = Hand.Of(ls); right = Hand.Of(rs);
            if (!Active) mapper.Seed(left, right, Time.unscaledTime);
            SetActive(true);
            sampled = true;
            return true;
        }

        private static readonly bool[] actionsCopy = new bool[256];
        private static readonly float[] axesCopy = new float[64];

        private static bool Buttons(bool[] __0)
        {
            try
            {
                if (!Sample()) return true;
                if (__0 == null || __0.Length < 99 || __0.Length > actionsCopy.Length) throw new InvalidOperationException($"the game's action list is {__0?.Length} long");
                // Mapped on a copy: an exception leaves the game's input as it was.
                Array.Copy(__0, actionsCopy, __0.Length);
                bool paused = SVR.InputManager.IEFBKOMBFGO != null && SVR.InputManager.IEFBKOMBFGO.IsPauseMenuShown;
                mapper.Map(actionsCopy, left, right, Time.unscaledTime, paused);
                Array.Copy(actionsCopy, __0, __0.Length);
                return false;
            }
            catch (Exception e) { Fail(e); return true; }
        }

        private static bool Axes(float[] __0)
        {
            try
            {
                if (!Sample()) return true;
                if (__0 == null || __0.Length < 14 || __0.Length > axesCopy.Length) throw new InvalidOperationException($"the game's axis list is {__0?.Length} long");
                Array.Copy(__0, axesCopy, __0.Length);
                Mapper.Axes(axesCopy, left, right);
                Array.Copy(axesCopy, __0, __0.Length);
                return false;
            }
            catch (Exception e) { Fail(e); return true; }
        }

        /// <summary>One controller's state.</summary>
        internal struct Hand
        {
            public ulong Pressed, Touched;
            public float X, Y, Trigger, Grip;
            public bool Primary => (Pressed & (1UL << 7)) != 0;    // A / X
            public bool Secondary => (Pressed & (1UL << 1)) != 0;  // B / Y
            public bool Stick => (Pressed & (1UL << 32)) != 0;     // stick click

            public static Hand Of(ControllerState s) => new Hand
            {
                Pressed = s.ulButtonPressed, Touched = s.ulButtonTouched, X = s.rAxis0.x, Y = s.rAxis0.y, Trigger = s.rAxis1.x,
                Grip = s.rAxis2.x > 0 ? s.rAxis2.x : ((s.ulButtonPressed & 4UL) != 0 ? 1f : 0f),
            };
        }

        /// <summary>The Touch layout onto the game's actions (indexes into SVR.LONCAPHEAPA) and axes (SVR.FEKBGPPHGEP).</summary>
        internal sealed class Mapper
        {
            private Hand previousLeft, previousRight;
            private bool ready, recentred;
            private float recentreStart;
            private int leftDirection, rightDirection;

            public void Reset() { ready = false; recentred = false; leftDirection = rightDirection = 0; }

            private static int Direction(Hand h)
            {
                if (h.Y >= 0.5f && Math.Abs(h.X) < 0.4f) return 1;
                if (h.X >= 0.5f && Math.Abs(h.Y) < 0.4f) return 2;
                if (h.Y <= -0.5f && Math.Abs(h.X) < 0.4f) return 3;
                if (h.X <= -0.5f && Math.Abs(h.Y) < 0.4f) return 4;
                return 0;
            }

            private static float Clamp(float x) => Math.Max(0f, Math.Min(1f, x));

            public void Seed(Hand l, Hand r, float time)
            {
                previousLeft = l; previousRight = r; ready = true;
                recentreStart = time; recentred = l.Stick;
                leftDirection = Direction(l); rightDirection = Direction(r);
            }

            public void Map(bool[] actions, Hand l, Hand r, float time, bool paused)
            {
                if (!ready) Seed(l, r, time);
                bool aDown = r.Primary && !previousRight.Primary;
                bool aUp = !r.Primary && previousRight.Primary;
                bool bDown = r.Secondary && !previousRight.Secondary;
                int ld = Direction(l), rd = Direction(r);
                bool leftFlick = ld != 0 && leftDirection == 0;
                bool rightFlick = rd != 0 && rightDirection == 0;
                actions[0] |= aDown;                                    // PressToContinue
                actions[1] |= bDown;                                    // BackOrCancel
                actions[43] |= bDown;                                   // CloseAllMenus
                actions[5] = (l.Touched & (1UL << 33)) != 0 || l.Trigger > 0.05f; // ContactLeftIndexTrigger
                actions[6] = (r.Touched & (1UL << 33)) != 0 || r.Trigger > 0.05f; // ContactRightIndexTrigger
                actions[40] |= r.Stick && !previousRight.Stick;         // PauseMenuMode
                if (l.Stick && !previousLeft.Stick) { recentreStart = time; recentred = false; }
                if (l.Stick && !recentred && time - recentreStart >= 1f) { actions[2] = true; recentred = true; } // CalibrateVR
                if (!l.Stick) recentred = false;
                if (!paused)
                {
                    actions[7] |= l.Secondary;                          // ExteriorCamera (Y)
                    actions[42] |= l.Primary;                           // ShowHelpSystem (X)
                    actions[37] |= aUp;                                 // crew command released
                    actions[39] |= r.Primary;                           // VoiceCommand (A held)
                    actions[41] |= aDown;                               // SkipCaptainsLog
                    actions[8] |= (leftFlick && ld == 4) || (rightFlick && rd == 4); // SpectatorPreviousCamera
                    actions[9] |= (leftFlick && ld == 2) || (rightFlick && rd == 2); // SpectatorNextCamera
                }
                previousLeft = l; previousRight = r;
                // A new flick needs the stick back in the middle first.
                if (ld == 0) leftDirection = 0; else if (leftDirection == 0) leftDirection = ld;
                if (rd == 0) rightDirection = 0; else if (rightDirection == 0) rightDirection = rd;
            }

            public static void Axes(float[] axes, Hand l, Hand r)
            {
                axes[0] = l.X; axes[1] = l.Y; axes[2] = r.X; axes[3] = r.Y;          // the sticks
                axes[4] = Clamp(l.Trigger); axes[5] = Clamp(r.Trigger);              // index triggers
                axes[6] = Clamp(l.Grip); axes[7] = Clamp(r.Grip);                    // grips
                axes[13] = Clamp(r.Trigger) - Clamp(l.Trigger);                      // the exterior view's speed (SteamVR's)
            }
        }
    }
}
