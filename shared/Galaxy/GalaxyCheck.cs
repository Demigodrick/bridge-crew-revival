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

        /// <summary>Two systems closer than this share of the universe width sit on top of each other on the map.</summary>
        private const float OverlapShare = 0.02f;

        public static List<string> Problems(GalaxyFile file, Catalogue catalogue)
        {
            var p = new List<string>();
            if (file.Systems == null || file.Systems.Count == 0)
            {
                p.Add("The region has no systems");
                return p;
            }

            var prefabs = new Dictionary<string, bool>();
            foreach (var x in catalogue.Prefabs) prefabs[x.Name] = true;
            var tags = new Dictionary<string, bool>();
            foreach (var t in catalogue.Tags) tags[t] = true;
            var templates = new Dictionary<string, bool>();
            var trenchNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in catalogue.TrenchSystems) { templates[s.Name] = true; trenchNames[s.Name] = true; }

            bool anyStation = false;
            foreach (var sys in file.Systems)
            {
                if (sys.Template == null || !templates.ContainsKey(sys.Template))
                    p.Add(sys.Id + ": no Trench system '" + sys.Template + "' to copy");
                if (sys.Name != null && trenchNames.ContainsKey(sys.Name))
                    p.Add(sys.Id + ": '" + sys.Name + "' is already a Trench system");

                var ids = new Dictionary<string, GalaxyNode>();
                foreach (var n in sys.Nodes) ids[n.Id] = n;

                bool jump = false;
                foreach (var n in sys.Nodes)
                {
                    string at = sys.Id + "/" + n.Id;
                    if (n.Prefab == null || !prefabs.ContainsKey(n.Prefab)) p.Add(at + ": no prefab '" + n.Prefab + "' in the game");
                    foreach (var t in n.Tags)
                    {
                        if (!tags.ContainsKey(t)) p.Add(at + ": no tag '" + t + "' in the game");
                        if (t == StationTag) anyStation = true;
                        if (catalogue.SpawnTags.Contains(t)) jump = true;
                    }
                    if (n.Parent != null)
                    {
                        if (!ids.ContainsKey(n.Parent)) p.Add(at + ": parent '" + n.Parent + "' isn't in this system");
                        else if (IsOwnAncestor(n, ids)) p.Add(at + ": is its own ancestor");
                    }
                }
                // With no spawn tag known there's nothing to look for, so don't claim a system lacks one.
                if (!jump && catalogue.SpawnTags.Count > 0)
                    p.Add(sys.Id + ": no jump point (a node tagged " + catalogue.SpawnTags[0] + ")");
            }

            if (!anyStation) p.Add("No station anywhere in the region: Patrol can't restock there");

            if (catalogue.UniverseWidth > 0)
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
