using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>Works out a Trench system's map families from its finished nodes.</summary>
    public static class CatalogueFamilies
    {
        // The system's families: the nodes directly under its ROOT node(s) that are tagged FAMILY or are a
        // "Family Group" (some Trench families lack the tag), with the names of their Points of Interest. A family
        // has a spawn or a station when any node under it carries a spawn tag or SpaceStation, counting the tags its
        // prefab adds as the game does.
        public static List<CatalogueFamily> From(IList<GalaxyNode> nodes, Func<string, IList<string>> prefabTags, ICollection<string> spawnTags)
        {
            var result = new List<CatalogueFamily>();
            var children = new Dictionary<string, List<GalaxyNode>>();
            var roots = new HashSet<string>();
            foreach (var n in nodes)
            {
                if (n.Parent == null)
                {
                    if (n.Tags.Contains("ROOT")) roots.Add(n.Id);
                    continue;
                }
                List<GalaxyNode> list;
                if (!children.TryGetValue(n.Parent, out list)) children[n.Parent] = list = new List<GalaxyNode>();
                list.Add(n);
            }
            if (roots.Count == 0) // no node tagged ROOT: take the top-level nodes as roots
                foreach (var n in nodes) if (n.Parent == null) roots.Add(n.Id);
            foreach (var n in nodes)
            {
                if (n.Parent == null || !roots.Contains(n.Parent)) continue;
                if (!n.Tags.Contains("FAMILY") && n.Prefab != "Family Group") continue;
                var family = new CatalogueFamily { Name = n.Name };
                List<GalaxyNode> kids;
                if (children.TryGetValue(n.Id, out kids))
                    foreach (var kid in kids)
                        if (kid.Prefab == "Point of Interest") family.Pois.Add(kid.Name);
                // Everything under the family, breadth first.
                var queue = new Queue<GalaxyNode>();
                queue.Enqueue(n);
                var seen = new HashSet<string> { n.Id };
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    if (node != n)
                    {
                        var tags = new List<string>(node.Tags);
                        if (!string.IsNullOrEmpty(node.Prefab)) tags.AddRange(prefabTags(node.Prefab));
                        foreach (var tag in tags)
                        {
                            if (spawnTags.Contains(tag)) family.HasSpawn = true;
                            if (tag == GalaxyCheck.StationTag) family.HasStation = true;
                        }
                    }
                    List<GalaxyNode> below;
                    if (children.TryGetValue(node.Id, out below))
                        foreach (var b in below)
                            if (seen.Add(b.Id)) queue.Enqueue(b);
                }
                result.Add(family);
            }
            return result;
        }
    }
}
