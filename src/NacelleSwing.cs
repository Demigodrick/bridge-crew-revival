using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Variable-geometry nacelles (the Voyager's): a side's nacelle and pylon pieces hang under a pivot at the pylon's
    /// root, which swings them up by an angle when the ship goes to warp and back down when it drops out. The warp's
    /// phase comes from the game's warp-state event (SimulationSpace.OnWarpStateChanged): up while accelerating and
    /// flying, down while decelerating and after.
    /// </summary>
    internal sealed class NacelleSwing : MonoBehaviour
    {
        public float Angle = 25f;      // degrees up at warp
        public float Seconds = 2f;     // to swing all the way
        private float sign;            // which way about the pivot's length axis is up for this side
        private float at;              // 0 down .. 1 up

        // The game's warp phase (warp is the whole sector's), for the scene it was in: a new mission (a new scene) starts
        // with the nacelles down even if the last one ended at warp.
        private static bool raised;
        private static int raisedIn = -1;
        private static int Scene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetHashCode();
        internal static bool Raised => raised && raisedIn == Scene;

        private void Start()
        {
            // Up is the way that lifts this side's pieces' middle, in the ship's own axes (the pivot's, as it hangs
            // unturned in the model): however the ship lies in space as it appears.
            transform.localRotation = Quaternion.identity;
            var middle = Vector3.zero; int count = 0;
            foreach (var r in GetComponentsInChildren<Renderer>(true)) { middle += transform.InverseTransformPoint(r.bounds.center); count++; }
            if (count > 0) middle /= count;
            var turned = Quaternion.AngleAxis(Angle, Vector3.forward) * middle;
            sign = turned.y > middle.y ? 1f : -1f;
            at = Raised ? 1f : 0f;
            Apply();
        }

        private void Update()
        {
            float want = Raised ? 1f : 0f;
            if (Mathf.Approximately(at, want)) return;
            at = Mathf.MoveTowards(at, want, Time.deltaTime / Mathf.Max(0.1f, Seconds));
            Apply();
        }

        private void Apply()
        {
            float t = at * at * (3f - 2f * at); // eased
            transform.localRotation = Quaternion.AngleAxis(sign * Angle * t, Vector3.forward);
        }

        // ---- the game's warp phase ----

        internal static void Patch(Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("SimulationSpace");
                var method = type != null ? AccessTools.Method(type, "CPHKHCOGNKB") : null;
                if (method == null) { Plugin.Log.LogWarning("Nacelles: the warp-state event wasn't found"); return; }
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(NacelleSwing), nameof(WarpState)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Nacelles: " + e.Message); }
        }

        private static void WarpState(object __0)
        {
            try
            {
                if (__0 == null) return;
                string phase = null;
                foreach (var f in __0.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType.IsEnum && Enum.IsDefined(f.FieldType, "Flying")) { phase = f.GetValue(__0)?.ToString(); break; }
                if (phase == null)
                    foreach (var p in __0.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (p.PropertyType.IsEnum && Enum.IsDefined(p.PropertyType, "Flying") && p.GetIndexParameters().Length == 0) { phase = p.GetValue(__0, null)?.ToString(); break; }
                if (phase == null) return;
                bool up = phase == "Accelerating" || phase == "Flying";
                if (up != Raised) Plugin.Log.LogInfo($"Nacelles: warp {phase}: {(up ? "up" : "down")}");
                raised = up; raisedIn = Scene;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Nacelles: warp state: " + e.Message); }
        }
    }
}
