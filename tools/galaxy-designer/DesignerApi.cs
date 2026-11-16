using System.Text;
using System.Text.RegularExpressions;
using StbcRevival;
using StbcRevival.Galaxy;

/// <summary>
/// The designer's API: the catalogue read from the game's files (GameCatalogue) with the pictures the game took
/// (galaxies/thumbs.json and thumbs/), its sprites and warp map art, and the galaxy files in the galaxies folder
/// (BepInEx/galaxies in the game). No sockets here, so tests drive it directly.
/// </summary>
public sealed class DesignerApi
{
    static readonly Regex NameOk = new Regex("^[a-z0-9-]+$");
    const string Json = "application/json; charset=utf-8";
    /// <summary>Names in the galaxies folder that are the game's own files, not galaxies.</summary>
    static readonly string[] Reserved = { "catalogue", "thumbs" };
    readonly string dir;
    readonly Func<GameData> reload;
    readonly object gate = new object();
    GameData game;

    /// <param name="galaxiesDir">BepInEx/galaxies: galaxy files, and the game's pictures (thumbs.json, thumbs/).</param>
    /// <param name="game">The catalogue read from the game's files, or why there is none.</param>
    /// <param name="reload">Reads the game's files again (POST /api/reload); null: not offered.</param>
    public DesignerApi(string galaxiesDir, GameData game, Func<GameData> reload = null)
    {
        dir = galaxiesDir;
        this.game = game ?? GameData.Failed("The game's files haven't been read.");
        this.reload = reload;
    }

    public GameData Game { get { lock (gate) return game; } }

    public (int status, string contentType, byte[] body) Handle(string method, string path, byte[] body)
    {
        try
        {
            if (path == "/api/catalogue" && method == "GET")
            {
                var g = Game;
                if (g.Catalogue == null) return Error(503, g.Error ?? "The game's files can't be read.");
                return (200, Json, Encoding.UTF8.GetBytes(CatalogueJson(g.Catalogue)));
            }
            if (path == "/api/reload" && method == "POST")
            {
                if (reload == null) return Error(404, "Not found");
                var fresh = reload();
                lock (gate) game = fresh;
                if (fresh.Catalogue == null) return Error(503, fresh.Error ?? "The game's files can't be read.");
                return Ok(MiniJson.Write(new Dictionary<string, object>
                {
                    ["systems"] = fresh.Catalogue.TrenchSystems.Count, ["prefabs"] = fresh.Catalogue.Prefabs.Count,
                }));
            }
            if (path == "/api/trench-map.png" && method == "GET")
            {
                var g = Game;
                string f = g.Folder != null && g.Catalogue?.Background != null && GameCatalogue.IsPictureFile(g.Catalogue.Background)
                    ? Path.Combine(g.Folder, g.Catalogue.Background) : null;
                return f != null && File.Exists(f) ? (200, "image/png", File.ReadAllBytes(f))
                    : Error(404, g.Catalogue == null ? g.Error ?? "No warp map art" : "The game's files have no warp map art");
            }
            if (path.StartsWith("/api/sprite/") && method == "GET")
            {
                string name = Uri.UnescapeDataString(path.Substring("/api/sprite/".Length));
                var g = Game;
                if (!GameCatalogue.IsPictureFile(name)) return Error(400, "Not a sprite's file name");
                string f = g.Folder == null ? null : Path.Combine(g.Folder, "sprites", name);
                return f != null && File.Exists(f) ? (200, "image/png", File.ReadAllBytes(f)) : Error(404, "No sprite " + name);
            }
            if (path.StartsWith("/api/thumb/") && method == "GET")
            {
                string name = Uri.UnescapeDataString(path.Substring("/api/thumb/".Length));
                if (!GameCatalogue.IsPictureFile(name)) return Error(400, "Not a picture's file name");
                string f = Path.Combine(dir, "thumbs", name);
                return File.Exists(f) ? (200, "image/png", File.ReadAllBytes(f)) : Error(404, "No picture " + name);
            }
            if (path == "/api/galaxies" && method == "GET")
            {
                var names = new List<object>();
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.json"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (Array.IndexOf(Reserved, n) < 0) names.Add(n);
                    }
                names.Sort((a, b) => string.CompareOrdinal((string)a, (string)b));
                return Ok(MiniJson.Write(names));
            }
            if (path == "/api/check" && method == "POST")
            {
                var warnings = new List<object>();
                var problems = Check(Text(body), warnings);
                return Ok(MiniJson.Write(new Dictionary<string, object> { ["problems"] = problems, ["warnings"] = warnings }));
            }
            if (path.StartsWith("/api/galaxy/") && (method == "GET" || method == "PUT"))
            {
                string name = Uri.UnescapeDataString(path.Substring("/api/galaxy/".Length));
                if (!NameOk.IsMatch(name)) return Error(400, "A galaxy's name is lower-case letters, digits and dashes");
                if (Array.IndexOf(Reserved, name) >= 0) return Error(400, $"'{name}' is the game's own file: pick another name");
                string file = Path.Combine(dir, name + ".json");
                if (method == "GET")
                    return File.Exists(file) ? (200, Json, File.ReadAllBytes(file)) : Error(404, "No galaxy named " + name);
                string text = Text(body);
                try { GalaxyFile.Parse(text); }
                catch (FormatException e) { return Error(400, e.Message); }
                Directory.CreateDirectory(dir);
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);
                // As sent, not re-serialised: the mod hashes the file's text. Written beside it and moved into place, so
                // a crash mid-write leaves the old file (and its .bak), never a cut-short one.
                string tmp = file + ".tmp";
                File.WriteAllBytes(tmp, body ?? Array.Empty<byte>());
                File.Move(tmp, file, true);
                return Ok(MiniJson.Write(new Dictionary<string, object> { ["saved"] = name }));
            }
        }
        catch (Exception e) { return Error(500, e.Message); }
        return Error(404, "Not found");
    }

    /// <summary>
    /// The catalogue with the game's pictures on its prefabs, and "picturesPending": true while the game hasn't taken
    /// them all yet (it does on the next visit to the shuttle).
    /// </summary>
    string CatalogueJson(Catalogue c)
    {
        ThumbIndex index = null;
        string indexFile = Path.Combine(dir, "thumbs.json");
        try { if (File.Exists(indexFile)) index = ThumbIndex.Parse(File.ReadAllText(indexFile)); }
        catch (Exception) { index = null; } // unreadable: as if there were none (the game takes the pictures again)
        string json;
        bool pending;
        lock (c)
        {
            pending = GameCatalogue.MergePictures(c, index, f => File.Exists(Path.Combine(dir, "thumbs", f)));
            json = c.ToJson();
        }
        return "{\"picturesPending\":" + (pending ? "true" : "false") + "," + json.Substring(json.IndexOf('{') + 1);
    }

    List<object> Check(string text, List<object> warnings = null)
    {
        var problems = new List<object>();
        GalaxyFile file;
        try { file = GalaxyFile.Parse(text); }
        catch (FormatException e) { problems.Add(e.Message); return problems; }
        // No game data: null, so only the checks that need none are made.
        List<string> advice;
        problems.AddRange(GalaxyCheck.Problems(file, Game.Catalogue, out advice));
        if (warnings != null) warnings.AddRange(advice);
        return problems;
    }

    static string Text(byte[] body) => body == null ? "" : Encoding.UTF8.GetString(body);
    static (int, string, byte[]) Ok(string json) => (200, Json, Encoding.UTF8.GetBytes(json));
    static (int, string, byte[]) Error(int status, string message)
        => (status, Json, Encoding.UTF8.GetBytes(MiniJson.Write(new Dictionary<string, object> { ["error"] = message })));
}
