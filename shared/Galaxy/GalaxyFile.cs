using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace StbcRevival.Galaxy
{
    /// <summary>The second page of the warp map: its button label and its background art.</summary>
    public class Page
    {
        /// <summary>The text on the page button.</summary>
        public string Label;
        /// <summary>"trench-darkened" (the Trench art, darkened) or the name of a PNG next to the json.</summary>
        public string Background;
    }

    /// <summary>One body in a system (a star, a planet, a station...), placed under a parent or at the system's origin.</summary>
    public class GalaxyNode
    {
        public string Id;
        /// <summary>Id of the node this one orbits or sits in, or null for a top-level node.</summary>
        public string Parent;
        public string Name;
        /// <summary>The game's SectorBody prefab name, from the catalogue.</summary>
        public string Prefab;
        public float X, Y, Z;
        /// <summary>One of GalaxyFile.Units; what the position is measured in.</summary>
        public string Unit = "Kilometers";
        public float RotX, RotY, RotZ;
        /// <summary>The game's tag names (jump point, station, ...), from the catalogue.</summary>
        public List<string> Tags = new List<string>();
        public float LandingRadius = 1000f;
        /// <summary>Text on the family's map entry; null leaves the game's default.</summary>
        public string MapName;
        /// <summary>Sprite name for the family's map background; null for none.</summary>
        public string FamilyBackground;
        /// <summary>Sprite name for the family's map icon; null for none.</summary>
        public string MapIcon;
        /// <summary>When set, this top-level node is a copy of a Trench family and has no body of its own.</summary>
        public CopyFamily CopyFamily;
    }

    /// <summary>Stands in for a whole Trench family: the game's own family, copied under this system.</summary>
    public sealed class CopyFamily
    {
        public string System;
        public string Family;
    }

    /// <summary>A system of the new region, built by copying a Trench system's look and feel.</summary>
    public class GalaxySystem
    {
        /// <summary>Stable and unique in the file; the name can change, the id doesn't.</summary>
        public string Id;
        public string Name;
        /// <summary>Where the system sits on the second page, in the Trench's universe units.</summary>
        public float X, Z;
        /// <summary>The Trench system to copy skybox, lights, tags and map sprites from.</summary>
        public string Template;
        /// <summary>Another skybox path to use instead of the template's; null keeps it.</summary>
        public string Skybox;
        /// <summary>System tags to use instead of the template's; null keeps them.</summary>
        public List<string> SystemTags;
        public List<GalaxyNode> Nodes = new List<GalaxyNode>();
    }

    /// <summary>
    /// The galaxy file the designer writes and the mod reads. Pure data and JSON, no Unity, so the same code runs in
    /// the game (net35) and in the tool (net8). A bad file throws FormatException saying where; the mod logs that and
    /// ignores the file rather than half-applying it.
    /// </summary>
    public class GalaxyFile
    {
        public static readonly string[] Units = { "Kilometers", "Skybox", "Lightseconds", "AstronomicalUnits" };

        public int Format = 1;
        public string Name;
        public Page Page = new Page { Label = "", Background = "trench-darkened" };
        public List<GalaxySystem> Systems = new List<GalaxySystem>();

        public static GalaxyFile Parse(string json)
        {
            try
            {
                var root = Json.Obj(MiniJson.Parse(json), "the file");
                var f = new GalaxyFile();
                f.Format = (int)Json.Num(root, "format", "the file", true, 1);
                f.Name = Json.Str(root, "name", "the file", true, "");
                if (root.ContainsKey("page") && root["page"] != null)
                {
                    var p = Json.Obj(root["page"], "page");
                    f.Page = new Page
                    {
                        Label = Json.Str(p, "label", "page", false, f.Name),
                        Background = Json.Str(p, "background", "page", false, "trench-darkened"),
                    };
                }
                else f.Page = new Page { Label = f.Name, Background = "trench-darkened" };

                var seen = new Dictionary<string, bool>();
                foreach (var so in Json.List(root, "systems", "the file", true))
                {
                    var sys = ParseSystem(so);
                    if (seen.ContainsKey(sys.Id)) throw new FormatException("Two systems have the id '" + sys.Id + "'");
                    seen[sys.Id] = true;
                    f.Systems.Add(sys);
                }
                return f;
            }
            catch (FormatException) { throw; }
            catch (Exception e)
            {
                // Anything else from bad input (a wrong type, a number out of range) is still just a bad file.
                throw new FormatException("Galaxy file: " + e.Message, e);
            }
        }

        private static GalaxySystem ParseSystem(object o)
        {
            var d = Json.Obj(o, "a system");
            string id = Json.Str(d, "id", "a system", true, null);
            string where = "system '" + id + "'";
            var s = new GalaxySystem();
            s.Id = id;
            s.Name = Json.Str(d, "name", where, true, null);
            float[] pos = Json.Floats(d, "position", where, 2, true);
            s.X = pos[0]; s.Z = pos[1];
            s.Template = Json.Str(d, "template", where, true, null);
            s.Skybox = Json.Str(d, "skybox", where, false, null);
            s.SystemTags = d.ContainsKey("systemTags") && d["systemTags"] != null ? Json.Strings(d, "systemTags", where) : null;
            var seen = new Dictionary<string, bool>();
            if (d.ContainsKey("nodes") && d["nodes"] != null)
                foreach (var no in Json.List(d, "nodes", where, false))
                {
                    var n = ParseNode(no, id);
                    if (seen.ContainsKey(n.Id)) throw new FormatException("System '" + id + "' has two nodes with the id '" + n.Id + "'");
                    seen[n.Id] = true;
                    s.Nodes.Add(n);
                }
            return s;
        }

        private static GalaxyNode ParseNode(object o, string systemId)
        {
            var d = Json.Obj(o, "a node in '" + systemId + "'");
            string id = Json.Str(d, "id", "a node in '" + systemId + "'", true, null);
            string where = "node '" + systemId + "/" + id + "'";
            var n = new GalaxyNode();
            n.Id = id;
            n.Parent = Json.Str(d, "parent", where, false, null);
            n.Name = Json.Str(d, "name", where, false, id);
            if (d.ContainsKey("copyFamily") && d["copyFamily"] != null)
            {
                var c = Json.Obj(d["copyFamily"], where + ": copyFamily");
                n.CopyFamily = new CopyFamily
                {
                    System = Json.Str(c, "system", where + ": copyFamily", true, null),
                    Family = Json.Str(c, "family", where + ": copyFamily", true, null),
                };
                if (n.Parent != null) throw new FormatException(where + ": a copied family can't have a parent");
            }
            n.Prefab = Json.Str(d, "prefab", where, n.CopyFamily == null, null);
            float[] pos = d.ContainsKey("position") || n.CopyFamily == null
                ? Json.Floats(d, "position", where, 3, true) : new float[3];
            n.X = pos[0]; n.Y = pos[1]; n.Z = pos[2];
            n.MapName = Json.Str(d, "mapName", where, false, null);
            n.FamilyBackground = Json.Str(d, "familyBackground", where, false, null);
            n.MapIcon = Json.Str(d, "mapIcon", where, false, null);
            n.Unit = Json.Str(d, "unit", where, false, "Kilometers");
            if (Array.IndexOf(Units, n.Unit) < 0)
                throw new FormatException(where + ": unknown unit '" + n.Unit + "' (use " + string.Join(", ", Units) + ")");
            if (d.ContainsKey("rotation") && d["rotation"] != null)
            {
                float[] rot = Json.Floats(d, "rotation", where, 3, false);
                n.RotX = rot[0]; n.RotY = rot[1]; n.RotZ = rot[2];
            }
            n.Tags = d.ContainsKey("tags") && d["tags"] != null ? Json.Strings(d, "tags", where) : new List<string>();
            n.LandingRadius = (float)Json.Num(d, "landingRadius", where, false, 1000);
            return n;
        }

        /// <summary>The file as JSON text, in the shape the spec shows.</summary>
        public string ToJson()
        {
            var root = new Dictionary<string, object>();
            root["format"] = Format;
            root["name"] = Name;
            var page = new Dictionary<string, object>();
            page["label"] = Page != null ? Page.Label : "";
            page["background"] = Page != null ? Page.Background : "trench-darkened";
            root["page"] = page;
            var systems = new List<object>();
            foreach (var s in Systems) systems.Add(SystemToJson(s));
            root["systems"] = systems;
            return MiniJson.Write(root);
        }

        internal static Dictionary<string, object> SystemToJson(GalaxySystem s)
        {
            var d = new Dictionary<string, object>();
            d["id"] = s.Id;
            d["name"] = s.Name;
            d["position"] = new List<object> { s.X, s.Z };
            d["template"] = s.Template;
            d["skybox"] = s.Skybox;
            d["systemTags"] = s.SystemTags == null ? null : Json.StringList(s.SystemTags);
            var nodes = new List<object>();
            foreach (var n in s.Nodes) nodes.Add(NodeToJson(n));
            d["nodes"] = nodes;
            return d;
        }

        internal static Dictionary<string, object> NodeToJson(GalaxyNode n)
        {
            var d = new Dictionary<string, object>();
            d["id"] = n.Id;
            d["parent"] = n.Parent;
            d["name"] = n.Name;
            d["prefab"] = n.Prefab;
            d["position"] = new List<object> { n.X, n.Y, n.Z };
            d["unit"] = n.Unit;
            d["rotation"] = new List<object> { n.RotX, n.RotY, n.RotZ };
            d["tags"] = Json.StringList(n.Tags);
            d["landingRadius"] = n.LandingRadius;
            if (n.MapName != null) d["mapName"] = n.MapName;
            if (n.FamilyBackground != null) d["familyBackground"] = n.FamilyBackground;
            if (n.MapIcon != null) d["mapIcon"] = n.MapIcon;
            if (n.CopyFamily != null)
                d["copyFamily"] = new Dictionary<string, object> { { "system", n.CopyFamily.System }, { "family", n.CopyFamily.Family } };
            return d;
        }

        /// <summary>
        /// Lowercase hex SHA-256 of the text as UTF-8 with CRLF turned into LF, so the same file hashes the same on
        /// Windows and Linux. The host and every crewmate compare this to agree they have the same galaxy.
        /// </summary>
        public static string Hash(string json)
        {
            string text = (json ?? "").Replace("\r\n", "\n");
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    /// <summary>Typed reads from MiniJson's loose objects that fail with a FormatException saying where.</summary>
    internal static class Json
    {
        public static Dictionary<string, object> Obj(object o, string where)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) throw new FormatException(where + ": an object { } expected");
            return d;
        }

        public static string Str(Dictionary<string, object> d, string key, string where, bool required, string fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                if (required) throw new FormatException(where + ": '" + key + "' is missing");
                return fallback;
            }
            var s = v as string;
            if (s == null) throw new FormatException(where + ": '" + key + "' should be text");
            return s;
        }

        public static double Num(Dictionary<string, object> d, string key, string where, bool required, double fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                if (required) throw new FormatException(where + ": '" + key + "' is missing");
                return fallback;
            }
            if (!(v is double)) throw new FormatException(where + ": '" + key + "' should be a number");
            double x = (double)v;
            if (double.IsNaN(x) || double.IsInfinity(x)) throw new FormatException(where + ": '" + key + "' is too large a number");
            return x;
        }

        public static List<object> List(Dictionary<string, object> d, string key, string where, bool required)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                if (required) throw new FormatException(where + ": '" + key + "' is missing");
                return new List<object>();
            }
            var l = v as List<object>;
            if (l == null) throw new FormatException(where + ": '" + key + "' should be a list [ ]");
            return l;
        }

        public static float[] Floats(Dictionary<string, object> d, string key, string where, int count, bool required)
        {
            var l = List(d, key, where, required);
            if (l.Count != count) throw new FormatException(where + ": '" + key + "' should be " + count + " numbers");
            var r = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!(l[i] is double)) throw new FormatException(where + ": '" + key + "' should be " + count + " numbers");
                r[i] = (float)(double)l[i];
                // A number past float's range (1e39, 1e999) would put a body at infinity.
                if (float.IsNaN(r[i]) || float.IsInfinity(r[i]))
                    throw new FormatException(where + ": '" + key + "' has a number too large (" + ((double)l[i]).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")");
            }
            return r;
        }

        public static List<string> Strings(Dictionary<string, object> d, string key, string where)
        {
            var r = new List<string>();
            foreach (var o in List(d, key, where, false))
            {
                var s = o as string;
                if (s == null) throw new FormatException(where + ": '" + key + "' should be a list of text");
                r.Add(s);
            }
            return r;
        }

        public static List<object> StringList(List<string> l)
        {
            var r = new List<object>();
            if (l != null) foreach (var s in l) r.Add(s);
            return r;
        }
    }
}
