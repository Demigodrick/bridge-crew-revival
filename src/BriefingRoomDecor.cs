using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The briefing room (Aegis_Briefing_Room), a little livelier. Visual only, on each player's own game:
    ///   - Props the room ships with but keeps switched off: the Saurian brandy and glasses and the platter, on
    ///     the side table.
    ///   - The table hologram (Mdl_AegisBriefingRoom: a slowly spinning Aegis, or the Enterprise / Enterprise-D,
    ///     chosen by ConsoleBriefingRoomShipHolograms) shows the chosen ship: the game's own for its ships, and for a
    ///     class of ours (CustomShip) a lighter copy of it (CustomShip.Hologram) in its own materials, under the same
    ///     spinning mount. The game's own hologram is hidden meanwhile by switching its renderers off and back on, so
    ///     its own ship choice is left alone.
    /// </summary>
    internal static class BriefingRoomDecor
    {
        private const string HologramRoot = "Mdl_AegisBriefingRoom";
        private const float HologramSize = 1.3f; // metres across, about the size of the Aegis hologram

        private static readonly string[] Props =
        {
            "SaurianBrandyBottleNotGlass", "Glasses", "Platter001", // not the StarFleetLogo: unfinished (no material)
        };

        private static float nextCheck;
        private static string shown;          // the class of ours on the table ("Ship:<class>"), or null for the game's own
        private const string ShipKey = "Ship:";
        private static GameObject hologram;   // our ship
        private static readonly List<Renderer> hidden = new List<Renderer>(); // the game's hologram, switched off
        private static readonly List<Light> dimmed = new List<Light>();

        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var root = GameObject.Find(HologramRoot);
            if (root == null)
            {
                shown = null;
                hologram = null;
                placed = false;
                hidden.Clear();
                dimmed.Clear();
                return;
            }
            bool wereOut = ShowProps();
            if (wereOut && !placed) PlaceProps(); // a tick after switching them on, when their bounds are known
            // The table shows the chosen ship: the game's own hologram, or ours (once its lighter copy is made).
            var flown = CustomShip.FlownNow;
            if (flown != null && CustomShip.Campaign) flown = null;
            var pieces = flown?.Hologram;
            string want = flown != null && pieces != null ? ShipKey + flown.Name : null;
            if (want == shown && (want == null || hologram != null)) return;
            Restore();
            shown = want;
            if (want != null) Show(root, flown, pieces);
        }

        // True when every prop was already out (nothing switched on this time).
        private static bool ShowProps()
        {
            bool allOut = true;
            foreach (var name in Props)
            {
                var prop = FindInactive(name);
                if (prop == null) { allOut = false; continue; }
                bool changed = !prop.activeSelf;
                if (changed) prop.SetActive(true);
                // Its renderers may be switched off as well as the object, and it sits on a layer the players'
                // cameras don't draw (13, kept for props shown only at certain moments): onto the side table's.
                int layer = RoomLayer();
                foreach (var r in prop.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) { r.enabled = true; changed = true; }
                    if (layer >= 0 && r.gameObject.layer != layer) { r.gameObject.layer = layer; changed = true; }
                }
                if (changed)
                {
                    allOut = false;
                    foreach (var r in prop.GetComponentsInChildren<Renderer>(true))
                        Plugin.Log.LogInfo($"Briefing room: {name} out: {r.name} at {r.bounds.center}, size {r.bounds.size}, "
                                           + $"scale {prop.transform.lossyScale}, layer {r.gameObject.layer}, material {(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}");
                }
            }
            return allOut;
        }

        private static bool placed;

        // Onto the side table's top (they sit at its middle height, half inside it, until the game would move
        // them): the platter flat on it, the glasses and the bottle on the platter.
        private static void PlaceProps()
        {
            placed = true;
            try
            {
                var table = FindInactive("BriefingRoom_SideTable (3)");
                var top = table == null ? null : table.GetComponentsInChildren<Renderer>(true).FirstOrDefault();
                if (top == null) return;
                var t = top.bounds;
                var cam = Camera.main;
                Plugin.Log.LogInfo($"Briefing room: side table top {t.max.y:0.00} m at ({t.center.x:0.00}, {t.center.z:0.00}); "
                                   + $"camera draws layer 13: {(cam != null ? ((cam.cullingMask & (1 << 13)) != 0).ToString() : "no camera")}");
                float surface = t.max.y + 0.005f;
                surface = Place("Platter001", t.center.x, t.center.z, surface);
                Place("Glasses", t.center.x + 0.1f, t.center.z + 0.06f, surface);
                Place("SaurianBrandyBottleNotGlass", t.center.x - 0.12f, t.center.z - 0.05f, surface);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Briefing room: placing the props: " + e.Message);
            }
        }

        // Moves a prop so its base sits at the given height, centred on (x, z); returns its new top height.
        private static float Place(string name, float x, float z, float bottom)
        {
            var prop = FindInactive(name);
            var renderer = prop == null ? null : prop.GetComponentsInChildren<Renderer>(true).FirstOrDefault();
            if (renderer == null) return bottom;
            var b = renderer.bounds;
            prop.transform.position += new Vector3(x - b.center.x, bottom - b.min.y, z - b.center.z);
            Plugin.Log.LogInfo($"Briefing room: {name} placed at ({x:0.00}, {bottom:0.00}, {z:0.00})");
            return bottom + b.size.y;
        }

        // The layer the room's own furniture is drawn on: the side table's (the props stand on one), -1 if not found.
        private static int RoomLayer()
        {
            var table = FindInactive("BriefingRoom_SideTable (3)") ?? FindInactive("BriefingRoom_SideTable");
            var renderer = table == null ? null : table.GetComponentsInChildren<Renderer>(true).FirstOrDefault();
            return renderer != null ? renderer.gameObject.layer : -1;
        }

        // A root object of the briefing room scene by name, switched off or not (GameObject.Find skips inactive ones).
        private static GameObject FindInactive(string name)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName("Aegis_Briefing_Room");
            if (!scene.isLoaded) return null;
            return scene.GetRootGameObjects().FirstOrDefault(g => g.name == name);
        }

        private static void Show(GameObject root, CustomShip flown, List<KeyValuePair<Mesh, Material>> pieces)
        {
            try
            {
                // The layer the game's hologram is drawn on.
                var aegis = root.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Aegis_Body");
                int layer = aegis != null ? aegis.gameObject.layer : root.layer;

                var ship = CopyShip(flown, pieces, layer);

                hologram = new GameObject("Revival hologram") { layer = layer };
                hologram.transform.SetParent(root.transform, false);
                // Sizes in metres, whatever scale the mount has in the scene.
                float mount = root.transform.lossyScale.x;
                if (mount > 0.0001f) hologram.transform.localScale = Vector3.one / mount;
                // As long as the game's own hologram on the table now (it's switched off below), so ours matches it.
                float size = ShownSize(root);
                Fit(ship, size > 0.05f ? size : HologramSize);
                Plugin.Log.LogInfo($"Briefing room: the table's hologram is {size:0.00} m long; ours sized to match");
                ship.transform.SetParent(hologram.transform, false);

                // The game's own hologram off (renderers and its blinking lights), to put back later.
                foreach (Transform child in root.transform)
                {
                    if (child.gameObject == hologram) continue;
                    foreach (var r in child.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled))
                    {
                        r.enabled = false;
                        hidden.Add(r);
                    }
                    foreach (var l in child.GetComponentsInChildren<Light>(true).Where(l => l.enabled))
                    {
                        l.enabled = false;
                        dimmed.Add(l);
                    }
                }
                Plugin.Log.LogInfo($"Briefing room: hologram shows the {flown.Name}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Briefing room: hologram: " + e.Message);
                Restore();
            }
        }

        private static void Restore()
        {
            if (hologram != null) UnityEngine.Object.Destroy(hologram);
            hologram = null;
            foreach (var r in hidden) if (r != null) r.enabled = true;
            foreach (var l in dimmed) if (l != null) l.enabled = true;
            hidden.Clear();
            dimmed.Clear();
        }

        // A class of ours: its hologram's pieces (in its model space: forward z, up y, as the ships' prefabs) in their own materials.
        private static GameObject CopyShip(CustomShip flown, List<KeyValuePair<Mesh, Material>> pieces, int layer)
        {
            var model = new GameObject(flown.Name) { layer = layer };
            foreach (var p in pieces)
            {
                var part = new GameObject(p.Key.name) { layer = layer };
                part.transform.SetParent(model.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = p.Key;
                var copy = part.AddComponent<MeshRenderer>();
                copy.sharedMaterial = p.Value;
                copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                copy.receiveShadows = false;
            }
            return model;
        }

        // Scale a model so its largest dimension is the given size, centred on its own origin.
        /// <summary>
        /// The length of the game's hologram showing on the table, in metres: its pieces' own sizes (not their world boxes,
        /// which grow as it spins), the largest across them, over the whole of it.
        /// </summary>
        private static float ShownSize(GameObject root)
        {
            float largest = 0f;
            foreach (Transform child in root.transform)
            {
                if (hologram != null && child.gameObject == hologram) continue;
                var filters = child.GetComponentsInChildren<MeshFilter>(false).Where(f => f.sharedMesh != null && f.GetComponent<Renderer>() is Renderer r && r.enabled).ToList();
                if (filters.Count == 0) continue;
                // The pieces' corners in this child's space, then its extent scaled to the world.
                var bounds = new Bounds(); bool first = true;
                foreach (var f in filters)
                    foreach (var corner in Corners(f.sharedMesh.bounds))
                    {
                        var p = child.InverseTransformPoint(f.transform.TransformPoint(corner));
                        if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p);
                    }
                var world = Vector3.Scale(bounds.size, child.lossyScale);
                largest = Mathf.Max(largest, Mathf.Max(world.x, Mathf.Max(world.y, world.z)));
            }
            return largest;
        }

        private static void Fit(GameObject model, float size)
        {
            var renderers = model.GetComponentsInChildren<MeshFilter>();
            var bounds = new Bounds();
            bool first = true;
            foreach (var f in renderers)
            {
                var local = f.sharedMesh.bounds;
                foreach (var corner in Corners(local))
                {
                    var p = f.transform.localPosition + f.transform.localRotation * Vector3.Scale(corner, f.transform.localScale);
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
            }
            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float scale = largest > 0 ? size / largest : 1f;
            model.transform.localScale = Vector3.one * scale;
            model.transform.localPosition = -bounds.center * scale;
        }

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
        }
    }
}
