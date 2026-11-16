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
        /// <summary>The English part of the name ("2824|Venov" gives "Venov"); a plain name as it is.</summary>
        public string DisplayName;
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
        /// <summary>The body's in-game name in English (its targeting name), else a tidy version of Name.</summary>
        public string Label;
        /// <summary>File name of its picture under galaxies/thumbs/, or null for none.</summary>
        public string Thumb;
    }

    /// <summary>Names as people read them, and safe file names, for the catalogue.</summary>
    public static class CatalogueNames
    {
        /// <summary>The English part of a text key ("5796|Space Station" gives "Space Station"); plain text as it is.</summary>
        public static string Display(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int bar = name.IndexOf('|');
            string text = bar >= 0 ? name.Substring(bar + 1).Trim() : name;
            return text.Length > 0 ? text : name;
        }

        /// <summary>
        /// A prefab name made readable: "Ship - " / "Prop - " dropped, underscores as spaces, words split where a
        /// capital follows a small letter or a digit ("DS4219_DarkStar" gives "DS4219 Dark Star").
        /// </summary>
        public static string Tidy(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string s = name.Trim();
            foreach (var prefix in new[] { "Ship - ", "Prop - " })
                if (s.StartsWith(prefix, StringComparison.Ordinal) && s.Length > prefix.Length) s = s.Substring(prefix.Length);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i] == '_' ? ' ' : s[i];
                if (ch == ' ')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    continue;
                }
                if (char.IsUpper(ch) && sb.Length > 0 && (char.IsLower(sb[sb.Length - 1]) || char.IsDigit(sb[sb.Length - 1]))) sb.Append(' ');
                sb.Append(ch);
            }
            string tidy = sb.ToString().Trim();
            return tidy.Length > 0 ? tidy : name;
        }

        /// <summary>A file name for a name: letters, digits, '-' and '_' kept, anything else '_', then ".png".</summary>
        public static string PngFile(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name ?? "")
            {
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
                char c = ok ? ch : '_';
                if (c == '_' && sb.Length > 0 && sb[sb.Length - 1] == '_') continue;
                sb.Append(c);
            }
            string stem = sb.ToString().Trim('_');
            if (stem.Length == 0) stem = "unnamed";
            if (stem.Length > 80) stem = stem.Substring(0, 80);
            return stem + ".png";
        }

        /// <summary>PngFile, made unique among the names already in used (case-insensitively) and added to it.</summary>
        public static string UniquePngFile(string name, HashSet<string> used)
        {
            string file = PngFile(name);
            string stem = file.Substring(0, file.Length - 4);
            for (int k = 2; used.Contains(file.ToLowerInvariant()); k++) file = stem + "-" + k + ".png";
            used.Add(file.ToLowerInvariant());
            return file;
        }
    }

    /// <summary>
    /// What the game has to offer a galaxy file, written by the mod from inside the game (the assets can't be read
    /// offline) and read by the designer and the check. Without it nothing in a file can be checked.
    /// </summary>
    public class Catalogue
    {
        /// <summary>
        /// The catalogue format. 2: display names, labels, pictures, sprite files. A file without the field is 1.
        /// (The mod no longer writes catalogues; the designer reads the game files. Kept for the designer's model.)
        /// </summary>
        public const int CurrentFormat = 2;

        public int Format = CurrentFormat;
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
        /// <summary>Sprite name to its PNG file under galaxies/sprites/ (family backdrops, map icons).</summary>
        public Dictionary<string, string> SpriteFiles = new Dictionary<string, string>();

        public static Catalogue Parse(string json)
        {
            try
            {
                var root = Json.Obj(MiniJson.Parse(json), "the catalogue");
                var c = new Catalogue();
                c.Format = (int)Json.Num(root, "catalogueFormat", "the catalogue", false, 1);
                foreach (var so in Json.List(root, "trenchSystems", "the catalogue", false))
                {
                    var d = Json.Obj(so, "a Trench system");
                    string name = Json.Str(d, "name", "a Trench system", true, null);
                    string where = "Trench system '" + name + "'";
                    var s = new CatalogueSystem { Name = name };
                    s.DisplayName = Json.Str(d, "displayName", where, false, null) ?? CatalogueNames.Display(name);
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
                    string pwhere = "prefab '" + name + "'";
                    c.Prefabs.Add(new CataloguePrefab
                    {
                        Name = name,
                        Kind = Json.Str(d, "kind", pwhere, false, "other"),
                        Radius = Json.Num(d, "radius", pwhere, false, 0),
                        Label = Json.Str(d, "label", pwhere, false, null) ?? CatalogueNames.Tidy(name),
                        Thumb = Json.Str(d, "thumb", pwhere, false, null),
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
                if (root.ContainsKey("spriteFiles") && root["spriteFiles"] != null)
                    foreach (var kv in Json.Obj(root["spriteFiles"], "spriteFiles"))
                    {
                        var file = kv.Value as string;
                        if (file == null) throw new FormatException("spriteFiles: '" + kv.Key + "' should be text");
                        c.SpriteFiles[kv.Key] = file;
                    }
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
            root["catalogueFormat"] = Format;
            root["background"] = Background;
            root["universe"] = new Dictionary<string, object>
            {
                { "minX", UniverseMinX }, { "minZ", UniverseMinZ }, { "width", UniverseWidth }, { "height", UniverseHeight },
            };
            var systems = new List<object>();
            foreach (var s in TrenchSystems)
            {
                var d = new Dictionary<string, object>();
                d["name"] = s.Name;
                d["displayName"] = s.DisplayName ?? CatalogueNames.Display(s.Name);
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
                prefabs.Add(new Dictionary<string, object>
                {
                    { "name", p.Name }, { "kind", p.Kind }, { "radius", p.Radius },
                    { "label", p.Label ?? CatalogueNames.Tidy(p.Name) }, { "thumb", p.Thumb },
                });
            root["prefabs"] = prefabs;
            root["tags"] = Json.StringList(Tags);
            root["skyboxes"] = Json.StringList(Skyboxes);
            root["spawnTags"] = Json.StringList(SpawnTags);
            root["sprites"] = Json.StringList(Sprites);
            var spriteFiles = new Dictionary<string, object>();
            foreach (var kv in SpriteFiles) spriteFiles[kv.Key] = kv.Value;
            root["spriteFiles"] = spriteFiles;
            return MiniJson.Write(root);
        }
    }
}
