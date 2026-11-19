using System;
using StbcRevival.Walking;
using SVR;
using UnityEngine;

namespace StbcRevival.Science
{
    /// <summary>
    /// Using the science console after standing up with Bridge Walk; seated, never. What changes when is ConsoleUse's
    /// (shared, unit-tested); this does the Unity side.
    ///   - Reach: standing, within 1.2 m of SciencePlacement.StandPoint along the floor, measured from your character
    ///     (Bridge Walk keeps it under your head in VR), with the console on.
    ///   - VR: in reach, the screen's controls are in your hands' list (ConsoleWidgets.Register) and Bridge Walk lets
    ///     the hands' index-pull, grab and index touch through, so pokes and slider drags work. The sticks still walk.
    ///   - Monitor: in reach, "F: use science console" ([Science] UseKey). The key engages: walking and mouse look stop,
    ///     the camera eases to face the screen from 0.6 m and the cursor is freed. Clicks and drags on the screen are
    ///     handled here (a ray from the mouse against the controls' mouse colliders): the game's own mouse path only
    ///     presses a control its character's hand has touched, and a standing character's hands don't reach a console
    ///     the seat's poses weren't made for. The use key or Escape (swallowed) goes back to walking.
    ///   - Leaving reach or sitting down: the controls leave the hands' list and the console is freed (ScienceScreen.Leave:
    ///     the progress is reported and stays on the board).
    /// </summary>
    internal static class ScienceUse
    {
        private const string P = "Science console: ";
        private const float ViewDistance = 0.6f; // the engaged camera, in front of the screen's middle

        private static readonly ConsoleUse use = new ConsoleUse();
        private static ScienceScreen registeredOn;
        private static float blend;
        private static SliderInteraction dragging;
        private static bool warnedConsole;

        /// <summary>Standing within reach of the console (and it's on).</summary>
        internal static bool InReach => use.InReach;

        /// <summary>Using it on a monitor: the view on the screen, the mouse free.</summary>
        internal static bool Engaged => use.Engaged;

        /// <summary>Every frame, from Bridge Walk's update (before its Escape check).</summary>
        internal static void Tick()
        {
            var me = Walker.Me;
            bool standing = Walker.Standing && me != null && me.PawnObject != null;
            var screen = ScienceScreen.Current;
            bool typing = TextPrompt.IsOpen;
            var frame = new UseFrame
            {
                Standing = standing,
                Vr = Walker.InVr,
                Active = ScienceSession.Active && screen != null,
                Distance = standing ? SciencePlacement.DistanceFrom(me.PawnObject.transform.position) : float.PositiveInfinity,
                UseKey = !typing && Plugin.ScienceUseKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.ScienceUseKey.Value),
                Escape = !typing && Input.GetKeyDown(KeyCode.Escape),
            };
            var c = use.Step(frame);

            if ((c & UseChange.Unregister) != 0) Unregister();
            if ((c & UseChange.Register) != 0) Register(screen, me);
            else if (use.Registered && screen != registeredOn) { Unregister(); Register(screen, me); } // the screen was made again
            if ((c & UseChange.Leave) != 0)
            {
                bool held = screen != null && screen.InUse;
                screen?.Leave(); // reports the scan's progress (it stays on the board) and frees the console, if it's ours
                if (held) Plugin.Log.LogInfo(P + (!standing ? "sat down" : frame.Active ? "walked out of reach" : "the console went off") + "; it's free again");
            }
            if ((c & UseChange.Engage) != 0)
            {
                Plugin.Log.LogInfo(P + "in use on the monitor");
            }
            if ((c & UseChange.Disengage) != 0)
            {
                dragging = null;
                // Still walking: the cursor is Bridge Walk's again (sitting down puts it back as the seat had it).
                if (Walker.Standing && !Walker.InVr) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
                Plugin.Log.LogInfo(P + "back to walking");
            }
            if ((c & UseChange.SwallowEscape) != 0) Walker.SwallowEscape = true;
            Walker.UsingConsole = use.Engaged;

            blend = standing ? ConsoleUse.Ease(blend, use.Engaged, Time.unscaledDeltaTime) : 0f;
            if (use.Engaged)
            {
                // Something of the game's may lock it again.
                if (!typing && (Cursor.lockState != CursorLockMode.None || !Cursor.visible)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                if (screen != null) Mouse(screen);
            }
        }

        // ------------------------------------------------------------------------------------------- VR: the hands
        private static void Register(ScienceScreen screen, NovaPlayerInfo me)
        {
            if (screen == null) return;
            ConsoleWidgets.Register(screen.Interactions);
            registeredOn = screen;
            // The hands only look at their list while the player's own console is switched on (HandAnimator.BatchUpdate).
            if (!warnedConsole)
            {
                ConsoleBase own = null;
                try { own = me != null ? me.GHJLPNMIOAJ : null; } catch { } // NovaPlayerInfo.Console
                if (own == null || !own.gameObject.activeSelf)
                {
                    warnedConsole = true;
                    Plugin.Log.LogWarning(P + $"your own console is {(own == null ? "missing" : "switched off")}: the hands may not use the science screen");
                }
            }
            Plugin.Log.LogInfo(P + $"in reach in VR: {screen.Interactions.Count} control(s) for the hands");
        }

        private static void Unregister()
        {
            if (registeredOn != null) ConsoleWidgets.Unregister(registeredOn.Interactions);
            registeredOn = null;
        }

        /// <summary>Bridge Walk's axis block (WalkPatches.NoAxis): the hands' index-pull and grab get through at the console in VR.</summary>
        internal static bool PassesAxis(FEKBGPPHGEP axis)
        {
            if (!use.PassesHandInput) return false;
            return axis == FEKBGPPHGEP.LeftHandIndexPull || axis == FEKBGPPHGEP.RightHandIndexPull
                || axis == FEKBGPPHGEP.LeftHandGrab || axis == FEKBGPPHGEP.RightHandGrab;
        }

        /// <summary>Bridge Walk's action block (WalkPatches.Blocks): the index finger's touch (it points the finger to poke) at the console in VR.</summary>
        internal static bool PassesAction(LONCAPHEAPA action)
        {
            if (!use.PassesHandInput) return false;
            return action == LONCAPHEAPA.ContactLeftIndexTrigger || action == LONCAPHEAPA.ContactRightIndexTrigger;
        }

        // ------------------------------------------------------------------------------------------- monitor: the view
        /// <summary>The 2D camera standing (Walker.EyePose): eased from your eyes to 0.6 m in front of the screen, facing it.</summary>
        internal static void View(ref Vector3 pos, ref Quaternion rot)
        {
            if (blend <= 0f) return;
            var anchor = SciencePlacement.ScreenAnchor;
            if (anchor == null) return;
            float s = ConsoleUse.Smooth(blend);
            // The anchor's +z points into the screen, away from the reader.
            Vector3 eye = anchor.position - anchor.forward * ViewDistance;
            Quaternion look = Quaternion.LookRotation(anchor.forward, anchor.up);
            pos = Vector3.Lerp(pos, eye, s);
            rot = Quaternion.Slerp(rot, look, s);
        }

        // ------------------------------------------------------------------------------------------- monitor: the mouse
        private static void Mouse(ScienceScreen screen)
        {
            var camera = Camera.main;
            if (camera == null) { dragging = null; return; }
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            if (dragging != null)
            {
                if (!Input.GetMouseButton(0) || !dragging.isActiveAndEnabled) { dragging = null; return; }
                Drag(dragging, ray);
                return;
            }
            if (!Input.GetMouseButtonDown(0)) return;
            HandInteraction hit = null;
            float nearest = float.MaxValue;
            foreach (var h in screen.Interactions)
            {
                if (h == null || !h.enabled || !h.gameObject.activeInHierarchy || !h.Interactable) continue;
                // Its mouse collider (made in its Awake, as the game's mouse uses it), else its hand trigger.
                if (!Hits(h.PLIHOHLOJDI, ray, out float d) && !Hits(h.DGPEFNGNJDM, ray, out d)) continue; // MouseTriggerCollider, TriggerCollider
                if (d < nearest) { nearest = d; hit = h; }
            }
            if (hit == null) return;
            if (hit is SliderInteraction slider) { dragging = slider; Drag(slider, ray); return; }
            var button = hit.GetComponentInParent<ConsoleButton>();
            if (button != null) button.Press();
        }

        private static bool Hits(Collider col, Ray ray, out float distance)
        {
            distance = 0f;
            if (col == null || !col.Raycast(ray, out var info, 10f)) return false;
            distance = info.distance;
            return true;
        }

        // The handle follows the mouse along its track: where the ray meets the screen, onto the track.
        private static void Drag(SliderInteraction slider, Ray ray)
        {
            Vector3 min = slider.FCHMAJKOCHF, max = slider.EHMILCABDPH; // MinPosition, MaxPosition
            Vector3 track = max - min;
            if (track.sqrMagnitude < 1e-8f) return;
            var anchor = SciencePlacement.ScreenAnchor;
            Vector3 normal = anchor != null ? anchor.forward : -ray.direction;
            var plane = new Plane(normal, min);
            if (!plane.Raycast(ray, out float enter)) return;
            float t = Mathf.Clamp01(Vector3.Dot(ray.GetPoint(enter) - min, track) / track.sqrMagnitude);
            if (Mathf.Abs(slider.OJKPBPCBPMA - t) > 1e-4f) slider.OJKPBPCBPMA = t; // Value: moves the handle, tells the panel
        }

        // ------------------------------------------------------------------------------------------- monitor: the prompt
        private static GUIStyle style, shadow;

        internal static void OnGUI()
        {
            if (!use.InReach || use.Engaged || use.Vr || TextPrompt.IsOpen || Event.current.type != EventType.Repaint) return;
            var key = Plugin.ScienceUseKey.Value;
            if (key == KeyCode.None) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 22, fontStyle = FontStyle.Bold };
                style.normal.textColor = Color.white;
                shadow = new GUIStyle(style);
                shadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            }
            string text = string.Format(Translation.T("{0}: use science console"), key);
            var r = new Rect(0f, Screen.height * 0.7f, Screen.width, 40f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, shadow);
            GUI.Label(r, text, style);
        }
    }
}
