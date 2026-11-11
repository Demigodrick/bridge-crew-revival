// bridge-designer: a standalone bridge viewer (the designer to come) for Star Trek: Bridge Crew, outside the game.
//
// It reads the bridges straight out of the game's own files on this PC (with the mod's reader, src/AssetFile.cs,
// and the glb exporter, tools/assets/Export.cs) and serves them, with the page in web/, to your browser on this PC
// only (127.0.0.1). Nothing from the game is copied anywhere else or kept on disk.
//
// Layouts (what was moved, turned or hidden) are saved to BepInEx/bridges/<scene>.json in the game folder, the
// previous one kept as .json.bak; the mod puts a bridge's layout on whenever that bridge loads.
//
// Usage: dotnet run -- [--game <game folder>] [--port 8770] [--no-open] [--web <folder>] [--layouts <folder>] [--models <folder>]
//        (the game folder also from $GAME_DIR; default: Steam's usual place. --web serves the page from a folder,
//        e.g. web/ in the source, to try changes to it without a rebuild.)
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

string gameDir = Environment.GetEnvironmentVariable("GAME_DIR");
int port = 8770;
bool open = true;
string webArg = null, layoutsArg = null, modelsArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameDir = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--no-open") open = false;
    else if (args[i] == "--web" && i + 1 < args.Length) webArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--layouts" && i + 1 < args.Length) layoutsArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--models" && i + 1 < args.Length) modelsArg = Path.GetFullPath(args[++i]);
    else { Console.Error.WriteLine("usage: bridge-designer [--game <folder>] [--port 8770] [--no-open] [--web <folder>] [--layouts <folder>] [--models <folder>]"); return 1; }
}
gameDir ??= DefaultGameDir();
string dataDir = Path.Combine(gameDir, "stbc_Data");
if (!File.Exists(Path.Combine(dataDir, "globalgamemanagers")))
{
    Console.Error.WriteLine($"The game wasn't found in {gameDir}. Give its folder with --game (the one with stbc.exe in it).");
    return 1;
}
var bridges = Bridges(dataDir);
Console.WriteLine($"Game: {gameDir}");
Console.WriteLine($"Bridges: {string.Join(", ", bridges.Select(b => $"{b.Name} (level{b.Level})"))}");

string web = webArg ?? Path.Combine(AppContext.BaseDirectory, "web");
string layouts = layoutsArg ?? Path.Combine(gameDir, "BepInEx", "bridges");
// Models to add to a bridge (.obj with their .mtl and textures): the mod reads them from the game's folder.
string models = modelsArg ?? Path.Combine(gameDir, "BepInEx", "bridges", "models");
Console.WriteLine($"Layouts: {layouts}");
Console.WriteLine($"Models: {models}{(Directory.Exists(models) ? "" : " (not there yet: put .obj models in it to add them to bridges)")}");
var cache = new Dictionary<int, byte[]>();
var lightmaps = new Dictionary<(int, int), byte[]>();
var listener = new HttpListener();
string url = $"http://127.0.0.1:{port}/";
listener.Prefixes.Add(url);
try { listener.Start(); }
catch (HttpListenerException e) { Console.Error.WriteLine($"Can't listen on {url}: {e.Message} (try --port)"); return 1; }
Console.WriteLine($"Open {url} (Ctrl+C to stop)");
if (open) OpenBrowser(url);

while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = Task.Run(() => Serve(ctx));
}

void Serve(HttpListenerContext ctx)
{
    var res = ctx.Response;
    string path = ctx.Request.Url!.AbsolutePath;
    try
    {
        if (path == "/api/bridges")
        {
            Send(res, "application/json", JsonSerializer.SerializeToUtf8Bytes(bridges.Select(b => new { level = b.Level, scene = b.Scene, name = b.Name })));
            return;
        }
        if (path.StartsWith("/api/bridge/") && path.EndsWith(".glb") && int.TryParse(path["/api/bridge/level".Length..^4], out int level))
        {
            if (!bridges.Any(b => b.Level == level)) { res.StatusCode = 404; res.Close(); return; }
            byte[] glb;
            lock (cache)
            {
                if (!cache.TryGetValue(level, out glb))
                {
                    var sw = Stopwatch.StartNew();
                    (glb, string summary) = GlbExport.Build(dataDir, "level" + level);
                    cache[level] = glb;
                    Console.WriteLine($"level{level}: {glb.Length / 1048576.0:0.0} MB in {sw.ElapsedMilliseconds} ms");
                    Console.Write(summary);
                }
            }
            Send(res, "model/gltf-binary", glb);
            return;
        }
        if (path.StartsWith("/api/lightmap/level"))
        {
            // /api/lightmap/level6/0.png: one of a bridge's baked lightmaps.
            var parts = path["/api/lightmap/level".Length..].Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int lv) && parts[1].EndsWith(".png") && int.TryParse(parts[1][..^4], out int li) && bridges.Any(b => b.Level == lv))
            {
                byte[] png;
                lock (lightmaps) if (!lightmaps.TryGetValue((lv, li), out png)) lightmaps[(lv, li)] = png = GlbExport.Lightmap(dataDir, lv, li);
                if (png != null) { Send(res, "image/png", png); return; }
            }
            res.StatusCode = 404; res.Close(); return;
        }
        if (path == "/api/models")
        {
            var list = Directory.Exists(models)
                ? Directory.GetFiles(models, "*.obj", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(models, f).Replace('\\', '/')).OrderBy(f => f).ToArray()
                : Array.Empty<string>();
            Send(res, "application/json", JsonSerializer.SerializeToUtf8Bytes(list));
            return;
        }
        if (path.StartsWith("/api/models/"))
        {
            // A model's own files (the .obj, its .mtl and textures), from the models folder only.
            string modelRel = Uri.UnescapeDataString(path["/api/models/".Length..]);
            string full = Path.GetFullPath(Path.Combine(models, modelRel));
            if (!full.StartsWith(Path.GetFullPath(models) + Path.DirectorySeparatorChar) || !File.Exists(full)) { res.StatusCode = 404; res.Close(); return; }
            string mtype = Path.GetExtension(full).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => "text/plain" };
            Send(res, mtype, File.ReadAllBytes(full));
            return;
        }
        if (path.StartsWith("/api/layout/"))
        {
            string sceneName = path["/api/layout/".Length..];
            if (!bridges.Any(b => b.Scene == sceneName)) { res.StatusCode = 404; res.Close(); return; }
            string layoutFile = Path.Combine(layouts, sceneName + ".json");
            if (ctx.Request.HttpMethod == "GET")
            {
                if (!File.Exists(layoutFile)) { res.StatusCode = 404; res.Close(); return; }
                Send(res, "application/json", File.ReadAllBytes(layoutFile));
                return;
            }
            if (ctx.Request.HttpMethod == "POST")
            {
                // Only from this page (not another site in the browser): it sends JSON, which a plain form can't.
                if (ctx.Request.ContentType?.StartsWith("application/json") != true) { res.StatusCode = 415; res.Close(); return; }
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                string problem = CheckLayout(body, sceneName);
                if (problem != null) { res.StatusCode = 400; Send(res, "text/plain", Encoding.UTF8.GetBytes(problem)); return; }
                Directory.CreateDirectory(layouts);
                if (File.Exists(layoutFile)) File.Copy(layoutFile, layoutFile + ".bak", true);
                File.WriteAllText(layoutFile, body);
                Console.WriteLine($"Saved {layoutFile}");
                Send(res, "text/plain", Encoding.UTF8.GetBytes(layoutFile));
                return;
            }
            res.StatusCode = 405; res.Close(); return;
        }
        // The page's own files.
        string rel = path == "/" ? "index.html" : path.TrimStart('/');
        string file = Path.GetFullPath(Path.Combine(web, rel));
        if (!file.StartsWith(Path.GetFullPath(web) + Path.DirectorySeparatorChar) || !File.Exists(file)) { res.StatusCode = 404; res.Close(); return; }
        string type = Path.GetExtension(file) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" };
        Send(res, type, File.ReadAllBytes(file));
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"{path}: {e}");
        try { res.StatusCode = 500; Send(res, "text/plain", Encoding.UTF8.GetBytes(e.Message)); } catch { }
    }
}

// What's wrong with a layout from the page, or null: format 1, for this scene, and every change complete.
static string CheckLayout(string body, string sceneName)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        if (r.GetProperty("format").GetInt32() != 1) return "unknown format";
        if (r.GetProperty("scene").GetString() != sceneName) return "it's for another bridge";
        foreach (var p in r.GetProperty("pieces").EnumerateArray())
        {
            if (string.IsNullOrEmpty(p.GetProperty("path").GetString())) return "a change without a path";
            foreach (var v in new[] { "pivot", "move" }) if (p.GetProperty(v).GetArrayLength() != 3) return $"{v} isn't x, y, z";
            p.GetProperty("turn").GetDouble();
            p.GetProperty("hidden").GetBoolean();
        }
        if (r.TryGetProperty("added", out var added))
            foreach (var a in added.EnumerateArray())
            {
                bool model = a.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String;
                bool piece = a.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String && a.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number;
                if (!model && !piece) return "an added part that's neither a model nor a bridge's piece";
                foreach (var v in new[] { "pivot", "move" }) if (a.GetProperty(v).GetArrayLength() != 3) return $"added part: {v} isn't x, y, z";
                a.GetProperty("turn").GetDouble();
            }
        return null;
    }
    catch (Exception e) { return "not a layout: " + e.Message; }
}

static void Send(HttpListenerResponse res, string type, byte[] body)
{
    res.ContentType = type;
    res.ContentLength64 = body.Length;
    res.Headers["Cache-Control"] = "no-store";
    res.OutputStream.Write(body);
    res.Close();
}

// The game's scenes in build order (a scene's place is its levelN file), from the build settings in
// globalgamemanagers: the bridges are the ones under a Bridges folder.
static List<(int Level, string Scene, string Name)> Bridges(string dataDir)
{
    var bytes = File.ReadAllBytes(Path.Combine(dataDir, "globalgamemanagers"));
    var text = Encoding.ASCII.GetString(bytes);
    var found = new List<(int, string, string)>();
    int at = 0, index = 0;
    while ((at = text.IndexOf("Assets/", at, StringComparison.Ordinal)) >= 0)
    {
        int end = text.IndexOf(".unity", at, StringComparison.Ordinal);
        if (end < 0) break;
        string scene = text[at..(end + 6)];
        at = end + 6;
        if (scene.IndexOfAny(new[] { '\0', '\n' }) >= 0) continue;
        if (scene.Contains("/Bridges/"))
        {
            string name = Path.GetFileNameWithoutExtension(scene);
            string nice = name switch
            {
                "Bridge_scene_02" => "Aegis",
                "Retro_Bridge_Scene" => "Enterprise (original series)",
                "Bridge_scene_1701D" => "Enterprise-D",
                "Bridge_scene_Holodeck" => "Holodeck",
                "Aegis_Briefing_Room" => "Aegis briefing room",
                _ => name,
            };
            if (File.Exists(Path.Combine(dataDir, "level" + index))) found.Add((index, name, nice));
        }
        index++;
    }
    return found;
}

static string DefaultGameDir()
{
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var candidates = OperatingSystem.IsWindows()
        ? new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Star Trek Bridge Crew" }
        : new[] { Path.Combine(home, ".local/share/Steam/steamapps/common/Star Trek Bridge Crew"), Path.Combine(home, ".steam/steam/steamapps/common/Star Trek Bridge Crew") };
    return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else Process.Start("xdg-open", url);
    }
    catch { }
}
