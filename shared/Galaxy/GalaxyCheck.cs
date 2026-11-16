using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// Everything that would make a galaxy file misbehave in the game, as plain sentences. The designer shows them
    /// and asks "Save anyway?" while there are any; the mod runs the same check and ignores a file that fails it.
    /// </summary>
    public static class GalaxyCheck
    {
        /// <summary>The tag the game gives a station, which Patrol restocks at.</summary>
        public const string StationTag = "SpaceStation";

        /// <summary>The prefab of a node that groups a system's bodies into a map family.</summary>
        public const string FamilyPrefab = "Family Group";

        /// <summary>The prefab of a node that is one entry (a place to warp to) on the impulse map.</summary>
        public const string PoiPrefab = "Point of Interest";

        private static bool IsFamily(GalaxyNode n) { return n.CopyFamily != null || n.Prefab == FamilyPrefab; }

        /// <summary>Two systems closer than this share of the universe width sit on top of each other on the map.</summary>
        private const float OverlapShare = 0.02f;

        /// <summary>
        /// The prefab names the file's own nodes use (not copied families, not ""), once each in file order. Problems
        /// asks the catalogue about these and no others, so a catalogue of the Trench plus those of these the game
        /// has gives the same answer as one of every prefab.
        /// </summary>
        public static List<string> PrefabNames(GalaxyFile file)
        {
            var names = new List<string>();
            if (file == null) return names;
            if (file.Systems != null)
                foreach (var sys in file.Systems)
                    if (sys != null) AddPrefabNames(names, sys.Nodes);
            if (file.Trench != null)
                foreach (var a in file.Trench)
                    if (a != null) AddPrefabNames(names, a.Nodes);
            return names;
        }

        private static void AddPrefabNames(List<string> names, List<GalaxyNode> nodes)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
                if (n != null && n.CopyFamily == null && !string.IsNullOrEmpty(n.Prefab) && !names.Contains(n.Prefab)) names.Add(n.Prefab);
        }

        /// <summary>What the node checks need from the catalogue, built once per Problems call.</summary>
        private sealed class Lookups
        {
            public Catalogue Catalogue;
            public bool Known;
            public Dictionary<string, bool> Prefabs = new Dictionary<string, bool>();
            public Dictionary<string, bool> Tags = new Dictionary<string, bool>();
            public Dictionary<string, CatalogueSystem> Trench = new Dictionary<string, CatalogueSystem>();
            public Dictionary<string, bool> Sprites = new Dictionary<string, bool>();
            public Dictionary<string, bool> StationPrefabs = new Dictionary<string, bool>();
        }

        /// <summary>What one node list turned out to contain; a region system must have each, an addition needn't.</summary>
        private sealed class NodeFacts { public bool Jump, AnyPoi, AnyStation; }

        // The node rules, shared by region systems and Trench additions. Problems read "<prefix><node id>: ...".
        private static void CheckNodes(List<string> p, Lookups lk, string prefix, List<GalaxyNode> nodes, NodeFacts facts)
        {
            var ids = new Dictionary<string, GalaxyNode>();
            foreach (var n in nodes) ids[n.Id] = n;

            foreach (var n in nodes)
            {
                string at = prefix + n.Id;
                if (n.Name != null && n.Name.IndexOf('|') >= 0)
                    p.Add(at + ": use a plain name, not a text key ('" + n.Name + "')");
                if (n.Parent == null && !IsFamily(n))
                    p.Add(at + ": top-level nodes must be families (Family Group) or a copied family");
                if (n.Prefab == PoiPrefab)
                {
                    GalaxyNode up;
                    if (n.Parent == null || (ids.TryGetValue(n.Parent, out up) && !IsFamily(up)))
                        p.Add(at + ": a Point of Interest must sit directly under a family");
                    else if (ids.TryGetValue(n.Parent, out up) && up.CopyFamily == null) facts.AnyPoi = true;
                }
                if (n.CopyFamily != null)
                {
                    CatalogueFamily fam = null;
                    CatalogueSystem ts = null;
                    bool famsUnknown = false;
                    if (lk.Known)
                    {
                        if (lk.Trench.TryGetValue(n.CopyFamily.System ?? "", out ts))
                        {
                            famsUnknown = ts.Families.Count == 0;
                            foreach (var cf in ts.Families) if (cf.Name == n.CopyFamily.Family) fam = cf;
                        }
                        if (fam == null && !famsUnknown)
                            p.Add(at + ": no family '" + n.CopyFamily.Family + "' in Trench system '" + n.CopyFamily.System + "'");
                    }
                    if (!lk.Known || famsUnknown || (fam != null && fam.Pois.Count > 0)) facts.AnyPoi = true;
                    if (famsUnknown) { facts.Jump = true; facts.AnyStation = true; }
                    if (fam != null && fam.HasSpawn) facts.Jump = true;
                    if (fam != null && fam.HasStation) facts.AnyStation = true;
                }
                if (lk.Known && lk.Sprites.Count > 0)
                {
                    if (n.FamilyBackground != null && !lk.Sprites.ContainsKey(n.FamilyBackground))
                        p.Add(at + ": no sprite '" + n.FamilyBackground + "' in the game");
                    if (n.MapIcon != null && !lk.Sprites.ContainsKey(n.MapIcon))
                        p.Add(at + ": no sprite '" + n.MapIcon + "' in the game");
                }
                if (lk.Known && n.CopyFamily == null && (n.Prefab == null || (n.Prefab != "" && !lk.Prefabs.ContainsKey(n.Prefab)))) p.Add(at + ": no prefab '" + n.Prefab + "' in the game");
                if (n.CopyFamily == null && n.Prefab == "")
                {
                    // The game has prefab-less nodes only as jump points / markers and as regions under a POI.
                    bool marker = false;
                    foreach (var t in n.Tags)
                        if (t == "WarpDestination" || lk.Catalogue.SpawnTags.Contains(t)
                            || (lk.Catalogue.SpawnTags.Count == 0 && (t == "Player" || t == "AltPlayerSpawn"))) marker = true;
                    GalaxyNode par;
                    bool underPoi = n.Parent != null && ids.TryGetValue(n.Parent, out par) && par.Prefab == PoiPrefab;
                    if (!marker && !underPoi) p.Add(at + ": no model chosen");
                }
                // Real station bodies carry the tag on their prefab, not on the node.
                if (n.Prefab != null && lk.StationPrefabs.ContainsKey(n.Prefab)) facts.AnyStation = true;
                foreach (var t in n.Tags)
                {
                    if (lk.Known && !lk.Tags.ContainsKey(t)) p.Add(at + ": no tag '" + t + "' in the game");
                    if (t == StationTag) facts.AnyStation = true;
                    if (lk.Catalogue.SpawnTags.Contains(t)) facts.Jump = true;
                }
                if (n.Parent != null)
                {
                    if (!ids.ContainsKey(n.Parent)) p.Add(at + ": parent '" + n.Parent + "' isn't in this system");
                    else if (IsOwnAncestor(n, ids)) p.Add(at + ": is its own ancestor");
                }
            }
        }

        /// <summary>A null catalogue means no game data: only the checks that need none are made.</summary>
        public static List<string> Problems(GalaxyFile file, Catalogue catalogue)
        {
            var p = new List<string>();
            bool noSystems = file.Systems == null || file.Systems.Count == 0;
            bool noTrench = file.Trench == null || file.Trench.Count == 0;
            if (noSystems && noTrench)
            {
                p.Add("The region has no systems");
                return p;
            }

            var lk = new Lookups();
            lk.Known = catalogue != null;
            if (!lk.Known) catalogue = new Catalogue();
            lk.Catalogue = catalogue;
            foreach (var x in catalogue.Prefabs) lk.Prefabs[x.Name] = true;
            foreach (var t in catalogue.Tags) lk.Tags[t] = true;
            var templates = new Dictionary<string, bool>();
            var trenchNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in catalogue.TrenchSystems) { templates[s.Name] = true; trenchNames[s.Name] = true; lk.Trench[s.Name] = s; }
            foreach (var x in catalogue.Sprites) lk.Sprites[x] = true;
            foreach (var x in catalogue.Prefabs) if (x.Kind == "station") lk.StationPrefabs[x.Name] = true;
            bool known = lk.Known;

            bool anyStation = false;
            if (file.Systems != null)
            foreach (var sys in file.Systems)
            {
                if (known && (sys.Template == null || !templates.ContainsKey(sys.Template)))
                    p.Add(sys.Id + ": no Trench system '" + sys.Template + "' to copy");
                if (known && sys.Name != null && trenchNames.ContainsKey(sys.Name))
                    p.Add(sys.Id + ": '" + sys.Name + "' is already a Trench system");

                if (sys.Name != null && sys.Name.IndexOf('|') >= 0)
                    p.Add(sys.Id + ": use a plain name, not a text key ('" + sys.Name + "')");

                var facts = new NodeFacts();
                CheckNodes(p, lk, sys.Id + "/", sys.Nodes, facts);
                if (facts.AnyStation) anyStation = true;
                if (!facts.AnyPoi)
                    p.Add(sys.Id + ": no family with a Point of Interest: the impulse map would be empty");
                // With no spawn tag known there's nothing to look for, so don't claim a system lacks one.
                if (!facts.Jump && catalogue.SpawnTags.Count > 0)
                    p.Add(sys.Id + ": no jump point (a node tagged " + catalogue.SpawnTags[0] + ")");
            }

            // Additions to the Trench's own systems: the same node rules, but the Trench system already has its jump
            // point, and the stations and POIs the region needs come from the region's own systems.
            if (file.Trench != null)
                foreach (var add in file.Trench)
                {
                    CatalogueSystem target = FindTrench(catalogue, add.System);
                    string shown = target != null ? (target.DisplayName ?? CatalogueNames.Display(target.Name)) : CatalogueNames.Display(add.System ?? "");
                    if (known && target == null) p.Add("trench " + shown + ": no Trench system '" + add.System + "'");
                    CheckNodes(p, lk, "trench " + shown + "/", add.Nodes, new NodeFacts());
                }

            if (!anyStation && !noSystems) p.Add("No station anywhere in the region: Patrol can't restock there");

            if (known && catalogue.UniverseWidth > 0)
            {
                float min = catalogue.UniverseWidth * OverlapShare;
                for (int i = 0; i < file.Systems.Count; i++)
                    for (int j = i + 1; j < file.Systems.Count; j++)
                    {
                        float dx = file.Systems[i].X - file.Systems[j].X, dz = file.Systems[i].Z - file.Systems[j].Z;
                        if (Math.Sqrt(dx * dx + dz * dz) < min)
                            p.Add(file.Systems[i].Id + " and " + file.Systems[j].Id + " overlap on the map");
                    }
            }
            return p;
        }

        /// <summary>The Trench system a file's name means: the exact catalogue name, or the plain name after "|", case-insensitive.</summary>
        public static CatalogueSystem FindTrench(Catalogue catalogue, string name)
        {
            if (catalogue == null || name == null) return null;
            foreach (var s in catalogue.TrenchSystems) if (s.Name == name) return s;
            foreach (var s in catalogue.TrenchSystems)
                if (string.Equals(CatalogueNames.Display(s.Name), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // Walk up the parents; coming back to the node itself means it sits in a loop. A walk that ends in some
        // other loop (not through this node) is reported by the nodes on that loop, not this one.
        private static bool IsOwnAncestor(GalaxyNode start, Dictionary<string, GalaxyNode> ids)
        {
            var seen = new Dictionary<string, bool>();
            GalaxyNode cur = start;
            while (cur.Parent != null)
            {
                GalaxyNode up;
                if (!ids.TryGetValue(cur.Parent, out up)) return false;
                if (up == start) return true;
                if (seen.ContainsKey(up.Id)) return false;
                seen[up.Id] = true;
                cur = up;
            }
            return false;
        }
    }
}
