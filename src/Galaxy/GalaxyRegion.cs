using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using StbcRevival.Missions;
using UnityEngine;
using Tag = BLECEHNPNBH;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// [Galaxy] Region: the systems of a galaxy file (BepInEx/galaxies/&lt;Region&gt;.json) added after the Trench's own,
    /// for Revival Patrol and Free Roam on the Trench.
    ///
    /// The hook is a prefix on SectorMap.AssignSystemIds (called from SectorMap.Awake when the bridge instantiates the
    /// map), with an InitAllSystems prefix as the fallback: the earliest point both the numbering and the system build
    /// see (spike notes, "Changes implied" 1). The Trench's SectorRoot is an asset that stays loaded between missions, so
    /// its list is set before every mission on it: the Trench's own systems, then the region's when it's on
    /// (RegionLinks). Appending keeps every Trench system id and SectorId.
    ///
    /// Whether it's on is decided on every PC from the same inputs (GalaxyRoom: the room's GH, every player's GH, the
    /// room's mission), so every PC builds the same list. A player joining a mission already running follows the host's
    /// published decision (GalaxyRoom's GA) instead, and leaves the crew if they can't build the region.
    ///
    /// Building (spike notes 2-4): each region system is an Object.Instantiate of its template SystemRoot with a node
    /// tree of its own: a copy of the template's ROOT node, then each file node, either a deep copy of a Trench family
    /// (copyFamily) or a new SystemNode, each with a new SystemTransform. Everything is checked first (GalaxyCheck
    /// against a catalogue of the Trench in memory); any problem and the whole file is skipped, the Trench untouched.
    /// </summary>
    internal static class GalaxyRegion
    {
        private const string P = "Galaxy region: ";

        /// <summary>The Trench's own system count once seen; region system ids start here. Before that, no id is a
        /// region system's.</summary>
        internal static int TrenchCount => trenchOwn != null ? trenchOwn.Count : int.MaxValue;

        /// <summary>The region's systems are in the Trench's list for the mission being built or flown.</summary>
        internal static bool Active { get; private set; }

        /// <summary>The region's name from its file, else the config value.</summary>
        internal static string RegionName => GalaxyRoom.OwnName ?? (!string.IsNullOrEmpty(builtName) ? builtName : ConfigName);

        internal static string ConfigName => (Plugin.GalaxyRegionName?.Value ?? "").Trim();

        // The Trench asset seen, its own links (captured before anything was appended) and the region built for it.
        private static Map.SectorRoot keptFor;
        private static List<Map.SystemLink> trenchOwn;
        private static List<Map.SystemLink> regionLinks;
        private static string builtHash;     // the file hash the links were built from
        private static string builtName;
        private static string failedHash;    // a file that failed the check or the build: not tried again
        private static object decidedFor;    // the SectorMap the last decision was made for
        private static bool decidedOn;

        internal static void Patch(Harmony harmony)
        {
            var sectorMap = typeof(Sector.SectorMap);
            var assign = AccessTools.Method(sectorMap, "LGAFCFEMMDA") ?? AccessTools.Method(sectorMap, "AssignSystemIds");
            var init = AccessTools.Method(sectorMap, "InitAllSystems");
            if (assign != null) harmony.Patch(assign, prefix: new HarmonyMethod(typeof(GalaxyRegion), nameof(Assign_Prefix)));
            else Plugin.Log.LogWarning(P + "SectorMap.AssignSystemIds not found; only the InitAllSystems prefix is used");
            if (init != null) harmony.Patch(init, prefix: new HarmonyMethod(typeof(GalaxyRegion), nameof(Init_Prefix)));
            else Plugin.Log.LogWarning(P + "SectorMap.InitAllSystems not found");
            if (assign == null && init == null) Plugin.Log.LogWarning(P + "no hook: galaxy regions are off");
        }

        private static void Assign_Prefix(Sector.SectorMap __instance) => Ensure(__instance, "AssignSystemIds");

        private static void Init_Prefix(Sector.SectorMap __instance) => Ensure(__instance, "InitAllSystems");

        /// <summary>The galaxy file's path for the config value, or null when none is set.</summary>
        internal static string FilePath()
        {
            var name = ConfigName;
            if (name == "") return null;
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5);
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
            {
                Plugin.Log.LogWarning(P + $"'{name}' isn't a plain file name; no region");
                return null;
            }
            return Path.Combine(GalaxyCatalogue.Folder, name + ".json");
        }

        /// <summary>Put the region in the Trench's list or take it out, for the mission this SectorMap is for. Never throws.</summary>
        private static void Ensure(Sector.SectorMap sectorMap, string hook)
        {
            Map.SectorRoot map;
            try { map = Loader.EJLNDBGLIFL != null ? Loader.EJLNDBGLIFL.GetLoadedMap() : null; }
            catch (Exception e) { Plugin.Log.LogWarning(P + $"{hook}: the loaded map: {e.Message}"); return; }
            if (map == null || map.Systems == null) return;
            if (map.name.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // Our links only ever go in the Trench's list.
                Active = false;
                GalaxyCatalogue.TrenchCountOverride = null;
                return;
            }

            var before = new List<Map.SystemLink>(map.Systems);
            try
            {
                if (keptFor != map)
                {
                    // First sight of this Trench asset (or it was reloaded): its list is the Trench's own.
                    var old = regionLinks;
                    keptFor = map;
                    trenchOwn = new List<Map.SystemLink>();
                    foreach (var l in map.Systems) if (l != null && (old == null || !old.Contains(l))) trenchOwn.Add(l);
                    regionLinks = null;
                    builtHash = null;
                    Plugin.Log.LogInfo(P + $"{map.name}: {trenchOwn.Count} systems of its own");
                }

                bool on;
                if (ReferenceEquals(decidedFor, sectorMap)) on = decidedOn; // the fallback hook for the same mission: same answer
                else
                {
                    on = Decide();
                    if (on && !EnsureBuilt(map)) on = false;
                    decidedFor = sectorMap;
                    decidedOn = on;
                    if (GalaxyRoom.IsHost) GalaxyRoom.PublishDecision(on ? builtHash : GalaxyRoom.Off, on ? RegionName : "");
                }

                RegionLinks.Apply(map.Systems, trenchOwn, regionLinks ?? new List<Map.SystemLink>(), on);
                for (int i = 0; i < map.Systems.Count; i++) map.Systems[i].System.SystemID = i; // as AssignSystemIds does
                Active = on;
                GalaxyCatalogue.TrenchCountOverride = on ? trenchOwn.Count : (int?)null;
                if (on)
                    Plugin.Log.LogInfo(P + $"{hook}: {RegionName} on: {regionLinks.Count} systems after the Trench's {trenchOwn.Count} "
                        + $"(ids {trenchOwn.Count}-{map.Systems.Count - 1})");
                else if (before.Count != map.Systems.Count)
                    Plugin.Log.LogInfo(P + $"{hook}: region taken out; the Trench's own {map.Systems.Count} systems");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(P + $"{hook}: failed, the Trench's list left as it was: {e}");
                try
                {
                    // Back to the Trench's own list if we know it, else to what it was.
                    RegionLinks.Apply(map.Systems, trenchOwn ?? before, null, false);
                    for (int i = 0; i < map.Systems.Count; i++) map.Systems[i].System.SystemID = i;
                }
                catch (Exception e2) { Plugin.Log.LogError(P + "restoring the list: " + e2.Message); }
                Active = false;
                GalaxyCatalogue.TrenchCountOverride = null;
                decidedFor = sectorMap;
                decidedOn = false;
                if (GalaxyRoom.IsHost) GalaxyRoom.PublishDecision(GalaxyRoom.Off, "");
            }
        }

        // ---- The decision ----------------------------------------------------------------------------------------

        private static bool Decide()
        {
            var mission = RevivalMissions.Selected;
            bool wants = mission == typeof(PatrolSequence) || mission == typeof(Mission.DebugExplorationMode.DebugExplorationModeSequence);
            string own = GalaxyRoom.OwnHash;
            string missionName = mission != null ? mission.Name : "not a Revival mission";

            // Joining a mission already under way: the host decided at its start; follow that.
            string running = GalaxyRoom.RunningDecision();
            if (!GalaxyRoom.IsHost && running != null)
            {
                bool follow = running != GalaxyRoom.Off && running == own;
                Plugin.Log.LogInfo(P + $"joining a mission under way ({missionName}): the host has the region "
                    + (running == GalaxyRoom.Off ? "off" : $"on ({Short(running)})") + $", ours {Short(own)}: {(follow ? "on" : "off")}");
                if (running != GalaxyRoom.Off && !follow)
                    Plugin.Log.LogWarning(P + "the crew flies with a region we can't build; leaving the crew (GalaxyRoom)");
                return follow;
            }

            string hostHash = GalaxyRoom.HostHash;
            if (wants && GalaxyRoom.IsHost && own == "" && ConfigName != "")
                Plugin.Log.LogWarning(P + $"[Galaxy] Region is '{ConfigName}' but there's no {FilePath() ?? "file"}; the Trench only");
            var players = GalaxyRoom.Players();
            string name = !string.IsNullOrEmpty(GalaxyRoom.HostRegionName) ? GalaxyRoom.HostRegionName : RegionName;
            if (string.IsNullOrEmpty(name)) name = "The galaxy region";
            var result = GalaxyGate.Decide(wants, name, hostHash, players);

            var list = new List<string>();
            foreach (var p in players) list.Add(p.Key + " " + Short(p.Value));
            Plugin.Log.LogInfo(P + $"decision for {missionName}: host's file {Short(hostHash)}, ours {Short(own)}, crew [{string.Join(", ", list.ToArray())}]"
                + $" -> {(result.On ? "on" : "off")}{(result.Reason != null ? " (" + result.Reason + ")" : "")}");
            if (result.On && own != hostHash)
            {
                // Can't happen through GalaxyGate (we're among the players compared), but never build someone else's region.
                Plugin.Log.LogWarning(P + "on for the crew but our file differs from the host's; off here");
                return false;
            }
            if (result.Reason != null && GalaxyRoom.IsHost) Notify(result.Reason);
            return result.On;
        }

        private static string Short(string hash) => string.IsNullOrEmpty(hash) ? "none" : hash.Length > 8 ? hash.Substring(0, 8) : hash;

        // ---- Building --------------------------------------------------------------------------------------------

        /// <summary>The region's links, built for this Trench from the file as it is now; false if there's no usable file.</summary>
        private static bool EnsureBuilt(Map.SectorRoot map)
        {
            var path = FilePath();
            if (path == null || !File.Exists(path))
            {
                Plugin.Log.LogWarning(P + $"no galaxy file at {path ?? "(none set)"}");
                return false;
            }
            string json = File.ReadAllText(path);
            string hash = GalaxyFile.Hash(json);
            if (regionLinks != null && builtHash == hash) return true;
            if (failedHash == hash)
            {
                Plugin.Log.LogWarning(P + $"{Path.GetFileName(path)} still has the problems logged earlier; the Trench only");
                return false;
            }

            // Build with the Trench's own list in place, so the catalogue lists only the Trench's systems.
            RegionLinks.Apply(map.Systems, trenchOwn, null, false);
            try
            {
                var links = Build(map, json, path);
                if (links == null)
                {
                    failedHash = hash;
                    if (GalaxyRoom.IsHost) Notify($"{RegionName} is off: its galaxy file has problems (listed in the log).");
                    return false;
                }
                regionLinks = links;
                builtHash = hash;
                return true;
            }
            catch (Exception e)
            {
                failedHash = hash;
                Plugin.Log.LogError(P + $"building {Path.GetFileName(path)} failed; the Trench only: {e}");
                return false;
            }
        }

        private static List<Map.SystemLink> Build(Map.SectorRoot trench, string json, string path)
        {
            GalaxyFile file;
            try { file = GalaxyFile.Parse(json); }
            catch (FormatException e)
            {
                Plugin.Log.LogError(P + $"{Path.GetFileName(path)}: {e.Message}; the Trench only");
                return null;
            }
            builtName = !string.IsNullOrEmpty(file.Name) ? file.Name : ConfigName;

            // The same check the designer makes, against what this game has.
            var catalogue = GalaxyCatalogue.Build(trench, null);
            var problems = GalaxyCheck.Problems(file, catalogue);
            if (problems.Count > 0)
            {
                Plugin.Log.LogError(P + $"{Path.GetFileName(path)} has {problems.Count} problem(s); skipped, the Trench only:");
                foreach (var p in problems) Plugin.Log.LogError(P + "  " + p);
                return null;
            }

            var game = new GameParts(trench);
            var links = new List<Map.SystemLink>();
            var missing = new List<string>();
            foreach (var sys in file.Systems) game.Resolve(sys, missing);
            if (missing.Count > 0)
            {
                Plugin.Log.LogError(P + $"{Path.GetFileName(path)}: not found in the game, skipped, the Trench only:");
                foreach (var m in missing) Plugin.Log.LogError(P + "  " + m);
                return null;
            }
            foreach (var sys in file.Systems) links.Add(BuildSystem(sys, game));
            Plugin.Log.LogInfo(P + $"built {builtName} from {Path.GetFileName(path)}: {links.Count} systems");
            return links;
        }

        private static Map.SystemLink BuildSystem(GalaxySystem sys, GameParts game)
        {
            var template = game.System(sys.Template);
            var copy = UnityEngine.Object.Instantiate(template);
            copy.name = "Galaxy " + sys.Name;
            copy.hideFlags |= HideFlags.DontUnloadUnusedAsset; // the bridge load and every warp run Resources.UnloadUnusedAssets
            copy.SystemName = sys.Name;                         // plain: a "id|" text key would show the original's name
            copy.SystemIDTag = "Galaxy_" + sys.Id;
            if (sys.SystemTags != null) copy.SystemTags = TagList(sys.SystemTags);
            if (!string.IsNullOrEmpty(sys.Skybox)) copy.SkyboxPath = sys.Skybox;
            copy.SystemTransforms = new List<SystemTransform>();

            // ROOT, then the file's nodes, parents first.
            var root = Node.Copy(game.Root(template), null, copy);
            var built = new Dictionary<string, Map.SystemNode>();
            var pending = new List<GalaxyNode>(sys.Nodes);
            while (pending.Count > 0)
            {
                int done = 0;
                for (int i = 0; i < pending.Count; i++)
                {
                    var n = pending[i];
                    Map.SystemNode parent;
                    if (n.Parent == null) parent = root;
                    else if (!built.TryGetValue(n.Parent, out parent)) continue;
                    built[n.Id] = n.CopyFamily != null ? CopiedFamily(n, parent, copy, game) : Node.Make(n, parent, copy, game, template);
                    pending.RemoveAt(i--);
                    done++;
                }
                if (done == 0) throw new InvalidOperationException($"{sys.Id}: nodes with no parent to hang from"); // the check rules this out
            }

            int pois = 0, families = 0;
            foreach (var t in copy.SystemTransforms)
            {
                var sn = t.SystemNode;
                if (sn == null) continue;
                if (Node.HasTag(sn, Tag.FAMILY)) families++;
                if (Node.HasTag(sn, Tag.POI)) pois++;
            }
            var link = new Map.SystemLink { Position = new Vector3(sys.X, 0f, sys.Z), System = copy };
            Plugin.Log.LogInfo(P + $"  {sys.Name} ({sys.Id}): template {template.SystemName}, at ({sys.X}, {sys.Z}), "
                + $"{copy.SystemTransforms.Count} nodes, {families} families, {pois} Points of Interest");
            return link;
        }

        // A whole Trench family as new nodes, renamed; the file can give it a map name, backdrop and icon.
        private static Map.SystemNode CopiedFamily(GalaxyNode n, Map.SystemNode parent, Map.SystemRoot copy, GameParts game)
        {
            var source = game.Family(n.CopyFamily);
            var family = Node.Copy(source, parent, copy, true);
            family.name = n.Name;
            if (family.Transform != null)
            {
                family.Transform.name = n.Name;
                Node.SetTransformName(family.Transform, n.Name);
            }
            if (!string.IsNullOrEmpty(n.MapName)) family.SystemMapName = n.MapName;
            if (n.FamilyBackground != null) family.FamilyMapBackground = game.Sprite(n.FamilyBackground);
            if (n.MapIcon != null) family.SystemMapIconSprite = game.Sprite(n.MapIcon);
            return family;
        }

        internal static NovaTagList TagList(IList<string> names)
        {
            var tags = new List<Tag>();
            var kept = new List<string>();
            foreach (var name in names)
            {
                tags.Add((Tag)Enum.Parse(typeof(Tag), name));
                kept.Add(name);
            }
            var list = new NovaTagList();
            Traverse.Create(list).Field("_tagNames").SetValue(kept.ToArray());
            Traverse.Create(list).Field("_tags").SetValue(tags.ToArray());
            return list;
        }

        // ---- The host's message ----------------------------------------------------------------------------------

        /// <summary>Logged now, shown as a game popup once the mission's first system is loaded.</summary>
        private static void Notify(string text)
        {
            Plugin.Log.LogInfo(P + text);
            try { Plugin.Instance.StartCoroutine(NotifyLater(text)); }
            catch (Exception e) { Plugin.Log.LogWarning(P + "popup: " + e.Message); }
        }

        private static IEnumerator NotifyLater(string text)
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 180f && !SystemLoaded()) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(3f);
            try { UI.PopupMgr.ShowPopup(Translation.T(text).Replace('|', ' '), null, UI.PDHCCBFJODN.Notification, true); }
            catch (Exception e) { Plugin.Log.LogWarning(P + "popup: " + e.Message); }
        }

        private static bool SystemLoaded()
        {
            try
            {
                var map = decidedFor as Sector.SectorMap;
                if (map == null) return true; // gone: show it anyway
                return Traverse.Create(map).Property("KALBLMGFNIN").GetValue() != null; // MapBase.ActiveSystem
            }
            catch (Exception) { return true; }
        }

        // ---- What the game has, by name --------------------------------------------------------------------------

        /// <summary>The Trench's systems, prefabs and sprites by the names a galaxy file uses.</summary>
        private sealed class GameParts
        {
            private readonly Dictionary<string, Map.SystemRoot> systems = new Dictionary<string, Map.SystemRoot>();
            private readonly Dictionary<string, Sector.SectorBody> prefabs = new Dictionary<string, Sector.SectorBody>();
            private readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
            private readonly Dictionary<Map.SystemRoot, List<Map.SystemNode>> nodes = new Dictionary<Map.SystemRoot, List<Map.SystemNode>>();
            private bool loadedAll;

            internal GameParts(Map.SectorRoot trench)
            {
                foreach (var link in trench.Systems)
                {
                    var root = link?.System;
                    if (root == null) continue;
                    string name = !string.IsNullOrEmpty(root.SystemName) ? root.SystemName : root.name;
                    if (!systems.ContainsKey(name)) systems[name] = root;
                    foreach (var n in Nodes(root))
                    {
                        if (n.Prefab != null && !prefabs.ContainsKey(n.Prefab.name)) prefabs[n.Prefab.name] = n.Prefab;
                        AddSprite(n.FamilyMapBackground);
                        AddSprite(n.SystemMapIconSprite);
                    }
                }
            }

            private void AddSprite(Sprite s)
            {
                if (s != null && !string.IsNullOrEmpty(s.name) && !sprites.ContainsKey(s.name)) sprites[s.name] = s;
            }

            // Every prefab under Resources/Sector, as the catalogue lists them; only when the Trench's own don't do.
            private void LoadAll()
            {
                if (loadedAll) return;
                loadedAll = true;
                foreach (var go in Resources.LoadAll<GameObject>("Sector"))
                {
                    if (go == null) continue;
                    var body = go.GetComponent<Sector.SectorBody>();
                    if (body != null && !prefabs.ContainsKey(go.name)) prefabs[go.name] = body;
                }
            }

            internal List<Map.SystemNode> Nodes(Map.SystemRoot root)
            {
                if (nodes.TryGetValue(root, out var all)) return all;
                all = Node.All(root);
                nodes[root] = all;
                return all;
            }

            /// <summary>Everything the file names, found now, so nothing is changed when something is missing.</summary>
            internal void Resolve(GalaxySystem sys, List<string> missing)
            {
                if (!systems.TryGetValue(sys.Template ?? "", out var template)) missing.Add($"{sys.Id}: Trench system '{sys.Template}'");
                else if (RootOrNull(template) == null) missing.Add($"{sys.Id}: '{sys.Template}' has no ROOT node to copy");
                foreach (var n in sys.Nodes)
                {
                    string at = sys.Id + "/" + n.Id;
                    if (n.CopyFamily != null && FamilyOrNull(n.CopyFamily) == null)
                        missing.Add($"{at}: family '{n.CopyFamily.Family}' in '{n.CopyFamily.System}'");
                    if (n.CopyFamily == null && !string.IsNullOrEmpty(n.Prefab) && PrefabOrNull(n.Prefab) == null)
                        missing.Add($"{at}: prefab '{n.Prefab}'");
                    if (n.FamilyBackground != null && !sprites.ContainsKey(n.FamilyBackground)) missing.Add($"{at}: sprite '{n.FamilyBackground}'");
                    if (n.MapIcon != null && !sprites.ContainsKey(n.MapIcon)) missing.Add($"{at}: sprite '{n.MapIcon}'");
                    foreach (var t in n.Tags)
                        if (!Enum.IsDefined(typeof(Tag), t)) missing.Add($"{at}: tag '{t}'");
                    if (n.CopyFamily == null && Array.IndexOf(GalaxyFile.Units, n.Unit) < 0) missing.Add($"{at}: unit '{n.Unit}'");
                }
                if (sys.SystemTags != null)
                    foreach (var t in sys.SystemTags)
                        if (!Enum.IsDefined(typeof(Tag), t)) missing.Add($"{sys.Id}: system tag '{t}'");
            }

            internal Map.SystemRoot System(string name) => systems[name];

            internal Map.SystemNode Root(Map.SystemRoot system) => RootOrNull(system);

            private Map.SystemNode RootOrNull(Map.SystemRoot system)
            {
                foreach (var n in Nodes(system)) if (n.Parent == null && Node.HasTag(n, Tag.ROOT)) return n;
                return null;
            }

            internal Map.SystemNode Family(CopyFamily f) => FamilyOrNull(f);

            private Map.SystemNode FamilyOrNull(CopyFamily f)
            {
                if (!systems.TryGetValue(f.System ?? "", out var system)) return null;
                foreach (var n in Nodes(system)) if (n.name == f.Family) return n;
                return null;
            }

            internal Sector.SectorBody Prefab(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                return PrefabOrNull(name);
            }

            private Sector.SectorBody PrefabOrNull(string name)
            {
                if (prefabs.TryGetValue(name, out var body)) return body;
                LoadAll();
                return prefabs.TryGetValue(name, out body) ? body : null;
            }

            internal Sprite Sprite(string name) => name != null && sprites.TryGetValue(name, out var s) ? s : null;

            /// <summary>A backdrop for a family that names none: the template's first family's.</summary>
            internal Sprite TemplateBackdrop(Map.SystemRoot template)
            {
                foreach (var n in Nodes(template))
                    if (n.FamilyMapBackground != null && Node.HasTag(n, Tag.FAMILY)) return n.FamilyMapBackground;
                foreach (var n in Nodes(template))
                    if (n.FamilyMapBackground != null) return n.FamilyMapBackground;
                return null;
            }
        }

        // ---- Nodes and their transforms --------------------------------------------------------------------------

        private static class Node
        {
            private static FieldInfo NodeField(string name) => AccessTools.Field(typeof(Map.SystemNode), name)
                ?? throw new MissingFieldException("Map.SystemNode", name);

            private static FieldInfo TransformField(string name) => AccessTools.Field(typeof(SystemTransform), name)
                ?? throw new MissingFieldException("SystemTransform", name);

            internal static List<Map.SystemNode> Children(Map.SystemNode n) =>
                NodeField("_children").GetValue(n) as List<Map.SystemNode> ?? new List<Map.SystemNode>();

            /// <summary>Every node of a system: its transforms' nodes, and their children.</summary>
            internal static List<Map.SystemNode> All(Map.SystemRoot root)
            {
                var all = new List<Map.SystemNode>();
                foreach (var t in root.SystemTransforms)
                    if (t != null && t.SystemNode != null && !all.Contains(t.SystemNode)) all.Add(t.SystemNode);
                for (int i = 0; i < all.Count; i++)
                    foreach (var c in Children(all[i]))
                        if (c != null && !all.Contains(c)) all.Add(c);
                return all;
            }

            private static Tag[] OwnTags(Map.SystemNode n) => n.Tags?.Tags ?? new Tag[0];

            private static Tag[] PrefabTags(Map.SystemNode n)
            {
                if (n.Prefab == null) return new Tag[0];
                return (Traverse.Create(n.Prefab).Field("PrefabTags").GetValue() as NovaTagList)?.Tags ?? new Tag[0];
            }

            internal static bool HasTag(Map.SystemNode n, Tag tag) =>
                Array.IndexOf(OwnTags(n), tag) >= 0 || Array.IndexOf(PrefabTags(n), tag) >= 0;

            /// <summary>A copy of a Trench node (and with children, its whole subtree) as new objects under parent.
            /// The proven spike recipe: Instantiate, rewire _parent/_children/Root/Transform/View, a new transform with
            /// the original transform's stored values.</summary>
            internal static Map.SystemNode Copy(Map.SystemNode source, Map.SystemNode parent, Map.SystemRoot copy,
                bool withChildren = false)
            {
                var node = UnityEngine.Object.Instantiate(source);
                node.name = source.name; // not "(Clone)"
                node.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Wire(node, parent, copy);
                if (node.ProceduralStatusEffectData == null) node.ProceduralStatusEffectData = new StatusEffectData[0]; // SpawnMapBody reads .Length

                // A transform only where the original has one (LinkParentAndChildren skips children without).
                var sourceTransform = source.Transform;
                if (sourceTransform != null)
                {
                    var transform = NewTransform(node, copy, sourceTransform.name);
                    foreach (var f in new[] { "_position", "_rotation", "_radius", "_name" })
                        TransformField(f).SetValue(transform, TransformField(f).GetValue(sourceTransform));
                }

                if (withChildren)
                    foreach (var child in Children(source))
                        if (child != null) Copy(child, node, copy, true);
                return node;
            }

            /// <summary>A new node from the file.</summary>
            internal static Map.SystemNode Make(GalaxyNode n, Map.SystemNode parent, Map.SystemRoot copy, GameParts game, Map.SystemRoot template)
            {
                var node = ScriptableObject.CreateInstance<Map.SystemNode>();
                node.name = n.Name;
                node.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                node.Position = new Vector3(n.X, n.Y, n.Z);
                node.Rotation = Quaternion.Euler(n.RotX, n.RotY, n.RotZ);
                var scale = NodeField("Scale"); // Map.DistanceUnit, obfuscated in the game's assembly
                scale.SetValue(node, Enum.Parse(scale.FieldType, n.Unit));
                node.Prefab = game.Prefab(n.Prefab);
                node.ImpulseLandingRadius = n.LandingRadius;
                node.SystemMapName = !string.IsNullOrEmpty(n.MapName) ? n.MapName : n.Name;
                node.SystemMapIconSprite = game.Sprite(n.MapIcon);
                node.FamilyMapBackground = game.Sprite(n.FamilyBackground);
                if (node.ProceduralStatusEffectData == null) node.ProceduralStatusEffectData = new StatusEffectData[0];

                // The impulse map needs FAMILY on families and POI on Points of Interest (ContainerTransform, FamilyRegions):
                // the Trench has them on the node or its prefab; add them when neither does.
                var tags = new List<string>(n.Tags);
                node.Tags = TagList(tags);
                if (n.Prefab == GalaxyCheck.FamilyPrefab)
                {
                    if (!HasTag(node, Tag.FAMILY)) tags.Add(nameof(Tag.FAMILY));
                    if (node.FamilyMapBackground == null) node.FamilyMapBackground = game.TemplateBackdrop(template); // not a blank impulse map
                }
                if (n.Prefab == GalaxyCheck.PoiPrefab && !HasTag(node, Tag.POI)) tags.Add(nameof(Tag.POI));
                if (tags.Count != n.Tags.Count) node.Tags = TagList(tags);

                Wire(node, parent, copy);
                var transform = NewTransform(node, copy, n.Name);
                TransformField("_position").SetValue(transform, node.SystemPosition);
                TransformField("_rotation").SetValue(transform, node.Rotation);
                TransformField("_radius").SetValue(transform, node.Radius);
                TransformField("_name").SetValue(transform, n.Name);
                return node;
            }

            private static void Wire(Map.SystemNode node, Map.SystemNode parent, Map.SystemRoot copy)
            {
                NodeField("_parent").SetValue(node, parent);
                NodeField("_children").SetValue(node, new List<Map.SystemNode>());
                NodeField("Root").SetValue(node, copy);
                NodeField("Transform").SetValue(node, null);
                var view = AccessTools.Field(typeof(Map.SystemNode), "<View>k__BackingField");
                if (view != null) view.SetValue(node, null);
                if (parent != null) Children(parent).Add(node);
            }

            // id and sector come from InitAllSystems (AssignSector), System from Clone.
            private static SystemTransform NewTransform(Map.SystemNode node, Map.SystemRoot copy, string name)
            {
                var transform = ScriptableObject.CreateInstance<SystemTransform>();
                transform.name = name;
                transform.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                TransformField("_systemNode").SetValue(transform, node);
                NodeField("Transform").SetValue(node, transform);
                copy.SystemTransforms.Add(transform);
                return transform;
            }

            internal static void SetTransformName(SystemTransform t, string name) => TransformField("_name").SetValue(t, name);
        }
    }
}
