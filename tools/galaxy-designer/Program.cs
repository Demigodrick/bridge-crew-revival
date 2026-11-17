// galaxy-designer: designs a galaxy region (new systems on a second page of the helm's warp map) for Star Trek: Bridge Crew.
// A development tool, not shipped with the mod (no package, no installer): run it from the source.
//
// It serves its page (web/, built into the program) to your browser on this PC only (127.0.0.1). What the game has
// to offer (the Trench's systems, the prefabs, tags, sprites and the warp map art) is read from the game's own files
// (GameCatalogue.cs), cached so later starts are quick; the pictures of the prefabs are the ones the game takes on
// the shuttle when [Galaxy] PictureKey is set (BepInEx/galaxies/thumbs.json and thumbs/). Galaxy files are saved to BepInEx/galaxies/<name>.json,
// the previous one kept as .json.bak.
//
// Usage: dotnet run -- [--game <game folder>] [--port 8771] [--no-open] [--stay] [--web <folder>] [--cache <folder>]
//                      [--rebuild] [--catalogue-out <file>] [--check <galaxy file>]
//        (it stops by itself once its page is closed; --stay keeps it running until Ctrl+C)
//        (the game folder also from $GAME_DIR; otherwise the folder this program is in, if it's in the game's folder,
//        or the game in your Steam libraries. --web serves the page from a folder,
//        e.g. web/ in the source, to try changes to it without a rebuild. --cache: where the game data read is kept
//        (default ~/.cache/galaxy-designer, %LOCALAPPDATA%\GalaxyDesigner\cache on Windows); --rebuild reads the
//        game's files again; --catalogue-out writes the catalogue read to a file and stops; --check checks a galaxy
//        file against the game's data, as the game will, and stops: exit code 1 if it has problems.)
using System.Diagnostics;
using System.Net;
using System.Text;
using StbcRevival.Galaxy;

// "0.7.42+abc1234": the mod's version and the commit this was built from.
string version = typeof(GameFinder).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
Console.WriteLine($"Galaxy designer {version}");
string gameDir = Environment.GetEnvironmentVariable("GAME_DIR");
int port = 8771;
int? portArg = null;
bool open = true, stay = false;
string webArg = null, cacheArg = null, catalogueOut = null, checkFile = null;
bool rebuild = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameDir = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = (portArg = int.Parse(args[++i])).Value;
    else if (args[i] == "--no-open") open = false;
    else if (args[i] == "--stay") stay = true;
    else if (args[i] == "--web" && i + 1 < args.Length) webArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--cache" && i + 1 < args.Length) cacheArg = Path.GetFullPath(args[++i]);
    else if (args[i] == "--rebuild") rebuild = true;
    else if (args[i] == "--catalogue-out" && i + 1 < args.Length) catalogueOut = Path.GetFullPath(args[++i]);
    else if (args[i] == "--check" && i + 1 < args.Length) checkFile = Path.GetFullPath(args[++i]);
    else return Fail("usage: galaxy-designer [--game <folder>] [--port 8771] [--no-open] [--stay] [--web <folder>] [--cache <folder>] [--rebuild] [--catalogue-out <file>]");
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
string galaxies = Path.Combine(gameDir, "BepInEx", "galaxies");
Console.WriteLine($"Game: {gameDir}");
Console.WriteLine($"Galaxies: {galaxies}");

// What the game has to offer, from its own files (or the cache of them).
string cacheRoot = cacheArg ?? GameCatalogue.DefaultCacheRoot();
if (GameCatalogue.IsInside(cacheRoot, gameDir))
    return Fail($"The cache ({cacheRoot}) can't be inside the game's folder: give another with --cache <folder>.");
GameData LoadGame(bool again)
{
    Console.WriteLine(again ? "Reading the game's files ..." : "Reading the game's map data ...");
    var g = GameCatalogue.Load(gameDir, cacheRoot, again, m => Console.WriteLine(m));
    if (g.Catalogue == null) { Console.Error.WriteLine(g.Error); return g; }
    var c = g.Catalogue;
    Console.WriteLine($"Game data: {c.TrenchSystems.Count} Trench systems, {c.TrenchSystems.Sum(s => s.Families.Count)} families, " +
        $"{c.Prefabs.Count} prefabs, {c.Sprites.Count} sprites ({(g.FromCache ? "from the cache" : "read from the game's files")} " +
        $"in {g.Took.TotalMilliseconds:0} ms; kept in {g.Folder})");
    return g;
}
var game = LoadGame(rebuild);
if (catalogueOut != null)
{
    if (game.Catalogue == null) return Fail(null);
    File.WriteAllText(catalogueOut, game.Catalogue.ToJson());
    Console.WriteLine($"Catalogue written to {catalogueOut}");
    return 0;
}
if (checkFile != null)   // the release's check: the game would refuse a file with problems (warnings are advice)
{
    if (game.Catalogue == null) return Fail(null);
    GalaxyFile file;
    try { file = GalaxyFile.Parse(File.ReadAllText(checkFile)); }
    catch (Exception e) when (e is FormatException || e is IOException) { Console.Error.WriteLine($"{checkFile}: {e.Message}"); return 1; }
    var problems = GalaxyCheck.Problems(file, game.Catalogue, out var warnings);
    foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
    foreach (var p in problems) Console.Error.WriteLine($"  PROBLEM: {p}");
    Console.WriteLine($"{Path.GetFileName(checkFile)}: {problems.Count} problem(s), {warnings.Count} warning(s)");
    return problems.Count == 0 ? 0 : 1;
}
string thumbsIndex = null;
try { if (File.Exists(Path.Combine(galaxies, "thumbs.json"))) thumbsIndex = File.ReadAllText(Path.Combine(galaxies, "thumbs.json")); }
catch (Exception) { } // can't be read: as if there were none
if (!ThumbIndex.IsCurrent(thumbsIndex))
    Console.WriteLine("No pictures of the prefabs yet: the game takes them on your next visit to the shuttle.");
Directory.CreateDirectory(galaxies);

var page = new Page(webArg);
// The guide: from the source folder next to web/ when --web is given (edits show without a rebuild), else built in.
string Guide()
{
    if (webArg != null)
    {
        string f = Path.Combine(Path.GetDirectoryName(webArg.TrimEnd(Path.DirectorySeparatorChar, '/')) ?? "", "GUIDE.md");
        if (File.Exists(f)) return File.ReadAllText(f);
    }
    using var s = typeof(Page).Assembly.GetManifestResourceStream("guide/GUIDE.md");
    if (s == null) return null;
    using var r = new StreamReader(s, System.Text.Encoding.UTF8);
    return r.ReadToEnd();
}
var api = new DesignerApi(galaxies, game, () => LoadGame(true), Guide);
HttpListener listener = null;
string url = null;
for (int p = port; p < port + 10 && listener == null; p++)
{
    string tryUrl = $"http://127.0.0.1:{p}/";
    // The designer already open (started twice): its page again, and this one goes.
    if (portArg == null && await AlreadyRunning(tryUrl))
    {
        Console.WriteLine($"The galaxy designer is already running: {tryUrl} (close it first to run this one, {version})");
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
Console.WriteLine($"The galaxy designer is open in your browser: {url}");
Console.WriteLine(stay ? "Ctrl+C to stop." : "It stops by itself when you close its page in the browser.");
if (open) OpenBrowser(url);

// Ends once its pages are all closed (none back within 10 s, a reload's time). Each page holds /api/alive open
// while it's there (the page must open it, e.g. new EventSource("/api/alive")).
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
            Console.WriteLine("Its page is closed: the galaxy designer has stopped.");
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
            Send(res, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(version));
            return;
        }
        if (path.StartsWith("/api/"))
        {
            string method = ctx.Request.HttpMethod;
            // Only from this page (not another site in the browser): it sends JSON, which a plain form can't.
            if ((method == "PUT" || method == "POST") && ctx.Request.ContentType?.StartsWith("application/json") != true)
            {
                Send(res, 415, "text/plain", Encoding.UTF8.GetBytes("send application/json"));
                return;
            }
            byte[] body;
            using (var ms = new MemoryStream()) { ctx.Request.InputStream.CopyTo(ms); body = ms.ToArray(); }
            var (status, type, bytes) = api.Handle(method, path, body);
            if (status == 200 && method == "PUT") Console.WriteLine($"Saved {path["/api/galaxy/".Length..]}");
            Send(res, status, type, bytes);
            return;
        }
        // The page's own files.
        var (ptype, pbytes) = page.Get(path == "/" ? "index.html" : path.TrimStart('/'));
        if (pbytes == null) { res.StatusCode = 404; res.Close(); return; }
        Send(res, 200, ptype, pbytes);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"{path}: {e}");
        try { Send(res, 500, "text/plain", Encoding.UTF8.GetBytes(e.Message)); } catch { }
    }
}

static void Send(HttpListenerResponse res, int status, string type, byte[] body)
{
    res.StatusCode = status;
    res.ContentType = type;
    res.ContentLength64 = body.Length;
    res.Headers["Cache-Control"] = "no-store";
    res.Headers["X-Galaxy-Designer"] = "1";
    res.OutputStream.Write(body);
    res.Close();
}

// Ours, answering on that address?
static async Task<bool> AlreadyRunning(string url)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var r = await http.GetAsync(url + "api/galaxies");
        return r.IsSuccessStatusCode && r.Headers.TryGetValues("X-Galaxy-Designer", out _);
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
