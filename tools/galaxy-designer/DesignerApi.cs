using System.Text;
using System.Text.RegularExpressions;
using StbcRevival;
using StbcRevival.Galaxy;

/// <summary>
/// The designer's API over the galaxies folder (BepInEx/galaxies in the game): the catalogue and map the mod wrote,
/// and the galaxy files. No sockets here, so tests drive it directly.
/// </summary>
public sealed class DesignerApi
{
    static readonly Regex NameOk = new Regex("^[a-z0-9-]+$");
    const string Json = "application/json; charset=utf-8";
    readonly string dir;

    public DesignerApi(string galaxiesDir) { dir = galaxiesDir; }

    public (int status, string contentType, byte[] body) Handle(string method, string path, byte[] body)
    {
        try
        {
            if (path == "/api/catalogue" && method == "GET")
            {
                string f = Path.Combine(dir, "catalogue.json");
                return File.Exists(f) ? (200, Json, File.ReadAllBytes(f))
                    : Error(404, "No catalogue yet: in the game, press <CatalogueKey> at the shuttle (set [Galaxy] CatalogueKey first)");
            }
            if (path == "/api/trench-map.png" && method == "GET")
            {
                string f = Path.Combine(dir, "trench-map.png");
                return File.Exists(f) ? (200, "image/png", File.ReadAllBytes(f))
                    : Error(404, "No trench map yet: in the game, press <CatalogueKey> at the shuttle (set [Galaxy] CatalogueKey first)");
            }
            if (path == "/api/galaxies" && method == "GET")
            {
                var names = new List<object>();
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.json"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (n != "catalogue") names.Add(n);
                    }
                names.Sort((a, b) => string.CompareOrdinal((string)a, (string)b));
                return Ok(MiniJson.Write(names));
            }
            if (path == "/api/check" && method == "POST")
                return Ok(MiniJson.Write(new Dictionary<string, object> { ["problems"] = Check(Text(body)) }));
            if (path.StartsWith("/api/galaxy/") && (method == "GET" || method == "PUT"))
            {
                string name = Uri.UnescapeDataString(path.Substring("/api/galaxy/".Length));
                if (!NameOk.IsMatch(name)) return Error(400, "A galaxy's name is lower-case letters, digits and dashes");
                if (name == "catalogue") return Error(400, "'catalogue' is the game's own file: pick another name");
                string file = Path.Combine(dir, name + ".json");
                if (method == "GET")
                    return File.Exists(file) ? (200, Json, File.ReadAllBytes(file)) : Error(404, "No galaxy named " + name);
                string text = Text(body);
                try { GalaxyFile.Parse(text); }
                catch (FormatException e) { return Error(400, e.Message); }
                Directory.CreateDirectory(dir);
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);
                // As sent, not re-serialised: the mod hashes the file's text.
                File.WriteAllBytes(file, body ?? Array.Empty<byte>());
                return Ok(MiniJson.Write(new Dictionary<string, object> { ["saved"] = name }));
            }
        }
        catch (Exception e) { return Error(500, e.Message); }
        return Error(404, "Not found");
    }

    List<object> Check(string text)
    {
        var problems = new List<object>();
        GalaxyFile file;
        try { file = GalaxyFile.Parse(text); }
        catch (FormatException e) { problems.Add(e.Message); return problems; }
        Catalogue cat = new Catalogue();
        string cf = Path.Combine(dir, "catalogue.json");
        if (File.Exists(cf))
            try { cat = Catalogue.Parse(File.ReadAllText(cf)); }
            catch (FormatException e) { problems.Add("The catalogue can't be read: " + e.Message); }
        problems.AddRange(GalaxyCheck.Problems(file, cat));
        return problems;
    }

    static string Text(byte[] body) => body == null ? "" : Encoding.UTF8.GetString(body);
    static (int, string, byte[]) Ok(string json) => (200, Json, Encoding.UTF8.GetBytes(json));
    static (int, string, byte[]) Error(int status, string message)
        => (status, Json, Encoding.UTF8.GetBytes(MiniJson.Write(new Dictionary<string, object> { ["error"] = message })));
}
