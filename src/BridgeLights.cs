using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// Lights in bridge layouts (BridgeLayout). A bridge's own lights are changed like any other object, by their path:
    /// moved and turned with the change (or with a group they're in), off when the change hides them, and a change to
    /// the light itself can also set its colour, intensity and range ("light": { "color": [r, g, b], "intensity",
    /// "range" }). Only the real-time ones do anything: most of a bridge's light is baked into its lightmaps and light
    /// probes, which stay as they were. A light the layout switches off is kept off (the game's scripts turn some on
    /// and off).
    ///
    /// Added lights (the layout's "lights": { "id", "pivot", "move", "color", "intensity", "range" }) are real-time
    /// point lights, without shadows, at pivot + move, lighting what the bridge's own real-time lights light.
    /// Layouts without them, and versions of the mod before them, are unaffected.
    /// </summary>
    internal static class BridgeLights
    {
        internal const string AddedName = "RevivalAddedLight";

        /// <summary>What a layout sets on a light (anything left null stays as the game has it).</summary>
        internal sealed class Settings
        {
            public Color? Color;
            public float? Intensity, Range;

            public static Settings Parse(object v, string what)
            {
                if (v == null) return null;
                if (!(v is Dictionary<string, object> o)) throw new FormatException($"{what}: light isn't an object");
                var s = new Settings();
                if (o.TryGetValue("color", out var c) && c != null)
                {
                    if (!(c is List<object> a) || a.Count < 3 || !(a[0] is double) || !(a[1] is double) || !(a[2] is double))
                        throw new FormatException($"{what}: light colour isn't [r, g, b]");
                    s.Color = new Color(Clamp01((double)a[0]), Clamp01((double)a[1]), Clamp01((double)a[2]), 1f);
                }
                if (o.TryGetValue("intensity", out var i) && i is double id) s.Intensity = Mathf.Clamp((float)id, 0f, 8f);
                if (o.TryGetValue("range", out var r) && r is double rd) s.Range = Mathf.Clamp((float)rd, 0.05f, 50f);
                return s;
            }

            private static float Clamp01(double x) => Mathf.Clamp01((float)x);

            public void On(Light l)
            {
                if (Color.HasValue) l.color = Color.Value;
                if (Intensity.HasValue) l.intensity = Intensity.Value;
                if (Range.HasValue) l.range = Range.Value;
            }
        }

        /// <summary>A light's own values, to put back.</summary>
        internal struct Saved
        {
            public Light Light;
            public bool Enabled;
            public Color Color;
            public float Intensity, Range;

            public static Saved Of(Light l) => new Saved { Light = l, Enabled = l.enabled, Color = l.color, Intensity = l.intensity, Range = l.range };

            public void PutBack()
            {
                if (Light == null) return;
                Light.enabled = Enabled;
                Light.color = Color;
                Light.intensity = Intensity;
                Light.range = Range;
            }
        }

        internal sealed class Added
        {
            public string Id;
            public Vector3 Pivot, Move;
            public Settings Settings;
        }

        /// <summary>The layout's added lights (none if it has no "lights").</summary>
        internal static List<Added> ParseAdded(Dictionary<string, object> root, Func<Dictionary<string, object>, string, string, Vector3> vec)
        {
            var list = new List<Added>();
            if (!root.TryGetValue("lights", out var v) || v == null) return list;
            if (!(v is List<object> items)) throw new FormatException("lights isn't a list");
            int k = 0;
            foreach (var item in items)
            {
                k++;
                if (!(item is Dictionary<string, object> o)) throw new FormatException($"light {k} isn't an object");
                list.Add(new Added
                {
                    Id = o.TryGetValue("id", out var id) && id is string ids ? ids : k.ToString(),
                    Pivot = vec(o, "pivot", $"light {k}"),
                    Move = vec(o, "move", $"light {k}"),
                    Settings = Settings.Parse(o, $"light {k}"),
                });
            }
            return list;
        }

        private static readonly List<GameObject> made = new List<GameObject>();

        /// <summary>Makes the added lights in the scene (taking away any made before).</summary>
        internal static void Start(List<Added> lights, Scene scene)
        {
            Clear();
            if (lights.Count == 0) return;
            int mask = BridgeMask(scene);
            foreach (var a in lights)
            {
                var go = new GameObject(AddedName + " " + a.Id);
                SceneManager.MoveGameObjectToScene(go, scene);
                go.transform.position = a.Pivot + a.Move;
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.Auto;
                l.cullingMask = mask;
                l.color = Color.white;
                l.intensity = 1.5f;
                l.range = 3f;
                a.Settings?.On(l);
                made.Add(go);
            }
            Plugin.Log.LogInfo($"Bridge layout: {lights.Count} light(s) added (lighting layers {mask:X8})");
        }

        internal static void Clear()
        {
            foreach (var go in made) if (go != null) UnityEngine.Object.Destroy(go);
            made.Clear();
        }

        // Which layers the bridge's own real-time point lights light (most of them light the same): everything if none.
        private static int BridgeMask(Scene scene)
        {
            var count = new Dictionary<int, int>();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.gameObject.scene != scene || l.type != LightType.Point || l.name.StartsWith(AddedName, StringComparison.Ordinal)) continue;
                count[l.cullingMask] = count.TryGetValue(l.cullingMask, out var n) ? n + 1 : 1;
            }
            int best = ~0, most = 0;
            foreach (var kv in count) if (kv.Value > most) { most = kv.Value; best = kv.Key; }
            return best;
        }
    }
}
