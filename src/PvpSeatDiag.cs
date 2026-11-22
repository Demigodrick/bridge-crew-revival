using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP lobby: which way you face after a seat change. The game turns your body (the pawn) to the chair's facing on
    /// every seat change (NovaSpawnManager.ApplyCharacterSeatingOffset on the host, NetworkDeserialize on a crew
    /// member's game) and the view (the camera, the pawn's child) back to straight ahead when the console changes
    /// (FPSCamera.Reset). Players sometimes came out of a seat change at the other table turned the wrong way, so each
    /// seat change in a PvP room's briefing room logs the chair's, your body's and your view's facing (compass
    /// degrees), at once and again 2 s later (after the fade); and any time your view is turned more than 100° from
    /// your chair's facing for 2 s in a row, it logs that once per seat.
    /// </summary>
    internal static class PvpSeatDiag
    {
        private static Object lastStation;
        private static float resetAt = -1f;
        private static readonly System.Reflection.MethodInfo CameraReset = HarmonyLib.AccessTools.Method(typeof(SVR.FPSCamera), "Reset");
        private static float recheckAt = -1f, awaySince = -1f, nextLook;
        private static bool toldAway;

        internal static void Update()
        {
            if (!PvpLobby.InPvpRoom || GamePhaseManager.IEFBKOMBFGO == null
                || GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() != typeof(GamePhase_BriefingRoom))
            {
                lastStation = null;
                return;
            }
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 0.25f;
            var me = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC as NovaPlayerInfo;
            var station = me == null ? null : me.Station as NovaStation;
            if (station == null) return;
            if (resetAt > 0f && Time.unscaledTime >= resetAt)
            {
                resetAt = -1f;
                ResetView(me);
            }
            if (!ReferenceEquals(station, lastStation))
            {
                bool first = lastStation == null;
                lastStation = station;
                toldAway = false;
                awaySince = -1f;
                resetAt = Time.unscaledTime + 0.5f; // once the game has seated you there
                if (!first)
                {
                    Plugin.Log.LogInfo("PvP: [seat] now at " + Describe(me, station));
                    recheckAt = Time.unscaledTime + 2f;
                }
                return;
            }
            if (recheckAt > 0f && Time.unscaledTime >= recheckAt)
            {
                recheckAt = -1f;
                Plugin.Log.LogInfo("PvP: [seat] 2 s later at " + Describe(me, station));
            }
            var view = Camera.main;
            if (view == null || toldAway) return;
            float off = Mathf.Abs(Mathf.DeltaAngle(Yaw(station.transform), Yaw(view.transform)));
            if (off <= 100f) { awaySince = -1f; return; }
            if (awaySince < 0f) { awaySince = Time.unscaledTime; return; }
            if (Time.unscaledTime - awaySince < 2f) return;
            toldAway = true;
            Plugin.Log.LogInfo($"PvP: [seat] your view is turned {off:0}° from your chair's facing at " + Describe(me, station));
            StraightenRig(me, view.transform.parent); // if it's the rig that's turned, not your looking around
        }

        // The desktop camera keeps its own turn (FPSCamera: mouse look) and puts it straight only when your console
        // changes (Reset, on PlayerConsoleChangedEvent); a PvP lobby seat change or a join didn't always count, so a
        // view turned before stayed turned in the new chair: seen as "view 240°, body 135°, local turn 105°".
        private static void ResetView(NovaPlayerInfo me)
        {
            try
            {
                var camera = me?.PawnObject == null ? null : me.PawnObject.GetComponentInChildren<SVR.FPSCamera>(true);
                if (camera == null || CameraReset == null) return;
                StraightenRig(me, camera.transform.parent);
                CameraReset.Invoke(camera, null);
                Plugin.Log.LogInfo("PvP: [seat] view put straight for the new chair");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("PvP: [seat] putting the view straight: " + (e.InnerException ?? e).Message); }
        }

        // The camera's rig (Character Camera Root, the FPSCamera's parent) is only ever placed by the game (its
        // position each frame), never turned. After a move from one table to the other it was seen facing away from
        // the body: "chair 315°, body 315°, view 135°, local turn 0°", and putting the camera straight inside it
        // left you facing the wall. Turned back to the chair's facing when it's more than 90° off.
        private static void StraightenRig(NovaPlayerInfo me, Transform rig)
        {
            var station = me?.Station as NovaStation;
            if (rig == null || station == null) return;
            float off = Mathf.DeltaAngle(Yaw(rig), Yaw(station.transform));
            if (Mathf.Abs(off) <= 90f) return;
            string parent = rig.parent == null ? "nothing" : $"'{rig.parent.name}' {Yaw(rig.parent):0}°";
            rig.Rotate(station.transform.up, off, Space.World);
            Plugin.Log.LogWarning($"PvP: [seat] the camera rig '{rig.name}' faced {off:0}° away from your chair (under {parent}); turned back to it");
        }

        private static string Describe(NovaPlayerInfo me, NovaStation station)
        {
            var pawn = me.PawnObject;
            var view = Camera.main;
            string body = pawn == null ? "no body" : $"body {Yaw(pawn.transform):0}° at {pawn.transform.position}";
            var rig = view == null ? null : view.transform.parent;
            string look = view == null ? "no camera" : $"view {Yaw(view.transform):0}° (camera '{view.name}' under '{(rig == null ? "nothing" : rig.name)}' {(rig == null ? 0f : Yaw(rig)):0}° in '{(rig == null || rig.parent == null ? "nothing" : rig.parent.name)}', local turn {view.transform.localEulerAngles.y:0}°)";
            return $"{station.name}: chair {Yaw(station.transform):0}° at {station.transform.position}; {body}; {look}";
        }

        private static float Yaw(Transform t)
        {
            var forward = t.forward;
            forward.y = 0f;
            return forward.sqrMagnitude < 1e-6f ? 0f : Quaternion.LookRotation(forward).eulerAngles.y;
        }
    }
}
