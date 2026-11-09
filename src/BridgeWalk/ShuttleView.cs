using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// The shuttle's windows show the outside through a second camera (ShuttleCameraMover): placed once where the
    /// seated player's head is (relative to the shuttle's station) and given the player camera's local rotation every
    /// frame. Standing, your character turns with you, so that local rotation hardly changes and the outside turned
    /// along with your view. While you walk in the shuttle the mover is driven from here instead: at your eyes, turned
    /// as your view is, both relative to the station; sitting down puts it back.
    /// </summary>
    internal static class ShuttleView
    {
        private sealed class Saved { public ShuttleCameraMover Mover; public Vector3 Pos; public Quaternion Rot; }

        private static readonly List<Saved> saved = new List<Saved>();
        internal static bool Active;

        internal static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(ShuttleCameraMover), "BatchLateUpdate");
            if (m == null) { Plugin.Log.LogWarning("Bridge walk: ShuttleCameraMover.BatchLateUpdate not found; the shuttle's outside view won't follow you"); return; }
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(ShuttleView), nameof(SkipWhileWalking))); }
            catch (Exception e) { Plugin.Log.LogError("Bridge walk: patching the shuttle's outside view: " + e.Message); }
        }

        private static bool SkipWhileWalking() => !Active;

        internal static void Capture()
        {
            saved.Clear();
            foreach (var mover in UnityEngine.Object.FindObjectsOfType<ShuttleCameraMover>())
                if (mover != null) saved.Add(new Saved { Mover = mover, Pos = mover.transform.localPosition, Rot = mover.transform.localRotation });
            Active = saved.Count > 0;
            Plugin.Log.LogInfo(Active ? $"Bridge walk: the shuttle's outside view follows you ({saved.Count} camera{(saved.Count == 1 ? "" : "s")})" : "Bridge walk: no outside view camera found in the shuttle");
        }

        internal static void Follow(Transform cam)
        {
            if (!Active || cam == null) return;
            Transform station;
            try { var st = GGPMPCDFHAK.GetStation(0); station = st != null ? st.transform : null; } catch { station = null; }
            if (station == null) return;
            Vector3 pos = station.InverseTransformPoint(cam.position);
            Quaternion rot = Quaternion.Inverse(station.rotation) * cam.rotation;
            foreach (var s in saved)
            {
                if (s.Mover == null) continue;
                s.Mover.transform.localPosition = pos;
                s.Mover.transform.localRotation = rot;
            }
        }

        internal static void Restore()
        {
            foreach (var s in saved)
                if (s.Mover != null) { s.Mover.transform.localPosition = s.Pos; s.Mover.transform.localRotation = s.Rot; }
            saved.Clear();
            Active = false;
        }
    }
}
