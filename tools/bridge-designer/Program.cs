// bridge-designer: a standalone bridge viewer (the designer to come) for Star Trek: Bridge Crew, outside the game.
//
// It reads the bridges straight out of the game's own files on this PC (with the mod's reader, src/AssetFile.cs,
// and the glb exporter, tools/assets/Export.cs) and serves them, with the page in web/ (built into the program), to your browser on this PC
// only (127.0.0.1). Nothing from the game is copied anywhere else or kept on disk.
//
// Layouts (what was moved, turned or hidden) are saved to BepInEx/bridges/<scene>.json in the game folder, the
// previous one kept as .json.bak; the mod puts a bridge's layout on whenever that bridge loads.
//
// Usage: dotnet run -- [--game <game folder>] [--port 8770] [--no-open] [--web <folder>] [--layouts <folder>] [--models <folder>]
//        (the game folder also from $GAME_DIR; otherwise the folder this program is in, if it's in the game's folder
//        as the installers put it, or the game in your Steam libraries. --web serves the page from a folder,
//        e.g. web/ in the source, to try changes to it without a rebuild.)
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

string gameDir = Environment.GetEnvironmentVariable("GAME_DIR");
int port = 8770;
int? portArg = null;
bool open = true;
string webArg = null, layoutsArg = null, modelsArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameDir = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = (portArg = int.Parse(args[++i])).Value;
    else if (args[i] == "--no-open") open = false;
    else if (args[i] == "--web" && i + 1 < args.Length) webArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--layouts" && i + 1 < args.Length) layoutsArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--models" && i + 1 < args.Length) modelsArg = Path.GetFullPath(args[++i]);
    else return Fail("usage: bridge-designer [--game <folder>] [--port 8770] [--no-open] [--web <folder>] [--layouts <folder>] [--models <folder>]");
}
// The game: given, beside this program (installed in the game's folder), or in one of Steam's libraries.
gameDir ??= GameFinder.Find();
while (gameDir == null || !GameFinder.IsGame(gameDir))
{
    Console.Error.WriteLine(gameDir == null
        ? "Couldn't find Star Trek: Bridge Crew in your Steam libraries."
        : $"The game isn't in {gameDir} (there's no stbc_Data folder in it).");
    if (Console.IsInputRedirected) return Fail("Give the game's folder with --game.");
    Console.Write("Type or paste the game's folder (the one with stbc.exe in it), then press Enter: ");
    string typed = Console.ReadLine()?.Trim().Trim('"');
    if (string.IsNullOrEmpty(typed)) return Fail(null);
    gameDir = typed;
}
string dataDir = Path.Combine(gameDir, "stbc_Data");
var bridges = Bridges(dataDir);
Console.WriteLine($"Game: {gameDir}");
Console.WriteLine($"Bridges: {string.Join(", ", bridges.Select(b => $"{b.Name} (level{b.Level})"))}");

// The page: inside this program, or from a folder with --web (to try changes to it without a rebuild).
var page = new Page(webArg);
string layouts = layoutsArg ?? Path.Combine(gameDir, "BepInEx", "bridges");
// Models to add to a bridge (.obj with their .mtl and textures): the mod reads them from the game's folder.
string models = modelsArg ?? Path.Combine(gameDir, "BepInEx", "bridges", "models");
Console.WriteLine($"Layouts: {layouts}");
Console.WriteLine($"Models: {models}{(Directory.Exists(models) ? "" : " (not there yet: put .obj models in it to add them to bridges)")}");
var cache = new Dictionary<int, byte[]>();
var lightmaps = new Dictionary<(int, int), byte[]>();
HttpListener listener = null;
string url = null;
for (int p = port; p < port + 10 && listener == null; p++)
{
    string tryUrl = $"http://127.0.0.1:{p}/";
    // The designer already open (started twice): its page again, and this one goes.
    if (portArg == null && await AlreadyRunning(tryUrl))
    {
        Console.WriteLine($"The bridge designer is already running: {tryUrl}");
        if (open) OpenBrowser(tryUrl);
        return 0;
    }
    var l = new HttpListener();
    l.Prefixes.Add(tryUrl);
    try { l.Start(); listener = l; url = tryUrl; }
    catch (HttpListenerException e)
    {
        l.Close();
        if (portArg != null) return Fail($"Can't listen on {tryUrl}: {e.Message} (try another --port)");
    }
}
if (listener == null) return Fail($"Can't listen on ports {port} to {port + 9}: they're all in use (try --port)");
Console.WriteLine();
Console.WriteLine($"The bridge designer is open in your browser: {url}");
Console.WriteLine("Keep this window open while you design; close it when you're done.");
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
            Send(res, "application/json", Encoding.UTF8.GetBytes(new JsonArray(bridges.Select(b => (JsonNode)new JsonObject { ["level"] = b.Level, ["scene"] = b.Scene, ["name"] = b.Name }).ToArray()).ToJsonString()));
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
            Send(res, "application/json", Encoding.UTF8.GetBytes(new JsonArray(list.Select(f => (JsonNode)f).ToArray()).ToJsonString()));
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
        var (type, bytes) = page.Get(path == "/" ? "index.html" : path.TrimStart('/'));
        if (bytes == null) { res.StatusCode = 404; res.Close(); return; }
        Send(res, type, bytes);
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
    res.Headers["X-Bridge-Designer"] = "1";
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

// Ours, answering on that address? (The bridge list is only ours.)
static async Task<bool> AlreadyRunning(string url)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var r = await http.GetAsync(url + "api/bridges");
        return r.IsSuccessStatusCode && r.Headers.TryGetValues("X-Bridge-Designer", out _);
    }
    catch { return false; }
}

// A problem said, and (started from a double-click) the window kept open to read it.
static int Fail(string message)
{
    if (message != null) Console.Error.WriteLine(message);
    if (!Console.IsInputRedirected && OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
    return 1;
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
