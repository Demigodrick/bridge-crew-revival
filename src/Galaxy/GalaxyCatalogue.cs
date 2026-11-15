using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using Mission;
using UnityEngine;
using Tag = BLECEHNPNBH;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// [Galaxy] CatalogueKey (off by default): write what the game has to offer a galaxy file to
    /// BepInEx/galaxies/catalogue.json, for the galaxy designer and the check: the Trench's systems and node trees,
    /// the SectorBody prefabs, the tag names, the player spawn tags and, with a warp panel in the scene (on the
    /// bridge), the warp map's universe bounds and background (trench-map.png). Never breaks the game: anything
    /// that fails is logged and left out.
    /// </summary>
    internal static class GalaxyCatalogue
    {
        private static float lastWritten = -10f;

        internal static string Folder => Path.Combine(Paths.BepInExRootPath, "galaxies");

        internal static void Update()
        {
            var key = Plugin.CatalogueKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key) || Time.realtimeSinceStartup - lastWritten < 3f) return;
            lastWritten = Time.realtimeSinceStartup;
            try
            {
                var trench = FindTrench();
                if (trench == null)
                {
                    Plugin.Log.LogWarning("Galaxy: no Trench sector map found; catalogue not written");
                    return;
                }
                WarpPanel panel = null;
                try { panel = UnityEngine.Object.FindObjectOfType<WarpPanel>(); }
                catch (Exception e) { Plugin.Log.LogWarning("Galaxy: looking for a warp panel: " + e.Message); }
                if (panel == null) Plugin.Log.LogInfo("Galaxy: no warp panel in the scene; universe bounds and background left out");

                Directory.CreateDirectory(Folder);
                var catalogue = Build(trench, panel);
                File.WriteAllText(Path.Combine(Folder, "catalogue.json"), catalogue.ToJson(), new UTF8Encoding(false));
                Plugin.Log.LogInfo($"Galaxy: catalogue written ({catalogue.TrenchSystems.Count} systems, {catalogue.Prefabs.Count} prefabs) to {Folder}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Galaxy: catalogue failed: " + e);
            }
        }

        /// <summary>The loaded map if it's the Trench, else the Trench from the game's Resources.</summary>
        private static Map.SectorRoot FindTrench()
        {
            try
            {
                var loaded = Loader.EJLNDBGLIFL != null ? Loader.EJLNDBGLIFL.GetLoadedMap() : null;
                if (loaded != null && IsTrench(loaded))
                {
                    Plugin.Log.LogInfo($"Galaxy: using the loaded map, {loaded.name}");
                    return loaded;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: the loaded map: " + e.Message); }
            foreach (var map in Resources.LoadAll<Map.SectorRoot>("Mission Data/Maps"))
                if (map != null && IsTrench(map))
                {
                    Plugin.Log.LogInfo($"Galaxy: using {map.name} from the game's Resources (Mission Data/Maps)");
                    return map;
                }
            return null;
        }

        private static bool IsTrench(Map.SectorRoot map) => map.name.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static Catalogue Build(Map.SectorRoot trench, WarpPanel panel)
        {
            var c = new Catalogue();
            // Prefab name -> body, in the order met; the Trench's first, then the rest of Resources.
            var bodies = new Dictionary<string, Sector.SectorBody>();
            var order = new List<string>();
            // Tags the Trench's nodes put on each prefab, for its kind when the prefab's own tags don't say.
            var nodeTags = new Dictionary<string, List<string>>();

            Systems(c, trench, bodies, order, nodeTags);
            ResourcePrefabs(bodies, order);
            foreach (var name in order)
            {
                try
                {
                    var body = bodies[name];
                    double radius = 0;
                    try { radius = body.OOCLPMNONMG; } catch (Exception) { /* not every body knows its size off-scene */ }
                    if (double.IsNaN(radius) || double.IsInfinity(radius)) radius = 0;
                    var tags = TagNames(Traverse.Create(body).Field("PrefabTags").GetValue());
                    string kind = Kind(tags);
                    if (kind == "other" && nodeTags.TryGetValue(name, out var used)) kind = Kind(used);
                    c.Prefabs.Add(new CataloguePrefab { Name = name, Kind = kind, Radius = radius });
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: prefab {name}: {e.Message}"); }
            }

            try { c.Tags.AddRange(Enum.GetNames(typeof(Tag))); }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: tag names: " + e.Message); }
            try
            {
                foreach (var list in new[] { MissionManager.NovaPlayerSpawnDefaultTags, MissionManager.NovaPlayerSpawnAlternateTags })
                    if (list != null)
                        foreach (var t in list)
                            if (!c.SpawnTags.Contains(t.ToString())) c.SpawnTags.Add(t.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: spawn tags: " + e.Message); }
            foreach (var s in c.TrenchSystems)
                if (!string.IsNullOrEmpty(s.Skybox) && !c.Skyboxes.Contains(s.Skybox)) c.Skyboxes.Add(s.Skybox);

            if (panel != null) WarpMap(c, panel);
            return c;
        }

        private static void Systems(Catalogue c, Map.SectorRoot trench, Dictionary<string, Sector.SectorBody> bodies, List<string> order,
            Dictionary<string, List<string>> nodeTags)
        {
            var links = Traverse.Create(trench).Field("Systems").GetValue() as System.Collections.IList;
            if (links == null)
            {
                Plugin.Log.LogWarning("Galaxy: the Trench has no system list");
                return;
            }
            foreach (var link in links)
            {
                string name = "?";
                try
                {
                    var t = Traverse.Create(link);
                    var root = t.Field("System").GetValue() as Map.SystemRoot;
                    if (root == null) continue;
                    var pos = (Vector3)t.Field("Position").GetValue();
                    var rt = Traverse.Create(root);
                    name = rt.Field("SystemName").GetValue() as string;
                    if (string.IsNullOrEmpty(name)) name = root.name;
                    var system = new CatalogueSystem
                    {
                        Name = name,
                        X = pos.x,
                        Z = pos.z,
                        Skybox = rt.Field("_skyboxPath").GetValue() as string,
                        SystemTags = TagNames(rt.Field("SystemTags").GetValue()),
                    };
                    system.Nodes = Nodes(root, name, bodies, order, nodeTags);
                    c.TrenchSystems.Add(system);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: system {name}: {e.Message}"); }
            }
        }

        // The system's node tree, parents before children: the nodes of its SystemTransforms, and their children.
        private static List<GalaxyNode> Nodes(Map.SystemRoot root, string systemName, Dictionary<string, Sector.SectorBody> bodies,
            List<string> order, Dictionary<string, List<string>> nodeTags)
        {
            var all = new List<Map.SystemNode>();
            var transforms = Traverse.Create(root).Field("SystemTransforms").GetValue() as System.Collections.IList;
            if (transforms != null)
                foreach (var st in transforms)
                    if (st != null && Traverse.Create(st).Field("_systemNode").GetValue() is Map.SystemNode n && n != null && !all.Contains(n))
                        all.Add(n);
            // Children not listed as transforms of their own.
            for (int i = 0; i < all.Count; i++)
                foreach (var child in Children(all[i]))
                    if (!all.Contains(child)) all.Add(child);

            var ids = new Dictionary<Map.SystemNode, string>();
            var usedIds = new HashSet<string>();
            for (int i = 0; i < all.Count; i++)
            {
                string id = Slug(all[i].name);
                if (id == "" || usedIds.Contains(id)) id = (id == "" ? "node" : id) + "-" + i;
                usedIds.Add(id);
                ids[all[i]] = id;
            }

            var result = new List<GalaxyNode>();
            var done = new HashSet<Map.SystemNode>();
            foreach (var n in all)
                if (Parent(n) == null || !ids.ContainsKey(Parent(n))) Add(n, null);
            foreach (var n in all) if (!done.Contains(n)) Add(n, null); // a parent loop: shouldn't happen
            return result;

            void Add(Map.SystemNode n, string parent)
            {
                if (!done.Add(n)) return;
                try
                {
                    var t = Traverse.Create(n);
                    var prefab = t.Field("Prefab").GetValue() as Sector.SectorBody;
                    string prefabName = prefab != null ? prefab.name : "";
                    if (prefab != null && !bodies.ContainsKey(prefabName))
                    {
                        bodies[prefabName] = prefab;
                        order.Add(prefabName);
                    }
                    var pos = (Vector3)t.Field("Position").GetValue();
                    var rot = ((Quaternion)t.Field("Rotation").GetValue()).eulerAngles;
                    string unit = t.Field("Scale").GetValue()?.ToString();
                    if (Array.IndexOf(GalaxyFile.Units, unit) < 0)
                    {
                        Plugin.Log.LogWarning($"Galaxy: {systemName}/{n.name}: unknown unit '{unit}', written as Kilometers");
                        unit = "Kilometers";
                    }
                    var tags = TagNames(t.Field("Tags").GetValue());
                    if (prefab != null)
                    {
                        if (!nodeTags.TryGetValue(prefabName, out var list)) nodeTags[prefabName] = list = new List<string>();
                        foreach (var tag in tags) if (!list.Contains(tag)) list.Add(tag);
                    }
                    string name = n.name;
                    if (string.IsNullOrEmpty(name)) name = t.Field("SystemMapName").GetValue() as string;
                    if (string.IsNullOrEmpty(name)) name = prefabName;
                    float landing = (float)t.Field("ImpulseLandingRadius").GetValue();
                    result.Add(new GalaxyNode
                    {
                        Id = ids[n],
                        Parent = parent,
                        Name = string.IsNullOrEmpty(name) ? ids[n] : name,
                        Prefab = prefabName,
                        X = pos.x, Y = pos.y, Z = pos.z,
                        Unit = unit,
                        RotX = rot.x, RotY = rot.y, RotZ = rot.z,
                        Tags = tags,
                        LandingRadius = float.IsNaN(landing) || float.IsInfinity(landing) ? 0 : landing,
                    });
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Galaxy: {systemName}/{n.name}: {e.Message}");
                    return; // its children are written as top-level nodes below
                }
                foreach (var child in Children(n))
                    if (ids.ContainsKey(child)) Add(child, ids[n]);
            }
        }

        private static Map.SystemNode Parent(Map.SystemNode n) => Traverse.Create(n).Field("_parent").GetValue() as Map.SystemNode;

        private static List<Map.SystemNode> Children(Map.SystemNode n)
        {
            var result = new List<Map.SystemNode>();
            if (Traverse.Create(n).Field("_children").GetValue() is System.Collections.IList children)
                foreach (var child in children)
                    if (child is Map.SystemNode c && c != null) result.Add(c);
            return result;
        }

        // Every SectorBody prefab under the game's Resources/Sector, after the Trench's own.
        private static void ResourcePrefabs(Dictionary<string, Sector.SectorBody> bodies, List<string> order)
        {
            try
            {
                int before = order.Count;
                foreach (var go in Resources.LoadAll<GameObject>("Sector"))
                {
                    if (go == null) continue;
                    var body = go.GetComponent<Sector.SectorBody>();
                    if (body == null || bodies.ContainsKey(go.name)) continue;
                    bodies[go.name] = body;
                    order.Add(go.name);
                }
                Plugin.Log.LogInfo($"Galaxy: {order.Count - before} more SectorBody prefabs under Resources/Sector");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: prefabs under Resources/Sector: " + e.Message); }
        }

        // A NovaTagList's names: the serialized names, else the parsed tags.
        private static List<string> TagNames(object tagList)
        {
            var result = new List<string>();
            if (tagList == null) return result;
            var t = Traverse.Create(tagList);
            if (t.Field("_tagNames").GetValue() is string[] names && names.Length > 0)
            {
                foreach (var n in names) if (!string.IsNullOrEmpty(n) && !result.Contains(n)) result.Add(n);
                return result;
            }
            if (t.Field("_tags").GetValue() is Array tags)
                foreach (var tag in tags) if (tag != null && !result.Contains(tag.ToString())) result.Add(tag.ToString());
            return result;
        }

        private static string Kind(List<string> tags)
        {
            if (tags.Contains(nameof(Tag.Star))) return "star";
            if (tags.Contains(nameof(Tag.SpaceStation))) return "station";
            if (tags.Contains(nameof(Tag.Moon))) return "moon";
            if (tags.Contains(nameof(Tag.Planet))) return "planet";
            if (tags.Contains(nameof(Tag.Asteroid))) return "asteroids";
            if (tags.Contains(nameof(Tag.Nebula))) return "nebula";
            return "other";
        }

        private static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in (name ?? "").ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        // The warp map's universe bounds (as UniverseToMapPosition uses them) and its background as a PNG.
        private static void WarpMap(Catalogue c, WarpPanel panel)
        {
            try
            {
                var t = Traverse.Create(panel);
                c.UniverseWidth = (float)t.Field("_universeWidth").GetValue();
                c.UniverseHeight = (float)t.Field("_universeHeight").GetValue();
                c.UniverseMinX = (float)t.Field("NOHNHPFPNOD").GetValue();
                c.UniverseMinZ = (float)t.Field("CJBBAHGEKBL").GetValue();
                Plugin.Log.LogInfo($"Galaxy: universe from x {c.UniverseMinX}, z {c.UniverseMinZ}, {c.UniverseWidth} x {c.UniverseHeight}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: universe bounds: " + e.Message); }
            try
            {
                var mapBase = Traverse.Create(panel).Field("_mapBase").GetValue() as GameObject;
                var image = mapBase != null ? mapBase.GetComponent<UnityEngine.UI.Image>() : null;
                var sprite = image != null ? image.sprite : null;
                if (sprite == null || sprite.texture == null)
                {
                    Plugin.Log.LogWarning("Galaxy: the warp map has no background sprite; no trench-map.png");
                    return;
                }
                var png = SpritePng(sprite);
                File.WriteAllBytes(Path.Combine(Folder, "trench-map.png"), png);
                c.Background = "trench-map.png";
                Plugin.Log.LogInfo($"Galaxy: warp map background written ({sprite.name}, {png.Length} bytes)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: no trench-map.png: " + e.Message); }
        }

        // The texture may not be readable: copy it through a RenderTexture, then crop to the sprite's rect.
        private static byte[] SpritePng(Sprite sprite)
        {
            var texture = sprite.texture;
            var rect = sprite.textureRect;
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                int x = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, texture.width - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, texture.height - 1);
                int w = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, texture.width - x);
                int h = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, texture.height - y);
                copy = new Texture2D(w, h, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(x, y, w, h), 0, 0);
                copy.Apply();
                return copy.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }
    }
}
