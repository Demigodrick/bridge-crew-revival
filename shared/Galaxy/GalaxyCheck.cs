using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// Everything that would make a galaxy file misbehave in the game, as plain sentences. The designer shows them
    /// and refuses to save while there are any; the mod runs the same check and ignores a file that fails it.
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

        /// <summary>A null catalogue means no game data: only the checks that need none are made.</summary>
        public static List<string> Problems(GalaxyFile file, Catalogue catalogue)
        {
            var p = new List<string>();
            if (file.Systems == null || file.Systems.Count == 0)
            {
                p.Add("The region has no systems");
                return p;
            }

            var prefabs = new Dictionary<string, bool>();
            bool known = catalogue != null;
            if (!known) catalogue = new Catalogue();
            foreach (var x in catalogue.Prefabs) prefabs[x.Name] = true;
            var tags = new Dictionary<string, bool>();
            foreach (var t in catalogue.Tags) tags[t] = true;
            var templates = new Dictionary<string, bool>();
            var trenchNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var trench = new Dictionary<string, CatalogueSystem>();
            foreach (var s in catalogue.TrenchSystems) { templates[s.Name] = true; trenchNames[s.Name] = true; trench[s.Name] = s; }
            var sprites = new Dictionary<string, bool>();
            foreach (var x in catalogue.Sprites) sprites[x] = true;

            var stationPrefabs = new Dictionary<string, bool>();
            foreach (var x in catalogue.Prefabs) if (x.Kind == "station") stationPrefabs[x.Name] = true;

            bool anyStation = false;
            foreach (var sys in file.Systems)
            {
                if (known && (sys.Template == null || !templates.ContainsKey(sys.Template)))
                    p.Add(sys.Id + ": no Trench system '" + sys.Template + "' to copy");
                if (known && sys.Name != null && trenchNames.ContainsKey(sys.Name))
                    p.Add(sys.Id + ": '" + sys.Name + "' is already a Trench system");

                if (sys.Name != null && sys.Name.IndexOf('|') >= 0)
                    p.Add(sys.Id + ": use a plain name, not a text key ('" + sys.Name + "')");

                var ids = new Dictionary<string, GalaxyNode>();
                foreach (var n in sys.Nodes) ids[n.Id] = n;

                bool jump = false, anyPoi = false;
                foreach (var n in sys.Nodes)
                {
                    string at = sys.Id + "/" + n.Id;
                    if (n.Name != null && n.Name.IndexOf('|') >= 0)
                        p.Add(at + ": use a plain name, not a text key ('" + n.Name + "')");
                    if (n.Parent == null && !IsFamily(n))
                        p.Add(at + ": top-level nodes must be families (Family Group) or a copied family");
                    if (n.Prefab == PoiPrefab)
                    {
                        GalaxyNode up;
                        if (n.Parent == null || (ids.TryGetValue(n.Parent, out up) && !IsFamily(up)))
                            p.Add(at + ": a Point of Interest must sit directly under a family");
                        else if (ids.TryGetValue(n.Parent, out up) && up.CopyFamily == null) anyPoi = true;
                    }
                    if (n.CopyFamily != null)
                    {
                        CatalogueFamily fam = null;
                        CatalogueSystem ts = null;
                        bool famsUnknown = false;
                        if (known)
                        {
                            if (trench.TryGetValue(n.CopyFamily.System ?? "", out ts))
                            {
                                famsUnknown = ts.Families.Count == 0;
                                foreach (var cf in ts.Families) if (cf.Name == n.CopyFamily.Family) fam = cf;
                            }
                            if (fam == null && !famsUnknown)
                                p.Add(at + ": no family '" + n.CopyFamily.Family + "' in Trench system '" + n.CopyFamily.System + "'");
                        }
                        if (!known || famsUnknown || (fam != null && fam.Pois.Count > 0)) anyPoi = true;
                        if (famsUnknown) { jump = true; anyStation = true; }
                        if (fam != null && fam.HasSpawn) jump = true;
                        if (fam != null && fam.HasStation) anyStation = true;
                    }
                    if (known && sprites.Count > 0)
                    {
                        if (n.FamilyBackground != null && !sprites.ContainsKey(n.FamilyBackground))
                            p.Add(at + ": no sprite '" + n.FamilyBackground + "' in the game");
                        if (n.MapIcon != null && !sprites.ContainsKey(n.MapIcon))
                            p.Add(at + ": no sprite '" + n.MapIcon + "' in the game");
                    }
                    if (known && n.CopyFamily == null && (n.Prefab == null || (n.Prefab != "" && !prefabs.ContainsKey(n.Prefab)))) p.Add(at + ": no prefab '" + n.Prefab + "' in the game");
                    if (n.CopyFamily == null && n.Prefab == "")
                    {
                        // The game has prefab-less nodes only as jump points / markers and as regions under a POI.
                        bool marker = false;
                        foreach (var t in n.Tags)
                            if (t == "WarpDestination" || catalogue.SpawnTags.Contains(t)
                                || (catalogue.SpawnTags.Count == 0 && (t == "Player" || t == "AltPlayerSpawn"))) marker = true;
                        GalaxyNode par;
                        bool underPoi = n.Parent != null && ids.TryGetValue(n.Parent, out par) && par.Prefab == PoiPrefab;
                        if (!marker && !underPoi) p.Add(at + ": no model chosen");
                    }
                    // Real station bodies carry the tag on their prefab, not on the node.
                    if (n.Prefab != null && stationPrefabs.ContainsKey(n.Prefab)) anyStation = true;
                    foreach (var t in n.Tags)
                    {
                        if (known && !tags.ContainsKey(t)) p.Add(at + ": no tag '" + t + "' in the game");
                        if (t == StationTag) anyStation = true;
                        if (catalogue.SpawnTags.Contains(t)) jump = true;
                    }
                    if (n.Parent != null)
                    {
                        if (!ids.ContainsKey(n.Parent)) p.Add(at + ": parent '" + n.Parent + "' isn't in this system");
                        else if (IsOwnAncestor(n, ids)) p.Add(at + ": is its own ancestor");
                    }
                }
                if (!anyPoi)
                    p.Add(sys.Id + ": no family with a Point of Interest: the impulse map would be empty");
                // With no spawn tag known there's nothing to look for, so don't claim a system lacks one.
                if (!jump && catalogue.SpawnTags.Count > 0)
                    p.Add(sys.Id + ": no jump point (a node tagged " + catalogue.SpawnTags[0] + ")");
            }

            if (!anyStation) p.Add("No station anywhere in the region: Patrol can't restock there");

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
