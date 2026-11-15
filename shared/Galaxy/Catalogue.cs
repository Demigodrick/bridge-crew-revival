using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>A family of a Trench system: its Points of Interest, and whether a jump point arrives in it.</summary>
    public sealed class CatalogueFamily
    {
        public string Name;
        public List<string> Pois = new List<string>();
        /// <summary>Any node under the family carries a SpawnTags tag.</summary>
        public bool HasSpawn;
        /// <summary>Any node under the family is tagged SpaceStation.</summary>
        public bool HasStation;
    }

    /// <summary>A Trench system as the game has it, offered to the designer for reference and as a template.</summary>
    public class CatalogueSystem
    {
        public string Name;
        public float X, Z;
        public string Skybox;
        public List<string> SystemTags = new List<string>();
        public List<GalaxyNode> Nodes = new List<GalaxyNode>();
        public List<CatalogueFamily> Families = new List<CatalogueFamily>();
    }

    /// <summary>A SectorBody prefab the game has, with a size and a rough type so the designer can draw it.</summary>
    public class CataloguePrefab
    {
        public string Name;
        /// <summary>star, planet, moon, station, asteroids, nebula or other.</summary>
        public string Kind = "other";
        public double Radius;
    }

    /// <summary>
    /// What the game has to offer a galaxy file, written by the mod from inside the game (the assets can't be read
    /// offline) and read by the designer and the check. Without it nothing in a file can be checked.
    /// </summary>
    public class Catalogue
    {
        public List<CatalogueSystem> TrenchSystems = new List<CatalogueSystem>();
        public List<CataloguePrefab> Prefabs = new List<CataloguePrefab>();
        public List<string> Tags = new List<string>();
        public List<string> Skyboxes = new List<string>();
        /// <summary>The tag(s) that mark where players arrive (the jump point); the first is the one named in messages.</summary>
        public List<string> SpawnTags = new List<string>();
        /// <summary>The warp map's bounds, in the same units as system positions. Width 0 means unknown.</summary>
        public float UniverseMinX, UniverseMinZ, UniverseWidth, UniverseHeight;
        /// <summary>File name of the warp map background PNG exported next to the catalogue.</summary>
        public string Background;
        /// <summary>Sprite names usable as a family's background or map icon.</summary>
        public List<string> Sprites = new List<string>();

        public static Catalogue Parse(string json)
        {
            try
            {
                var root = Json.Obj(MiniJson.Parse(json), "the catalogue");
                var c = new Catalogue();
                foreach (var so in Json.List(root, "trenchSystems", "the catalogue", false))
                {
                    var d = Json.Obj(so, "a Trench system");
                    string name = Json.Str(d, "name", "a Trench system", true, null);
                    string where = "Trench system '" + name + "'";
                    var s = new CatalogueSystem { Name = name };
                    float[] pos = Json.Floats(d, "position", where, 2, true);
                    s.X = pos[0]; s.Z = pos[1];
                    s.Skybox = Json.Str(d, "skybox", where, false, null);
                    s.SystemTags = Json.Strings(d, "systemTags", where);
                    foreach (var no in Json.List(d, "nodes", where, false)) s.Nodes.Add(ReadNode(no, name));
                    foreach (var fo in Json.List(d, "families", where, false))
                    {
                        var fd = Json.Obj(fo, "a family in " + where);
                        string fname = Json.Str(fd, "name", "a family in " + where, true, null);
                        object hs;
                        s.Families.Add(new CatalogueFamily
                        {
                            Name = fname,
                            Pois = Json.Strings(fd, "pois", "family '" + fname + "'"),
                            HasSpawn = fd.TryGetValue("hasSpawn", out hs) && hs is bool && (bool)hs,
                            HasStation = fd.TryGetValue("hasStation", out hs) && hs is bool && (bool)hs,
                        });
                    }
                    c.TrenchSystems.Add(s);
                }
                foreach (var po in Json.List(root, "prefabs", "the catalogue", false))
                {
                    var d = Json.Obj(po, "a prefab");
                    string name = Json.Str(d, "name", "a prefab", true, null);
                    c.Prefabs.Add(new CataloguePrefab
                    {
                        Name = name,
                        Kind = Json.Str(d, "kind", "prefab '" + name + "'", false, "other"),
                        Radius = Json.Num(d, "radius", "prefab '" + name + "'", false, 0),
                    });
                }
                c.Tags = Json.Strings(root, "tags", "the catalogue");
                c.Skyboxes = Json.Strings(root, "skyboxes", "the catalogue");
                c.SpawnTags = Json.Strings(root, "spawnTags", "the catalogue");
                if (root.ContainsKey("universe") && root["universe"] != null)
                {
                    var u = Json.Obj(root["universe"], "universe");
                    c.UniverseMinX = (float)Json.Num(u, "minX", "universe", false, 0);
                    c.UniverseMinZ = (float)Json.Num(u, "minZ", "universe", false, 0);
                    c.UniverseWidth = (float)Json.Num(u, "width", "universe", false, 0);
                    c.UniverseHeight = (float)Json.Num(u, "height", "universe", false, 0);
                }
                c.Sprites = Json.Strings(root, "sprites", "the catalogue");
                c.Background = Json.Str(root, "background", "the catalogue", false, null);
                return c;
            }
            catch (FormatException) { throw; }
            catch (Exception e)
            {
                throw new FormatException("Catalogue: " + e.Message, e);
            }
        }

        private static GalaxyNode ReadNode(object o, string systemName)
        {
            // Same shape as a node in a galaxy file; reuse the file's reader through a one-node system.
            var wrapper = new Dictionary<string, object>
            {
                { "format", 1.0 }, { "name", "c" },
                { "systems", new List<object> { new Dictionary<string, object> {
                    { "id", systemName }, { "name", systemName }, { "position", new List<object> { 0.0, 0.0 } },
                    { "template", systemName }, { "nodes", new List<object> { o } } } } },
            };
            return GalaxyFile.Parse(MiniJson.Write(wrapper)).Systems[0].Nodes[0];
        }

        public string ToJson()
        {
            var root = new Dictionary<string, object>();
            var systems = new List<object>();
            foreach (var s in TrenchSystems)
            {
                var d = new Dictionary<string, object>();
                d["name"] = s.Name;
                d["position"] = new List<object> { s.X, s.Z };
                d["skybox"] = s.Skybox;
                d["systemTags"] = Json.StringList(s.SystemTags);
                var nodes = new List<object>();
                foreach (var n in s.Nodes) nodes.Add(GalaxyFile.NodeToJson(n));
                d["nodes"] = nodes;
                var fams = new List<object>();
                foreach (var f in s.Families)
                    fams.Add(new Dictionary<string, object> { { "name", f.Name }, { "pois", Json.StringList(f.Pois) }, { "hasSpawn", f.HasSpawn }, { "hasStation", f.HasStation } });
                d["families"] = fams;
                systems.Add(d);
            }
            root["trenchSystems"] = systems;
            var prefabs = new List<object>();
            foreach (var p in Prefabs)
                prefabs.Add(new Dictionary<string, object> { { "name", p.Name }, { "kind", p.Kind }, { "radius", p.Radius } });
            root["prefabs"] = prefabs;
            root["tags"] = Json.StringList(Tags);
            root["skyboxes"] = Json.StringList(Skyboxes);
            root["spawnTags"] = Json.StringList(SpawnTags);
            root["universe"] = new Dictionary<string, object>
            {
                { "minX", UniverseMinX }, { "minZ", UniverseMinZ }, { "width", UniverseWidth }, { "height", UniverseHeight },
            };
            root["sprites"] = Json.StringList(Sprites);
            root["background"] = Background;
            return MiniJson.Write(root);
        }
    }
}
