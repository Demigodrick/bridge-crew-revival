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
    /// What the game has to offer a galaxy file, read from inside the game. Two uses:
    /// - Build: the Trench's systems, node trees, prefabs (with kinds and readable labels) and tags, for the in-game
    ///   check of a region file (GalaxyRegion). Nothing of it is written out: the galaxy designer reads the game files itself.
    /// - Pictures of the SectorBody prefabs for the designer (GalaxyThumbnails): BepInEx/galaxies/thumbs/*.png and the
    ///   index thumbs.json (ThumbIndex: prefab name → file, kind, label). Only when [Galaxy] PictureKey is set (a
    ///   development setting; None by default, and then nothing is taken): by itself a few seconds into the first
    ///   shuttle visit when there's no current index, and again on the key (on the shuttle or in the briefing room). A run cut short by leaving the shuttle (or a level load) is retried on the next shuttle visit;
    ///   any other end counts as done; at most one automatic attempt a session.
    /// Also FindMapArt and PathOf, for the warp map's pages (WarpPages). Never breaks the game: anything that fails
    /// is logged and left out.
    /// </summary>
    internal static class GalaxyCatalogue
    {
        private static float lastStarted = -10f;

        /// <summary>
        /// The Trench's own system count while a region's systems are appended to its list (set by the region code);
        /// null lists every system. Only the first that many are written, so appended systems never become templates.
        /// </summary>
        internal static int? TrenchCountOverride { get; set; }

        internal static string Folder => Path.Combine(Paths.BepInExRootPath, "galaxies");

        internal static string IndexPath => Path.Combine(Folder, "thumbs.json");

        // The automatic run: checked once per shuttle visit, a few seconds in; over for the session once it's done
        // (or failed for good).
        private const float AutoDelay = 5f;
        private static bool autoDone, autoChecked;
        private static float shuttleSince = -1f, nextAutoLook;
        // Resources.UnloadUnusedAssets wanted, done once no level is loading (dropped if one loads meanwhile).
        private static bool unloadWanted;

        internal static void Update()
        {
            GalaxyThumbnails.Update();
            UnloadWhenSettled();
            Auto();
            var key = Plugin.PictureKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key) || Time.realtimeSinceStartup - lastStarted < 3f) return;
            if (GalaxyThumbnails.Running)
            {
                Plugin.Log.LogInfo("Galaxy: still taking the prefab pictures; key ignored");
                return;
            }
            TakePictures(false);
        }

        /// <summary>
        /// [Galaxy] CatalogueKey was this key's name: its value, once, becomes PictureKey's when that is still None.
        /// The old entry is an orphan (no longer bound); it's dropped from the file afterwards.
        /// </summary>
        internal static void MigrateOldKey(BepInEx.Configuration.ConfigFile config, BepInEx.Configuration.ConfigEntry<KeyCode> pictureKey)
        {
            try
            {
                var old = new BepInEx.Configuration.ConfigDefinition("Galaxy", "CatalogueKey");
                var orphans = Traverse.Create(config).Property("OrphanedEntries").GetValue() as Dictionary<BepInEx.Configuration.ConfigDefinition, string>;
                if (orphans == null || !orphans.TryGetValue(old, out string value)) return;
                orphans.Remove(old);
                if (pictureKey.Value == KeyCode.None && Enum.IsDefined(typeof(KeyCode), value.Trim()))
                {
                    pictureKey.Value = (KeyCode)Enum.Parse(typeof(KeyCode), value.Trim());
                    Plugin.Log.LogInfo($"Galaxy: [Galaxy] CatalogueKey = {value.Trim()} carried over to PictureKey");
                }
                config.Save();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: carrying over CatalogueKey: " + e.Message); }
        }

        private static void UnloadWhenSettled()
        {
            if (!unloadWanted || GalaxyThumbnails.Running) return;
            try
            {
                if (GameState.Loading())
                {
                    unloadWanted = false; // a level load unloads what's unused itself
                    return;
                }
            }
            catch (Exception) { }
            unloadWanted = false;
            try { Resources.UnloadUnusedAssets(); }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: unloading the loaded prefabs: " + e.Message); }
        }

        // Only with [Galaxy] PictureKey set (a development setting): takes the pictures by itself the first time the
        // player is on the shuttle, if there's no current index; never on the bridge or in the briefing room. With
        // the key at None (the default) nothing at all happens here: no catalogue, no file looked at.
        private static void Auto()
        {
            var key = Plugin.PictureKey;
            if (key == null || key.Value == KeyCode.None) return;
            if (autoDone || GalaxyThumbnails.Running || Time.realtimeSinceStartup < nextAutoLook) return;
            nextAutoLook = Time.realtimeSinceStartup + 0.5f;
            bool onShuttle;
            try { onShuttle = GameState.Phase() == "GamePhase_MainMenu" && !GameState.Loading(); }
            catch (Exception) { return; }
            if (!onShuttle)
            {
                shuttleSince = -1f;
                autoChecked = false;
                return;
            }
            if (shuttleSince < 0) shuttleSince = Time.realtimeSinceStartup;
            if (autoChecked || Time.realtimeSinceStartup - shuttleSince < AutoDelay) return;
            autoChecked = true;
            try
            {
                string index = ReadIndex();
                if (ThumbIndex.IsCurrent(index))
                {
                    autoDone = true;
                    Plugin.Log.LogInfo($"Galaxy: the prefab pictures are current (thumbs.json format {ThumbIndex.CurrentFormat}); not taken again");
                    return;
                }
                Plugin.Log.LogInfo("Galaxy: taking the prefab pictures for the galaxy designer by itself (" +
                    (index == null ? "none yet" : "the last run was cut short or is older") + ")");
                TakePictures(true);
            }
            catch (Exception e) { Plugin.Log.LogError("Galaxy: automatic pictures: " + e); }
            // Without a run under way the attempt is over, taken or not; another would fail the same way (no Trench,
            // Build throwing, the run unable to start): not again this session (the key still works). A run under way
            // decides at its end: only leaving the shuttle (or a load) leaves it for the next visit.
            if (!GalaxyThumbnails.Running && !autoDone)
            {
                autoDone = true;
                Plugin.Log.LogInfo("Galaxy: automatic picture attempt over; not tried again this session");
            }
        }

        // auto: the automatic run, which stops if the player leaves the shuttle.
        private static void TakePictures(bool auto)
        {
            lastStarted = Time.realtimeSinceStartup;
            if (!GalaxyThumbnails.CanRun(out string why, auto))
            {
                Plugin.Log.LogInfo($"Galaxy: no prefab pictures: {why}; they're taken on the shuttle or in the briefing room");
                return;
            }
            bool pending = false;
            try
            {
                var trench = FindTrench();
                if (trench == null)
                {
                    Plugin.Log.LogWarning("Galaxy: no Trench sector map found; no prefab pictures");
                    return;
                }
                var catalogue = Build(trench, out var bodies);
                var items = PictureItems(catalogue, bodies);
                pending = GalaxyThumbnails.Start(items, auto, (interrupted, summary) =>
                {
                    Plugin.Log.LogInfo("Galaxy: " + summary);
                    foreach (var item in items) item.Source = null;
                    unloadWanted = true; // once no level is loading
                    // Only leaving (or a load, or the scene going) leaves it for another try; any other end is as
                    // complete as it gets, failures logged above, so it isn't retried every visit.
                    if (!interrupted) autoDone = true;
                    WriteIndex(catalogue, interrupted ? 0 : ThumbIndex.CurrentFormat);
                });
                if (!pending)
                {
                    // It won't start next time either: an index of the pictures already there, as done (the key tries again).
                    Plugin.Log.LogWarning("Galaxy: the prefab pictures could not start (see above)");
                    WriteIndex(catalogue, ThumbIndex.CurrentFormat);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Galaxy: prefab pictures failed: " + e);
            }
            finally
            {
                // While pictures are taken the prefabs are still needed; the run's end unloads them.
                if (!pending) unloadWanted = true;
            }
        }

        // thumbs.json: every prefab with a picture. format 0: a run cut short, taken again next shuttle visit.
        private static void WriteIndex(Catalogue c, int format)
        {
            try
            {
                var index = new ThumbIndex { Format = format };
                foreach (var p in c.Prefabs)
                    if (p.Thumb != null)
                        index.Pictures[p.Name] = new ThumbIndex.Picture { File = p.Thumb, Kind = p.Kind, Label = p.Label };
                Directory.CreateDirectory(Folder);
                // Written to a temporary file and moved into place, so a save cut short (the game closing) leaves the old file.
                string temp = IndexPath + ".tmp";
                File.WriteAllText(temp, index.ToJson(), new UTF8Encoding(false));
                if (File.Exists(IndexPath)) File.Delete(IndexPath);
                File.Move(temp, IndexPath);
                Plugin.Log.LogInfo($"Galaxy: thumbs.json written: {index.Pictures.Count} pictures of {c.Prefabs.Count} prefabs" +
                    (format == 0 ? " (cut short: taken again on the next shuttle visit)" : $" (format {format})"));
            }
            catch (Exception e) { Plugin.Log.LogError("Galaxy: writing thumbs.json: " + e); }
        }

        // thumbs.json's text, or null without one. A save cut short between removing the old file and moving the new
        // one in leaves only thumbs.json.tmp: used if it reads as an index (it was complete), else ignored.
        private static string ReadIndex()
        {
            string temp = IndexPath + ".tmp";
            try
            {
                if (!File.Exists(IndexPath) && File.Exists(temp))
                {
                    string text = File.ReadAllText(temp);
                    ThumbIndex.Parse(text);
                    File.Move(temp, IndexPath);
                    Plugin.Log.LogInfo("Galaxy: thumbs.json restored from its last save (thumbs.json.tmp)");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: thumbs.json.tmp not used: " + e.Message); }
            return File.Exists(IndexPath) ? File.ReadAllText(IndexPath) : null;
        }

        // A picture item per prefab, with its file name; an earlier run's picture kept until a new one is taken.
        private static List<GalaxyThumbnails.Item> PictureItems(Catalogue c, Dictionary<string, Sector.SectorBody> bodies)
        {
            var items = new List<GalaxyThumbnails.Item>();
            var used = new HashSet<string>();
            foreach (var p in c.Prefabs)
            {
                string file = CatalogueNames.UniquePngFile(p.Name, used);
                try { if (File.Exists(Path.Combine(GalaxyThumbnails.Folder, file))) p.Thumb = file; }
                catch (Exception) { }
                bodies.TryGetValue(p.Name, out var body);
                items.Add(new GalaxyThumbnails.Item { Prefab = p, Source = body != null ? body.gameObject : null, File = file });
            }
            return items;
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

        /// <summary>The Trench and every SectorBody prefab under Resources/Sector (the pictures).</summary>
        internal static Catalogue Build(Map.SectorRoot trench, out Dictionary<string, Sector.SectorBody> bodies) =>
            Build(trench, out bodies, null, null);

        /// <summary>
        /// With <paramref name="find"/>: the Trench's prefabs and, of <paramref name="wanted"/>, those the Trench doesn't
        /// have but find does, instead of loading all of Resources/Sector (the region check: GalaxyCheck only asks
        /// about the prefabs the file names, so it rejects the same files). Without: as above.
        /// </summary>
        internal static Catalogue Build(Map.SectorRoot trench, out Dictionary<string, Sector.SectorBody> bodies,
            IEnumerable<string> wanted, Func<string, Sector.SectorBody> find)
        {
            var c = new Catalogue();
            // Prefab name -> body, in the order met; the Trench's first, then the rest of Resources.
            bodies = new Dictionary<string, Sector.SectorBody>();
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
            if (find == null) ResourcePrefabs(bodies, order);
            else FoundPrefabs(bodies, order, wanted, find);
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
                    c.Prefabs.Add(new CataloguePrefab { Name = name, Kind = kind, Radius = radius, Label = Label(body, name) });
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: prefab {name}: {e.Message}"); }
            }

            try { c.Tags.AddRange(Enum.GetNames(typeof(Tag))); }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: tag names: " + e.Message); }
            foreach (var s in c.TrenchSystems)
                if (!string.IsNullOrEmpty(s.Skybox) && !c.Skyboxes.Contains(s.Skybox)) c.Skyboxes.Add(s.Skybox);

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
                    // The Trench as the game ships it, even while a system with additions is swapped for its copy.
                    var root = GalaxyRegion.Original(t.Field("System").GetValue() as Map.SystemRoot);
                    if (root == null) continue;
                    var pos = (Vector3)t.Field("Position").GetValue();
                    var rt = Traverse.Create(root);
                    name = rt.Field("SystemName").GetValue() as string;
                    if (string.IsNullOrEmpty(name)) name = root.name;
                    var system = new CatalogueSystem
                    {
                        Name = name,
                        DisplayName = CatalogueNames.Display(name),
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

        // The body's name as targeting shows it, in English: its TargetableComponent's _uiName ("5796|Space Station"
        // gives "Space Station", the text key's English part, whatever the game's language); else the tidied prefab
        // name ("Target" is the component's default, not a name).
        private static string Label(Sector.SectorBody body, string prefabName)
        {
            try
            {
                var targetable = body.GetComponentInChildren<Sector.TargetableComponent>(true);
                var ui = targetable != null ? Traverse.Create(targetable).Field("_uiName").GetValue() as string : null;
                string text = CatalogueNames.Display(ui);
                if (!string.IsNullOrEmpty(text) && text.Trim() != "Target") return text.Trim();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: name of prefab {prefabName}: {e.Message}"); }
            return CatalogueNames.Tidy(prefabName);
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

        // Of the names asked for, those the Trench doesn't have, looked up one by one; a name not found stays out.
        private static void FoundPrefabs(Dictionary<string, Sector.SectorBody> bodies, List<string> order, IEnumerable<string> wanted,
            Func<string, Sector.SectorBody> find)
        {
            if (wanted == null) return;
            foreach (var name in wanted)
            {
                if (string.IsNullOrEmpty(name) || bodies.ContainsKey(name)) continue;
                try
                {
                    var body = find(name);
                    if (body == null) continue;
                    bodies[name] = body;
                    order.Add(name);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: prefab {name}: {e.Message}"); }
            }
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
    }
}
