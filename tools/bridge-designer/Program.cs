// bridge-designer: a standalone bridge viewer (the designer to come) for Star Trek: Bridge Crew, outside the game.
//
// It reads the bridges straight out of the game's own files on this PC (with the mod's reader, src/AssetFile.cs,
// and the glb exporter, tools/assets/Export.cs) and serves them, with the page in web/, to your browser on this PC
// only (127.0.0.1). Nothing from the game is copied anywhere else or kept on disk.
//
// Usage: dotnet run -- [--game <game folder>] [--port 8770] [--no-open]
//        (the game folder also from $GAME_DIR; default: Steam's usual place)
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

string gameDir = Environment.GetEnvironmentVariable("GAME_DIR");
int port = 8770;
bool open = true;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameDir = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--no-open") open = false;
    else { Console.Error.WriteLine("usage: bridge-designer [--game <folder>] [--port 8770] [--no-open]"); return 1; }
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

string web = Path.Combine(AppContext.BaseDirectory, "web");
var cache = new Dictionary<int, byte[]>();
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
