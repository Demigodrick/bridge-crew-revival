using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using SVR;
using UnityEngine;
using OpenVR = Valve.VR.MHCAEAFAOGC;               // Valve.VR.OpenVR (obfuscated): PBCKCOJGAEI = System
using ControllerState = Valve.VR.JLCPFAINHMF;      // VRControllerState_t
using ControllerRole = Valve.VR.NKGBNPGDCLH;       // ETrackedControllerRole

namespace StbcRevival.Walking
{
    /// <summary>
    /// Bridge Walk in VR (from BamaSuperman's VR Walking add-on, MIT): standing up in VR moves the headset's tracking
    /// origin (the rig the head camera sits in) with your character instead of placing a 2D camera. Your head keeps
    /// its own tracking: walking around your play space moves your character with you (walls still stop it), the
    /// left stick walks the way you look, the right stick turns you (smoothly or in steps, [Bridge Walk] VrTurning),
    /// and the walk key, or holding B on the right controller with the Quest controls on ([VR] QuestControls), stands
    /// you up and sits you down. Sticks are read through the game's own input, so any controllers on any runtime walk.
    ///
    /// Standing in VR, the game's seat warning (the screen fading when your head leaves the seat or nears your own
    /// body) is off; seated it's as the game has it. Your headset's own boundary is never touched.
    ///
    /// Each frame the rig is put back where the walk keeps it (in the bridge's space, so it moves with the ship) before
    /// the game's cameras draw, as the game sets it back toward the seat. Your hands stay where you hold them while the
    /// walk animates your body (TrackedArms). Crewmates see you as they see any standing player.
    /// </summary>
    internal static partial class Walker
    {
        /// <summary>Standing in VR (the rig is moved; the head is tracked).</summary>
        public static bool InVr;
        /// <summary>The walk reading the game's own sticks while it blocks them from the game (WalkPatches).</summary>
        public static bool ReadingInput;

        private static Transform rig;
        private static Camera vrCamera;
        private static Vector3 rigSavedLocalPos, rigBasisPos;
        private static Quaternion rigSavedLocalRot, rigBasisRot;
        private static bool turnLatched;

        private const float Deadzone = 0.25f;

        /// <summary>The game is in VR mode.</summary>
        public static bool VrOn
        {
            get { try { return CameraManager.EDPLGHHIGNF; } catch { return false; } } // IsVREnabled
        }

        // SteamVR_Camera's head and origin (their properties' names are scrambled; the getters' aren't).
        private static readonly System.Reflection.MethodInfo steamVrHead = AccessTools.Method(typeof(SteamVR_Camera), "get_head");
        private static readonly System.Reflection.MethodInfo steamVrOrigin = AccessTools.Method(typeof(SteamVR_Camera), "get_origin");

        private static Vector3 Planar(Vector3 v) => v - Up * Vector3.Dot(v, Up);
        private static Vector3 RigWorldPos => Basis != null ? Basis.TransformPoint(rigBasisPos) : rigBasisPos;
        private static Quaternion RigWorldRot => BasisRot * rigBasisRot;

        private static void StoreRig(Vector3 pos, Quaternion rot)
        {
            rigBasisPos = Basis != null ? Basis.InverseTransformPoint(pos) : pos;
            rigBasisRot = Quaternion.Inverse(BasisRot) * rot;
            RestoreRig();
        }

        private static void RestoreRig()
        {
            if (rig == null) return;
            rig.position = RigWorldPos;
            rig.rotation = RigWorldRot;
        }

        /// <summary>The head camera and the rig it's tracked in: the game's own VR camera (CharacterCamera under
        /// CharacterCameraRoot) or the SteamVR plugin's. False (and why, in the log) if there's none to move.</summary>
        private static bool FindVrRig(NovaPlayerInfo me, out Transform head)
        {
            head = null; rig = null; vrCamera = null;
            string mode;
            var main = Camera.main;
            var steamVr = main != null ? main.GetComponent<SteamVR_Camera>() : null;
            if (steamVr == null)
                foreach (var c in UnityEngine.Object.FindObjectsOfType<SteamVR_Camera>()) if (c.isActiveAndEnabled) { steamVr = c; break; }
            if (steamVr != null && steamVrHead != null && steamVrOrigin != null)
            {
                head = steamVrHead.Invoke(steamVr, null) as Transform; rig = steamVrOrigin.Invoke(steamVr, null) as Transform; vrCamera = steamVr.GetComponent<Camera>(); mode = "SteamVR's camera";
            }
            else if (me.CharacterCamera != null && me.CharacterCamera.isActiveAndEnabled && me.CharacterCameraRoot != null)
            {
                vrCamera = me.CharacterCamera; head = vrCamera.transform; rig = me.CharacterCameraRoot.transform; mode = "the game's VR camera";
            }
            else { Plugin.Log.LogInfo("Bridge walk: your VR camera isn't ready yet"); return false; }
            var pawnT = me.PawnObject.transform;
            if (head == null || rig == null || head == rig || !head.IsChildOf(rig) || rig == pawnT || pawnT.IsChildOf(rig))
            {
                Plugin.Log.LogWarning($"Bridge walk: can't walk in VR here: the head ({(head != null ? Bridge.Path(head) : "none")}) isn't tracked inside a rig of its own ({(rig != null ? Bridge.Path(rig) : "none")})");
                rig = null; return false;
            }
            Plugin.Log.LogInfo($"Bridge walk: in VR through {mode}: head {Bridge.Path(head)}, rig {Bridge.Path(rig)}");
            return true;
        }

        /// <summary>Standing up in VR, once the character is on its spot: the rig goes with it, your eyes at eye height.</summary>
        private static void StandVr()
        {
            rigSavedLocalPos = rig.localPosition;
            rigSavedLocalRot = rig.localRotation;
            StoreRig(rig.position, rig.rotation);
            Vector3 forward = Planar(cam.forward).normalized;
            if (forward.sqrMagnitude < 0.1f) forward = Planar(pawn.forward).normalized;
            Vector3 eye = pawn.position + Up * Mathf.Clamp(Plugin.BridgeWalkEyeHeight.Value, 1f, 2.2f) + forward * 0.12f;
            StoreRig(RigWorldPos + eye - cam.position, RigWorldRot);
            turnLatched = false;
            InVr = true;
            HeadingFromHead();
        }

        private static void SitVr()
        {
            InVr = false;
            if (rig != null) { rig.localPosition = rigSavedLocalPos; rig.localRotation = rigSavedLocalRot; }
            rig = null; vrCamera = null;
        }

        /// <summary>Your heading and pitch are where your head looks; the character faces it.</summary>
        private static void HeadingFromHead()
        {
            RestoreRig();
            Vector3 f = Quaternion.Inverse(BasisRot) * cam.forward;
            if (f.x * f.x + f.z * f.z < 0.001f) return;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            pawn.rotation = BasisRot * Quaternion.Euler(0, yaw, 0);
        }

        private static float Axis(FEKBGPPHGEP axis)
        {
            ReadingInput = true;
            try { return InputManager.GetAxis(axis); }
            catch { return 0f; }
            finally { ReadingInput = false; }
        }

        private static Vector2 Stick(FEKBGPPHGEP x, FEKBGPPHGEP y)
        {
            var v = new Vector2(Axis(x), Axis(y));
            float m = v.magnitude;
            if (m < Deadzone) return Vector2.zero;
            return v / m * Mathf.Clamp01((m - Deadzone) / (1f - Deadzone));
        }

        private static void TickVr(bool typing)
        {
            HeadingFromHead();
            bool blocked = typing;
            try { blocked |= InputManager.IEFBKOMBFGO != null && InputManager.IEFBKOMBFGO.IsPauseMenuShown; } catch { }
            Vector3 start = pawn.position;
            Vector3 forward = Planar(cam.forward).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = pawn.forward;

            // Walking around your play space: the character follows your head (the rig stays put).
            Vector3 room = Planar(cam.position - forward * 0.12f - pawn.position);
            if (room.sqrMagnitude > 0.0004f && room.magnitude < 3f) { TryMove(room); RestoreRig(); }

            Vector2 move = Vector2.zero;
            float turn = 0f;
            if (!blocked)
            {
                move = Stick(FEKBGPPHGEP.LeftHandX, FEKBGPPHGEP.LeftHandY);
                turn = Axis(FEKBGPPHGEP.RightHandX);
                move.x += (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
                move.y += (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            }

            float degrees = 0f;
            if (Plugin.BridgeWalkVrTurning.Value == Plugin.VrTurning.Smooth)
            {
                turnLatched = false;
                if (Mathf.Abs(turn) > Deadzone)
                    degrees = Mathf.Sign(turn) * Mathf.Clamp01((Mathf.Abs(turn) - Deadzone) / (1f - Deadzone))
                              * Mathf.Clamp(Plugin.BridgeWalkVrTurnSpeed.Value, 5f, 180f) * Mathf.Min(Time.deltaTime, 0.05f);
            }
            else
            {
                if (Mathf.Abs(turn) < 0.3f) turnLatched = false;
                if (Mathf.Abs(turn) > 0.7f && !turnLatched) { turnLatched = true; degrees = Mathf.Sign(turn) * Mathf.Clamp(Plugin.BridgeWalkVrSnapAngle.Value, 10f, 90f); }
            }
            if (Mathf.Abs(degrees) > 1e-4f)
            {
                // Turned about your head, so you stay where you are.
                Quaternion q = Quaternion.AngleAxis(degrees, Up);
                Vector3 pivot = cam.position;
                StoreRig(pivot + q * (RigWorldPos - pivot), q * RigWorldRot);
                HeadingFromHead();
                forward = Planar(cam.forward).normalized;
            }

            if (move.sqrMagnitude > 1f) move.Normalize();
            if (move.sqrMagnitude > 0.001f)
            {
                Vector3 before = pawn.position;
                Vector3 dir = forward * move.y + Vector3.Cross(Up, forward) * move.x;
                TryMove(dir * Mathf.Clamp(Plugin.BridgeWalkSpeed.Value, 0.1f, 3f) * Mathf.Min(Time.deltaTime, 0.05f));
                StoreRig(RigWorldPos + pawn.position - before, RigWorldRot);
            }
            float actual = Time.deltaTime > 0 ? (pawn.position - start).magnitude / Time.deltaTime : 0f;
            Speed = Mathf.Lerp(Speed, actual, 1f - Mathf.Exp(-10f * Time.deltaTime));
            HeadingFromHead();
            PlaceCamera();
            if (Time.unscaledTime >= nextSend) { nextSend = Time.unscaledTime + 1f / 15f; WalkNet.SendState(false); }
        }

        /// <summary>In VR the head is tracked: the rig goes back where the walk keeps it, and the outside views follow your eyes.</summary>
        private static void PlaceVr()
        {
            RestoreRig();
            if (onBridge) ViewscreenRig.Follow(cam, Basis);
            ShuttleView.Follow(cam);
        }

        // ---- holding B to stand up or sit down

        private static float heldSince = -1f;
        private static bool heldDone;
        private static readonly uint stateSize = (uint)Marshal.SizeOf(typeof(ControllerState));
        private static bool buttonBroken;

        /// <summary>True once, when B on the right controller has been held for 0.7 s. Only with the Quest controls: in
        /// the game's own SteamVR layout that button is the pause menu.</summary>
        public static bool VrToggleHeld()
        {
            if (!QuestControls.Active) { heldSince = -1f; return false; }
            bool down = RightB();
            if (!down) { heldSince = -1f; heldDone = false; return false; }
            if (heldSince < 0f) heldSince = Time.unscaledTime;
            if (heldDone || Time.unscaledTime - heldSince < 0.7f) return false;
            heldDone = true;
            return true;
        }

        private static bool RightB()
        {
            if (buttonBroken || !UnityEngine.VR.VRSettings.enabled || UnityEngine.VR.VRSettings.loadedDeviceName != "OpenVR") return false;
            try
            {
                var system = OpenVR.PBCKCOJGAEI;
                if (system == null) return false;
                uint right = system.GetTrackedDeviceIndexForControllerRole((ControllerRole)2);
                if (right == uint.MaxValue) return false;
                var state = new ControllerState();
                return system.GetControllerState(right, ref state, stateSize) && (state.ulButtonPressed & (1UL << 1)) != 0;
            }
            catch (Exception e) { buttonBroken = true; Plugin.Log.LogWarning("Bridge walk: reading the controller's B button: " + e.Message); return false; }
        }

        // ---- the rig before the game's cameras draw

        private static void BeforeVrCamera(Camera c)
        {
            if (InVr && c != null && c == vrCamera) RestoreRig();
        }
    }

    /// <summary>
    /// The walk animation poses the whole body, arms too: for your own character in VR, the hands (and head) are put
    /// back where the game's tracking had them, the arms bent to reach them.
    /// </summary>
    internal sealed class TrackedArms
    {
        private sealed class Arm
        {
            private readonly Transform upper, lower, hand;
            private readonly Vector3 target, elbow;
            private readonly Quaternion rotation;

            public Arm(Animator a, HumanBodyBones u, HumanBodyBones l, HumanBodyBones h)
            {
                upper = a.GetBoneTransform(u); lower = a.GetBoneTransform(l); hand = a.GetBoneTransform(h);
                if (hand != null) { target = hand.position; rotation = hand.rotation; }
                if (lower != null) elbow = lower.position;
            }

            public void Restore()
            {
                if (upper == null || lower == null || hand == null) return;
                Vector3 root = upper.position;
                float a = (lower.position - root).magnitude, b = (hand.position - lower.position).magnitude;
                Vector3 line = target - root;
                float distance = line.magnitude;
                if (distance < 0.001f || a < 0.001f || b < 0.001f) return;
                Vector3 direction = line / distance;
                float d = Mathf.Clamp(distance, Mathf.Abs(a - b) + 0.001f, a + b - 0.001f);
                float x = (a * a - b * b + d * d) / (2 * d);
                Vector3 pole = elbow - root;
                pole -= direction * Vector3.Dot(pole, direction);
                if (pole.sqrMagnitude < 0.0001f) pole = Vector3.Cross(direction, Vector3.up);
                if (pole.sqrMagnitude < 0.0001f) pole = Vector3.Cross(direction, Vector3.right);
                Vector3 wantElbow = root + direction * x + pole.normalized * Mathf.Sqrt(Mathf.Max(0, a * a - x * x));
                upper.rotation = Quaternion.FromToRotation(lower.position - root, wantElbow - root) * upper.rotation;
                lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, target - lower.position) * lower.rotation;
                hand.rotation = rotation;
            }
        }

        private Arm left, right;
        private Transform head;
        private Quaternion headRotation;

        /// <summary>Where the tracking has the hands and head now (before the walk pose), or null.</summary>
        public static TrackedArms Capture(GameObject visual)
        {
            var a = visual != null ? visual.GetComponentInChildren<Animator>() : null;
            if (a == null || !a.isHuman) return null;
            var t = new TrackedArms
            {
                left = new Arm(a, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
                right = new Arm(a, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
                head = a.GetBoneTransform(HumanBodyBones.Head),
            };
            if (t.head != null) t.headRotation = t.head.rotation;
            return t;
        }

        public void Restore()
        {
            left.Restore(); right.Restore();
            if (head != null) head.rotation = headRotation;
        }
    }

    /// <summary>The game's seat warning, off while you stand in VR (TrackingAndCalibrationWarning's check, AEFDMBEPPBN).</summary>
    internal static class SeatWarning
    {
        private static readonly AccessTools.FieldRef<WarningBase, bool> show = TryShow();

        private static AccessTools.FieldRef<WarningBase, bool> TryShow()
        {
            try { return AccessTools.FieldRefAccess<WarningBase, bool>("CDODPBAOPDN"); }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: the seat warning's flag wasn't found (" + e.Message + "); it stays on in VR"); return null; }
        }

        internal static void Patch(Harmony h)
        {
            var m = AccessTools.DeclaredMethod(typeof(TrackingAndCalibrationWarning), "AEFDMBEPPBN", Type.EmptyTypes);
            if (m == null || show == null) { Plugin.Log.LogWarning("Bridge walk: the game's seat warning check wasn't found; it may fade your view while you walk in VR"); return; }
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(SeatWarning), nameof(Check))); }
            catch (Exception e) { Plugin.Log.LogError("Bridge walk: patching the seat warning: " + e.Message); }
        }

        private static bool Check(TrackingAndCalibrationWarning __instance)
        {
            if (!Walker.Standing || !Walker.InVr) return true;
            show(__instance) = false;
            return false;
        }
    }
}
