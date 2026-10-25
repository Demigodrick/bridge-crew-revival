using System;
using System.Collections.Generic;
using System.Linq;
using StbcRevival.Missions;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The briefing room (Aegis_Briefing_Room), a little livelier. Visual only, on each player's own game:
    ///   - Props the room ships with but keeps switched off: the Saurian brandy and glasses, the platter, the
    ///     Starfleet logo.
    ///   - The table hologram (Mdl_AegisBriefingRoom: a slowly spinning Aegis, or the Enterprise / Enterprise-D,
    ///     chosen by ConsoleBriefingRoomShipHolograms) shows the chosen Revival mission's enemy ships instead.
    ///     They're copies of the ship prefabs' visible meshes only (no AI, physics or network parts), in the
    ///     Aegis hologram's material, under the same spinning mount. The game's own hologram is hidden by
    ///     switching its renderers off and back on, so its own ship choice is left alone.
    /// </summary>
    internal static class BriefingRoomDecor
    {
        private const string HologramRoot = "Mdl_AegisBriefingRoom";
        private const float HologramSize = 1.3f; // metres across, about the size of the Aegis hologram

        private static readonly string[] Props =
        {
            "SaurianBrandyBottleNotGlass", "Glasses", "Platter001", "StarFleetLogo",
        };

        // What each Revival mission sends at you: the first ship in the middle, the others either side.
        private static readonly Dictionary<string, string[]> Enemies = new Dictionary<string, string[]>
        {
            [RevivalMissions.LastStand] = new[] { "Sector/Ships/Ship - D5 Cruiser", "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - Pirate A" },
            [RevivalMissions.FleetBattle] = new[] { "Sector/Ships/Ship - Klingon Flagship", "Sector/Ships/Ship - Romulan DDeridex" },
            [RevivalMissions.Patrol] = new[] { "Sector/Ships/Ship - Pirate A", "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - Pirate B" },
            [RevivalMissions.Evacuation] = new[] { "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - Pirate A", "Sector/Ships/Ship - Pirate B" },
        };

        private static float nextCheck;
        private static string shown;          // the mission whose ships are on the table, or null
        private static GameObject hologram;   // our ships
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
                hidden.Clear();
                dimmed.Clear();
                return;
            }
            ShowProps();
            var mission = RevivalBriefing.RoomSelection();
            if (mission == null || !Enemies.ContainsKey(mission)) mission = null;
            if (mission == shown && (mission == null || hologram != null)) return;
            Restore();
            shown = mission;
            if (mission != null) Show(root, mission);
        }

        private static void ShowProps()
        {
            foreach (var name in Props)
            {
                var prop = FindInactive(name);
                if (prop == null) continue;
                bool changed = !prop.activeSelf;
                if (changed) prop.SetActive(true);
                // Its renderers may be switched off as well as the object.
                foreach (var r in prop.GetComponentsInChildren<Renderer>(true).Where(r => !r.enabled))
                {
                    r.enabled = true;
                    changed = true;
                }
                if (changed)
                    foreach (var r in prop.GetComponentsInChildren<Renderer>(true))
                        Plugin.Log.LogInfo($"Briefing room: {name} out: {r.name} at {r.bounds.center}, size {r.bounds.size}, "
                                           + $"scale {prop.transform.lossyScale}, layer {r.gameObject.layer}, material {(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}");
            }
        }

        // A root object of the briefing room scene by name, switched off or not (GameObject.Find skips inactive ones).
        private static GameObject FindInactive(string name)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName("Aegis_Briefing_Room");
            if (!scene.isLoaded) return null;
            return scene.GetRootGameObjects().FirstOrDefault(g => g.name == name);
        }

        private static void Show(GameObject root, string mission)
        {
            try
            {
                // The hologram's look: the Aegis hologram's own material, and its layer.
                var aegis = root.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Aegis_Body");
                var material = aegis != null ? aegis.sharedMaterial : null;
                int layer = aegis != null ? aegis.gameObject.layer : root.layer;

                var ships = new List<GameObject>();
                foreach (var prefab in Enemies[mission])
                {
                    var model = Copy(prefab, material, layer);
                    if (model != null) ships.Add(model);
                }
                if (ships.Count == 0)
                {
                    Plugin.Log.LogWarning($"Briefing room: no ship models for {mission}; the table keeps its own hologram");
                    return;
                }

                hologram = new GameObject("Revival hologram") { layer = layer };
                hologram.transform.SetParent(root.transform, false);
                // Sizes in metres, whatever scale the mount has in the scene.
                float mount = root.transform.lossyScale.x;
                if (mount > 0.0001f) hologram.transform.localScale = Vector3.one / mount;
                // The middle ship biggest, the escorts smaller and either side.
                for (int i = 0; i < ships.Count; i++)
                {
                    var ship = ships[i];
                    float size = i == 0 ? HologramSize : HologramSize * 0.55f;
                    float x = i == 0 ? 0f : (i % 2 == 1 ? -1f : 1f) * HologramSize * 0.75f;
                    float z = i == 0 ? 0f : -HologramSize * 0.25f;
                    Fit(ship, size);
                    ship.transform.SetParent(hologram.transform, false);
                    ship.transform.localPosition += new Vector3(x, 0f, z);
                }

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
                Plugin.Log.LogInfo($"Briefing room: hologram shows {mission}'s ships ({ships.Count})");
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

        // A ship's look, without the ship: its visible meshes (the most detailed level, where it has levels of
        // detail; no shield bubbles or effects), placed as in the prefab, in the hologram material.
        private static GameObject Copy(string path, Material material, int layer)
        {
            var prefab = Resources.Load(path) as GameObject;
            if (prefab == null)
            {
                Plugin.Log.LogWarning("Briefing room: no prefab " + path);
                return null;
            }
            var lods = prefab.GetComponentsInChildren<LODGroup>(true);
            var lower = new HashSet<Renderer>(lods.SelectMany(g => g.GetLODs().Skip(1).SelectMany(l => l.renderers)).Where(r => r != null));
            var model = new GameObject(prefab.name) { layer = layer };
            var toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || filter.sharedMesh == null || lower.Contains(renderer)) continue;
                var name = filter.name.ToLowerInvariant();
                if (new[] { "shield", "collider", "warp", "glow", "flare", "hit", "bubble", "trail" }.Any(name.Contains)) continue;
                var part = new GameObject(filter.name) { layer = layer };
                part.transform.SetParent(model.transform, false);
                var m = toRoot * filter.transform.localToWorldMatrix;
                part.transform.localPosition = m.MultiplyPoint3x4(Vector3.zero);
                part.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                part.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = part.AddComponent<MeshRenderer>();
                copy.sharedMaterials = material != null ? Enumerable.Repeat(material, Math.Max(1, renderer.sharedMaterials.Length)).ToArray() : renderer.sharedMaterials;
                copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                copy.receiveShadows = false;
            }
            if (model.transform.childCount == 0)
            {
                UnityEngine.Object.Destroy(model);
                Plugin.Log.LogWarning("Briefing room: no visible meshes in " + path);
                return null;
            }
            return model;
        }

        // Scale a model so its largest dimension is the given size, centred on its own origin.
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
