// bridge-designer: a standalone bridge viewer (the designer to come) for Star Trek: Bridge Crew, outside the game.
//
// It reads the bridges straight out of the game's own files on this PC (with the mod's reader, src/AssetFile.cs,
// and the glb exporter, tools/assets/Export.cs) and serves them, with the page in web/ (built into the program), to your browser on this PC
// only (127.0.0.1). Nothing from the game is copied anywhere else or kept on disk.
//
// Layouts (what was moved, turned or hidden) are saved to BepInEx/bridges/<scene>.json in the game folder, the
// previous one kept as .json.bak; the mod puts a bridge's layout on whenever that bridge loads. Each ship class of
// the mod's (BepInEx/ships/<Class>: the Nova, the Sovereign) has its own copy of the Aegis's bridge, which it flies on,
// listed as a bridge of its own, with a layout of its own (<scene>-<Class>.json).
//
// Science console placements (where the science console stands on each bridge, shipped with the mod) are saved into
// the mod's source, assets/bridges/science/<scene>.json, when the designer runs from it (dotnet run in the repo), or to
// the folder given with --science; otherwise that part of the page is hidden.
//
// Usage: dotnet run -- [--game <game folder>] [--port 8770] [--no-open] [--stay] [--web <folder>] [--layouts <folder>] [--models <folder>] [--bundles <folder>] [--science <folder>]
//        (it stops by itself once its page is closed; --stay keeps it running until Ctrl+C)
//        (the game folder also from $GAME_DIR; otherwise the folder this program is in, if it's in the game's folder
//        as the installers put it, or the game in your Steam libraries. --web serves the page from a folder,
//        e.g. web/ in the source, to try changes to it without a rebuild.)
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// "0.7.38+73872a7": the mod's version and the commit this was built from.
string version = typeof(GameFinder).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
Console.WriteLine($"Bridge designer {version}");
string gameDir = Environment.GetEnvironmentVariable("GAME_DIR");
int port = 8770;
int? portArg = null;
bool open = true, stay = false;
string webArg = null, layoutsArg = null, modelsArg = null, bundlesArg = null, scienceArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameDir = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = (portArg = int.Parse(args[++i])).Value;
    else if (args[i] == "--no-open") open = false;
    else if (args[i] == "--stay") stay = true;
    else if (args[i] == "--web" && i + 1 < args.Length) webArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--layouts" && i + 1 < args.Length) layoutsArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--models" && i + 1 < args.Length) modelsArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--bundles" && i + 1 < args.Length) bundlesArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--science" && i + 1 < args.Length) scienceArg = Path.GetFullPath(args[++i]);
    else return Fail("usage: bridge-designer [--game <folder>] [--port 8770] [--no-open] [--stay] [--web <folder>] [--layouts <folder>] [--models <folder>] [--bundles <folder>] [--science <folder>]");
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
string shipsDir = Path.Combine(gameDir, "BepInEx", "ships");
bridges.AddRange(ShipBridges(bridges, shipsDir));
Console.WriteLine($"Game: {gameDir}");
Console.WriteLine($"Bridges: {string.Join(", ", bridges.Select(b => $"{b.Name} (level{b.Level})"))}");

// The page: inside this program, or from a folder with --web (to try changes to it without a rebuild).
var page = new Page(webArg);
string layouts = layoutsArg ?? Path.Combine(gameDir, "BepInEx", "bridges");
// Models to add to a bridge (.obj with their .mtl and textures): the mod reads them from the game's folder.
string models = modelsArg ?? Path.Combine(gameDir, "BepInEx", "bridges", "models");
Console.WriteLine($"Layouts: {layouts}");
// The science console's placements: the mod's source (assets/bridges/science), found from where this runs, or given.
string science = scienceArg ?? ScienceFolder();
if (science != null) Console.WriteLine($"Science console placements: {science}");
Console.WriteLine($"Models: {models}{(Directory.Exists(models) ? "" : " (not there yet: put .obj models in it to add them to bridges)")}");
// Unity asset bundles to add prefabs from (built with Unity 5.4, LZ4): the mod loads them from the game's folder.
string bundles = bundlesArg ?? Path.Combine(gameDir, "BepInEx", "bridges", "bundles");
Console.WriteLine($"Bundles: {bundles}{(Directory.Exists(bundles) ? "" : " (not there yet: put Unity 5.4 asset bundles in it to add their prefabs to bridges)")}");
var unpacked = new Dictionary<string, (DateTime stamp, string folder, List<(string name, string file, long root)> prefabs, string error)>();
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
        Console.WriteLine($"The bridge designer is already running: {tryUrl} (close it first to run this one, {version})");
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
Console.WriteLine(stay ? "Ctrl+C to stop." : "It stops by itself when you close its page in the browser.");
if (open) OpenBrowser(url);

// Ends once its pages are all closed (none back within 10 s, a reload's time): started from a double-click it may
// have no window to close. Each page holds /api/alive open while it's there.
int pages = 0;
bool hadPage = false;
if (!stay)
    _ = Task.Run(async () =>
    {
        DateTime? none = null;
        while (true)
        {
            await Task.Delay(1000);
            if (Volatile.Read(ref pages) > 0 || !hadPage) { none = null; continue; }
            none ??= DateTime.UtcNow;
            if (DateTime.UtcNow - none.Value < TimeSpan.FromSeconds(10)) continue;
            Console.WriteLine("Its page is closed: the bridge designer has stopped.");
            Environment.Exit(0);
        }
    });

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
        if (path == "/api/alive")
        {
            // Held open while the page is there (server-sent events, a comment every 5 s to notice it's gone).
            Interlocked.Increment(ref pages);
            hadPage = true;
            try
            {
                res.ContentType = "text/event-stream";
                res.Headers["Cache-Control"] = "no-store";
                res.SendChunked = true;
                var ping = Encoding.UTF8.GetBytes(": here\n\n");
                while (true)
                {
                    res.OutputStream.Write(ping);
                    res.OutputStream.Flush();
                    Thread.Sleep(5000);
                }
            }
            catch { }
            finally
            {
                Interlocked.Decrement(ref pages);
                try { res.Abort(); } catch { }
            }
            return;
        }
        if (path == "/api/version")
        {
            Send(res, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(version));
            return;
        }
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
        if (path == "/api/bundles")
        {
            // Each bundle in the folder and the prefabs it offers (or why it can't be read).
            var list = new JsonArray();
            if (Directory.Exists(bundles))
                foreach (var f in Directory.GetFiles(bundles).Select(Path.GetFileName).Where(BundleFile).OrderBy(f => f))
                {
                    var u = Unpacked(f);
                    list.Add((JsonNode)new JsonObject { ["file"] = f, ["prefabs"] = new JsonArray(u.prefabs.Select(x => (JsonNode)x.name).ToArray()), ["error"] = u.error });
                }
            Send(res, "application/json", Encoding.UTF8.GetBytes(list.ToJsonString()));
            return;
        }
        if (path == "/api/bundle.glb")
        {
            // One prefab of a bundle as a .glb, in the game's space as the bridges are (the mod puts it where its root says).
            string file = ctx.Request.QueryString["file"], prefab = ctx.Request.QueryString["prefab"];
            if (file == null || prefab == null || !BundleFile(file) || file != Path.GetFileName(file) || !File.Exists(Path.Combine(bundles, file))) { res.StatusCode = 404; res.Close(); return; }
            var u = Unpacked(file);
            var hit = u.prefabs.FirstOrDefault(x => x.name == prefab);
            if (hit.name == null) { res.StatusCode = 404; Send(res, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(u.error ?? $"{prefab} isn't in {file}")); return; }
            try
            {
                var (glb, summary) = GlbExport.Build(u.folder, hit.file, dataDir, hit.root);
                Console.WriteLine($"{file}: {prefab}: {glb.Length / 1048576.0:0.0} MB");
                Console.Write(summary);
                Send(res, "model/gltf-binary", glb);
            }
            catch (Exception e) { res.StatusCode = 500; Send(res, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(e.Message)); }
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
                // A ship's bridge with no layout of yours yet: the one its ship comes with, if any (as the mod does).
                string read = File.Exists(layoutFile) ? layoutFile : ShippedLayout(sceneName, shipsDir);
                if (read == null) { res.StatusCode = 404; res.Close(); return; }
                Send(res, "application/json", File.ReadAllBytes(read));
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
        if (path.StartsWith("/api/science/"))
        {
            string sceneName = path["/api/science/".Length..];
            if (!bridges.Any(b => b.Scene == sceneName)) { res.StatusCode = 404; res.Close(); return; }
            // Not from the mod's source: no placements to make here.
            if (science == null) { res.StatusCode = 501; Send(res, "text/plain", Encoding.UTF8.GetBytes("only from the mod's source")); return; }
            string scienceFile = Path.Combine(science, sceneName + ".json");
            if (ctx.Request.HttpMethod == "GET")
            {
                // A class's copy of the Aegis without one of its own: the Aegis's (as the mod does).
                string read = File.Exists(scienceFile) ? scienceFile : sceneName.StartsWith("Bridge_scene_02-") ? Path.Combine(science, "Bridge_scene_02.json") : null;
                if (read == null || !File.Exists(read)) { res.StatusCode = 404; res.Close(); return; }
                res.Headers["X-Science-File"] = Path.GetFileName(read);
                Send(res, "application/json", File.ReadAllBytes(read));
                return;
            }
            if (ctx.Request.HttpMethod == "POST")
            {
                if (ctx.Request.ContentType?.StartsWith("application/json") != true) { res.StatusCode = 415; res.Close(); return; }
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                string problem = CheckScience(body);
                if (problem != null) { res.StatusCode = 400; Send(res, "text/plain", Encoding.UTF8.GetBytes(problem)); return; }
                Directory.CreateDirectory(science);
                File.WriteAllText(scienceFile, body);
                Console.WriteLine($"Saved {scienceFile}");
                Send(res, "text/plain", Encoding.UTF8.GetBytes(scienceFile));
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
        return CheckAdded(r);
    }
    catch (Exception e) { return "not a layout: " + e.Message; }
}

// What's wrong with a layout's (or a science placement's) added parts, or null.
static string CheckAdded(JsonElement r)
{
    if (r.TryGetProperty("added", out var added))
        foreach (var a in added.EnumerateArray())
        {
            bool model = a.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String;
            bool piece = a.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String && a.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number;
            bool prefab = a.TryGetProperty("bundle", out var bu) && bu.ValueKind == JsonValueKind.String && a.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.String;
            if (!model && !piece && !prefab) return "an added part that's neither a model, a bundle's prefab nor a bridge's piece";
            foreach (var v in new[] { "pivot", "move" }) if (a.GetProperty(v).GetArrayLength() != 3) return $"added part: {v} isn't x, y, z";
            a.GetProperty("turn").GetDouble();
        }
    return null;
}

// What's wrong with a science console placement from the page, or null (the mod's SciencePlacementFile reads it).
static string CheckScience(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        if (r.GetProperty("format").GetInt32() != 1) return "unknown format";
        var screen = r.GetProperty("screen");
        if (screen.GetProperty("pivot").GetArrayLength() != 3) return "the screen's pivot isn't x, y, z";
        screen.GetProperty("turn").GetDouble();
        screen.GetProperty("tilt").GetDouble();
        var size = screen.GetProperty("size");
        if (size.GetArrayLength() != 2 || size.EnumerateArray().Any(v => v.GetDouble() <= 0 || v.GetDouble() > 5)) return "the screen's size isn't two lengths up to 5 m";
        if (r.GetProperty("stand").GetArrayLength() != 3) return "where to stand isn't x, y, z";
        // The placement ships to every player; a model is only on this PC, so theirs would have a screen with no console.
        if (r.TryGetProperty("added", out var added))
            foreach (var a in added.EnumerateArray())
                if (a.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    return "the console can't be a model (" + m.GetString() + "): other players don't have it. Use a bridge's piece";
        return CheckAdded(r);
    }
    catch (Exception e) { return "not a science placement: " + e.Message; }
}

// The mod's source this runs from (dotnet run in the repo): its assets/bridges/science, or null.
static string ScienceFolder()
{
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "StbcRevival.csproj")) && Directory.Exists(Path.Combine(d.FullName, "assets", "bridges")))
                return Path.Combine(d.FullName, "assets", "bridges", "science");
    return null;
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

// Each ship class of the mod's (a folder in BepInEx/ships with its <Class>.obj) gets a copy of the bridge it flies on:
// the same scene, under a name of its own (<scene>-<Class>), so its layout is its own.
static List<(int Level, string Scene, string Name)> ShipBridges(List<(int Level, string Scene, string Name)> bridges, string shipsDir)
{
    var copies = new List<(int, string, string)>();
    if (!Directory.Exists(shipsDir)) return copies;
    // The game's bridge it flies on: the Aegis's.
    string[] shipBases = { "Bridge_scene_02" };
    foreach (string dir in Directory.GetDirectories(shipsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
    {
        string cls = Path.GetFileName(dir);
        if (!File.Exists(Path.Combine(dir, cls + ".obj")) || cls.Any(c => !char.IsLetterOrDigit(c))) continue;
        foreach (string baseScene in shipBases)
        {
            var b = bridges.FirstOrDefault(x => x.Scene == baseScene);
            if (b.Scene == null) continue;
            copies.Add((b.Level, baseScene + "-" + cls, $"{cls} (a copy of the Aegis)"));
        }
    }
    return copies;
}

// The layout a ship's bridge comes with (BepInEx/ships/<Class>/<scene>.json), or null.
static string ShippedLayout(string sceneName, string shipsDir)
{
    int dash = sceneName.LastIndexOf('-');
    if (dash <= 0) return null;
    string file = Path.Combine(shipsDir, sceneName[(dash + 1)..], sceneName[..dash] + ".json");
    return File.Exists(file) ? file : null;
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

// A bundle in the bundles folder: any file but Unity's .manifest and .meta beside it, and hidden ones.
static bool BundleFile(string name) => !name.StartsWith('.') && !name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

// A bundle unpacked (Bundle.Unpack) into a scratch folder, once for each time it's saved, and its prefabs.
(DateTime stamp, string folder, List<(string name, string file, long root)> prefabs, string error) Unpacked(string file)
{
    string full = Path.Combine(bundles, file);
    var stamp = File.GetLastWriteTimeUtc(full);
    lock (unpacked)
    {
        if (unpacked.TryGetValue(file, out var had) && had.stamp == stamp) return had;
        string folder = Path.Combine(Path.GetTempPath(), "bridge-designer-bundles", file + "-" + stamp.Ticks);
        try { if (had.folder != null && Directory.Exists(had.folder)) Directory.Delete(had.folder, true); } catch { }
        (DateTime, string, List<(string, string, long)>, string) result;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var files = Bundle.Unpack(full, folder);
            var prefabs = BundleContents.Prefabs(folder, files);
            result = (stamp, folder, prefabs, prefabs.Count == 0 ? "no prefabs in it" : null);
        }
        catch (Exception e) { result = (stamp, folder, new List<(string, string, long)>(), e.Message); }
        if (result.Item4 != null) Console.WriteLine($"Bundle {file}: {result.Item4}");
        return unpacked[file] = result;
    }
}
