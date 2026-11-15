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
    /// bridge), the warp map's universe bounds and background (trench-map.png), each system's families with their
    /// Points of Interest, and the sprite names nodes use. Never breaks the game: anything that fails is logged and
    /// left out.
    /// </summary>
    internal static class GalaxyCatalogue
    {
        private static float lastWritten = -10f;

        /// <summary>
        /// The Trench's own system count while a region's systems are appended to its list (set by the region code);
        /// null lists every system. Only the first that many are written, so appended systems never become templates.
        /// </summary>
        internal static int? TrenchCountOverride { get; set; }

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
                int families = 0;
                foreach (var s in catalogue.TrenchSystems) families += s.Families.Count;
                Plugin.Log.LogInfo($"Galaxy: catalogue written ({catalogue.TrenchSystems.Count} systems, {families} families, " +
                    $"{catalogue.Prefabs.Count} prefabs, {catalogue.Sprites.Count} sprites) to {Folder}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Galaxy: catalogue failed: " + e);
            }
            // LoadAll("Sector") loaded every prefab under it; let go of the ones the scene doesn't use.
            try { Resources.UnloadUnusedAssets(); }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: unloading the loaded prefabs: " + e.Message); }
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

            try
            {
                foreach (var list in new[] { MissionManager.NovaPlayerSpawnDefaultTags, MissionManager.NovaPlayerSpawnAlternateTags })
                    if (list != null)
                        foreach (var t in list)
                            if (!c.SpawnTags.Contains(t.ToString())) c.SpawnTags.Add(t.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: spawn tags: " + e.Message); }

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
            int count = links.Count;
            if (TrenchCountOverride.HasValue && TrenchCountOverride.Value < count)
            {
                Plugin.Log.LogInfo($"Galaxy: listing the Trench's own {TrenchCountOverride.Value} of {count} systems");
                count = Math.Max(0, TrenchCountOverride.Value);
            }
            // Prefab name -> the prefab's own tags, which the game adds to its nodes' tags.
            var prefabTags = new Dictionary<string, List<string>>();
            for (int li = 0; li < count; li++)
            {
                var link = links[li];
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
                    system.Nodes = Nodes(root, name, bodies, order, nodeTags, c.Sprites);
                    system.Families = CatalogueFamilies.From(system.Nodes, prefab => PrefabTags(prefab, bodies, prefabTags), c.SpawnTags);
                    c.TrenchSystems.Add(system);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: system {name}: {e.Message}"); }
            }
        }

        // The system's node tree, parents before children: the nodes of its SystemTransforms, and their children.
        private static List<GalaxyNode> Nodes(Map.SystemRoot root, string systemName, Dictionary<string, Sector.SectorBody> bodies,
            List<string> order, Dictionary<string, List<string>> nodeTags, List<string> sprites)
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
                if (id == "" || usedIds.Contains(id))
                {
                    string stem = id == "" ? "node" : id;
                    int k = i;
                    do id = stem + "-" + k++; while (usedIds.Contains(id));
                }
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
                    string mapName = t.Field("SystemMapName").GetValue() as string;
                    string background = SpriteName(t.Field("FamilyMapBackground").GetValue() as Sprite, sprites);
                    string icon = SpriteName(t.Field("SystemMapIconSprite").GetValue() as Sprite, sprites);
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
                        MapName = string.IsNullOrEmpty(mapName) ? null : mapName,
                        FamilyBackground = background,
                        MapIcon = icon,
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

        // The sprite's name, added to the catalogue's sprite list; null for none.
        private static string SpriteName(Sprite sprite, List<string> sprites)
        {
            if (sprite == null || string.IsNullOrEmpty(sprite.name)) return null;
            if (!sprites.Contains(sprite.name)) sprites.Add(sprite.name);
            return sprite.name;
        }

        private static List<string> PrefabTags(string prefab, Dictionary<string, Sector.SectorBody> bodies, Dictionary<string, List<string>> cache)
        {
            if (cache.TryGetValue(prefab, out var tags)) return tags;
            tags = new List<string>();
            try
            {
                if (bodies.TryGetValue(prefab, out var body) && body != null)
                    tags = TagNames(Traverse.Create(body).Field("PrefabTags").GetValue());
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: tags of prefab {prefab}: {e.Message}"); }
            cache[prefab] = tags;
            return tags;
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
                var art = FindMapArt(panel);
                if (art == null)
                {
                    Plugin.Log.LogWarning("Galaxy: no image with a sprite or texture covers the warp map; no trench-map.png");
                    return;
                }
                byte[] png;
                string what;
                if (art is UnityEngine.UI.Image image)
                {
                    png = TexturePng(image.sprite.texture, image.sprite.textureRect);
                    what = "sprite " + image.sprite.name;
                }
                else
                {
                    var raw = (UnityEngine.UI.RawImage)art;
                    var tex = raw.texture;
                    var uv = raw.uvRect;
                    // uvRect is in 0..1 of the texture; anything outside (tiling) is clamped by TexturePng.
                    png = TexturePng(tex, new Rect(uv.x * tex.width, uv.y * tex.height, uv.width * tex.width, uv.height * tex.height));
                    what = $"texture {tex.name} ({tex.GetType().Name}), uv {uv}";
                }
                File.WriteAllBytes(Path.Combine(Folder, "trench-map.png"), png);
                c.Background = "trench-map.png";
                Plugin.Log.LogInfo($"Galaxy: warp map background written from {PathOf(art.transform)}: {what}, {png.Length} bytes");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: no trench-map.png: " + e.Message); }
        }

        // The warp map's art: of every Image (with a sprite) and RawImage (with a texture) in the warp panel and its
        // canvas, inactive ones too, the one whose rectangle best matches _mapRect. Matching is the overlap's share
        // of both rectangles together (intersection over union), so a full-screen frame that also covers the map
        // loses to a picture the size of the map; it must cover at least half of the map. Every candidate is logged.
        // Also used by the warp map's second page (WarpPages) to darken the art, with log off.
        // panelOnly: only the warp panel's own subtree, not its canvas (a console canvas can hold other maps' art).
        internal static UnityEngine.UI.Graphic FindMapArt(WarpPanel panel, bool log = true, bool panelOnly = false)
        {
            var mapRect = Traverse.Create(panel).Field("_mapRect").GetValue() as RectTransform;
            if (mapRect == null)
            {
                Plugin.Log.LogWarning("Galaxy: the warp panel has no _mapRect");
                return null;
            }
            var map = WorldRectIn(mapRect, mapRect);
            if (log) Plugin.Log.LogInfo($"Galaxy: warp map rect {PathOf(mapRect)}, {map.width:0.#} x {map.height:0.#}");
            if (map.width <= 0 || map.height <= 0) return null;

            var searched = new List<Transform> { panel.transform };
            if (!panelOnly)
            {
                try
                {
                    var canvas = panel.GetComponentInParent<Canvas>();
                    if (canvas != null) searched.Add(canvas.rootCanvas != null ? canvas.rootCanvas.transform : canvas.transform);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Galaxy: the warp panel's canvas: " + e.Message); }
            }

            var seen = new HashSet<UnityEngine.UI.Graphic>();
            UnityEngine.UI.Graphic best = null;
            float bestScore = 0;
            foreach (var root in searched)
                foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                {
                    if (g == null || !seen.Add(g)) continue;
                    string art;
                    if (g is UnityEngine.UI.Image img)
                    {
                        if (img.sprite == null || img.sprite.texture == null) continue;
                        art = $"sprite {img.sprite.name} ({img.sprite.texture.width}x{img.sprite.texture.height})";
                    }
                    else if (g is UnityEngine.UI.RawImage raw)
                    {
                        if (raw.texture == null) continue;
                        art = $"texture {raw.texture.name} ({raw.texture.GetType().Name} {raw.texture.width}x{raw.texture.height}), uv {raw.uvRect}";
                    }
                    else continue;
                    var r = WorldRectIn(g.rectTransform, mapRect);
                    float overlap = Overlap(r, map);
                    float union = r.width * r.height + map.width * map.height - overlap;
                    float covers = overlap / (map.width * map.height);
                    float score = union > 0 ? overlap / union : 0;
                    if (log) Plugin.Log.LogInfo($"Galaxy: warp map candidate {PathOf(g.transform)}: {r.width:0.#} x {r.height:0.#}, " +
                        $"covers {covers:P0} of the map, match {score:0.00}, {(g.gameObject.activeInHierarchy ? "active" : "inactive")}, {art}");
                    if (covers >= 0.5f && score > bestScore)
                    {
                        best = g;
                        bestScore = score;
                    }
                }
            return best;
        }

        // A RectTransform's rectangle in another's local space (the bounds of its four corners).
        private static Rect WorldRectIn(RectTransform rect, RectTransform space)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var corner in corners)
            {
                var p = space.InverseTransformPoint(corner);
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static float Overlap(Rect a, Rect b)
        {
            float w = Math.Min(a.xMax, b.xMax) - Math.Max(a.xMin, b.xMin);
            float h = Math.Min(a.yMax, b.yMax) - Math.Max(a.yMin, b.yMin);
            return w > 0 && h > 0 ? w * h : 0;
        }

        internal static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null && parts.Count < 12; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        // The texture may not be readable: copy it through an sRGB RenderTexture (so colours come out as stored),
        // then crop to the given rectangle in pixels.
        private static byte[] TexturePng(Texture texture, Rect rect)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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
