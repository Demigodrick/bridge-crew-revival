using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using StbcRevival;
using StbcRevival.Galaxy;
using D = System.Collections.Generic.Dictionary<string, object>;

/// <summary>What the designer knows of the game: its catalogue, or why there is none, and where its pictures are.</summary>
public sealed class GameData
{
    /// <summary>Null when the game's files couldn't be read (Error says why).</summary>
    public Catalogue Catalogue;
    public string Error;
    /// <summary>The folder with the catalogue's sprite PNGs (sprites/) and the warp map art (trench-map.png).</summary>
    public string Folder;
    public bool FromCache;
    public TimeSpan Took;

    public static GameData Failed(string error) => new GameData { Error = error };
}

/// <summary>
/// Builds the designer's catalogue from the game's own files, offline: the Trench's systems and node trees, its
/// families, the SectorBody prefabs (the Trench's and the rest under Resources/Sector), the tag names, the sprites
/// the nodes use (as PNGs) and the warp map art. The files carry no type information: the script types' layouts
/// come from the game's Assembly-CSharp (tools/xref's Layout.cs and ScriptObjects.cs, linked into this program), the
/// map data is as specs/2026-11-16-map-data-layouts.md describes it. The result is cached (keyed by the game files'
/// sizes and dates), so only the first start after a game update reads them.
/// </summary>
public static class GameCatalogue
{
    /// <summary>Raised when what's cached changes shape, so older caches are built again.</summary>
    const int CacheFormat = 1;
    const string TrenchName = "0 - Sector - Trench A";
    /// <summary>The game's player spawn tags (MissionManager's default and alternate spawn tags).</summary>
    static readonly string[] SpawnTags = { "Player", "AltPlayerSpawn" };
    /// <summary>The warp map's universe when no warp panel is found: x −265..265, z −115..115.</summary>
    const float DefaultUniverseWidth = 530, DefaultUniverseHeight = 230;

    // ---- Cache ------------------------------------------------------------------------------------------------

    /// <summary>Where caches go: $XDG_CACHE_HOME (or ~/.cache)/galaxy-designer, %LOCALAPPDATA%\GalaxyDesigner\cache on Windows.</summary>
    public static string DefaultCacheRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalaxyDesigner", "cache");
        string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(xdg, "galaxy-designer");
    }

    /// <summary>The cache's key: the game files' names, sizes and dates (and this reader's version).</summary>
    public static string CacheKey(string gameDir)
    {
        var sb = new StringBuilder("galaxy-designer cache " + CacheFormat + "\n");
        string data = Path.Combine(gameDir, "stbc_Data");
        var files = new List<string>();
        foreach (var pattern in new[] { "*.assets", "*.resS", "level*", "globalgamemanagers" })
            files.AddRange(Directory.GetFiles(data, pattern));
        files.Add(Path.Combine(data, "Managed", "Assembly-CSharp.dll"));
        foreach (var f in files.Distinct().OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(f);
            sb.Append(Path.GetFileName(f)).Append('|');
            if (info.Exists) sb.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            sb.Append('\n');
        }
        return Hex(sb.ToString());
    }

    static string Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>A folder path made comparable: full, without a trailing separator.</summary>
    public static string NormalPath(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    /// <summary>Whether a folder is the other one or inside it.</summary>
    public static bool IsInside(string dir, string parent)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string d = NormalPath(dir) + Path.DirectorySeparatorChar, p = NormalPath(parent) + Path.DirectorySeparatorChar;
        return d.StartsWith(p, cmp);
    }

    /// <summary>
    /// Whether a cache folder has everything its catalogue names: catalogue.json, the background and each sprite's
    /// picture. A folder missing any is built again.
    /// </summary>
    public static bool IsComplete(string folder, Catalogue c)
    {
        if (c == null) return false;
        if (c.Background != null && (!IsPictureFile(c.Background) || !File.Exists(Path.Combine(folder, c.Background)))) return false;
        foreach (var f in c.SpriteFiles.Values)
            if (!IsPictureFile(f) || !File.Exists(Path.Combine(folder, "sprites", f))) return false;
        return true;
    }

    // One load at a time in this program (a start and a reload); between programs, a lock file in the game's cache.
    static readonly object loading = new object();

    /// <summary>
    /// The catalogue: from the cache when it's for these game files (and rebuild is false), else built from them and
    /// cached. Never throws: a game folder that can't be read gives an Error; a cache that can't be written is only
    /// a warning (the catalogue is then served from a temporary folder).
    /// </summary>
    public static GameData Load(string gameDir, string cacheRoot, bool rebuild, Action<string> log)
        => Load(gameDir, cacheRoot, rebuild, log, outDir => Build(gameDir, outDir, log));

    /// <summary>Load with the building given (tests build a small catalogue instead of reading a game).</summary>
    public static GameData Load(string gameDir, string cacheRoot, bool rebuild, Action<string> log, Func<string, Catalogue> build)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string data = Path.Combine(gameDir ?? "", "stbc_Data");
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(data))
            return GameData.Failed($"The game wasn't found{(string.IsNullOrEmpty(gameDir) ? "" : " in " + gameDir)} (no stbc_Data folder): start the designer with --game <the game's folder>.");
        string key;
        try { key = CacheKey(gameDir); }
        catch (Exception e) { return GameData.Failed($"The game's files in {data} can't be read: {e.Message}"); }

        // <cache>/<game folder's hash>/<the files' key>/: a new key (the game updated) is a new folder, so a folder
        // being served is never replaced by different data.
        string gameRoot = Path.Combine(cacheRoot, Hex(NormalPath(gameDir)).Substring(0, 16));
        string folder = Path.Combine(gameRoot, key.Substring(0, 24));
        lock (loading)
        {
            FileStream lockFile = null;
            try
            {
                lockFile = LockCache(gameRoot, log);
                if (!rebuild)
                {
                    try
                    {
                        string catFile = Path.Combine(folder, "catalogue.json");
                        if (File.Exists(catFile))
                        {
                            var cached = Catalogue.Parse(File.ReadAllText(catFile));
                            if (IsComplete(folder, cached))
                                return new GameData { Catalogue = cached, Folder = folder, FromCache = true, Took = clock.Elapsed };
                            log?.Invoke("The cached game data is missing pictures; reading the game's files again");
                        }
                    }
                    catch (Exception e) { log?.Invoke("The cached catalogue can't be read (" + e.Message + "); reading the game's files again"); }
                }

                // Built in a folder of its own: in the cache if it can be written, else a temporary one.
                string unique = $"building-{Environment.ProcessId}-{Guid.NewGuid():N}";
                string building = Path.Combine(gameRoot, unique);
                bool inCache = true;
                try { Directory.CreateDirectory(building); }
                catch (Exception e)
                {
                    log?.Invoke($"The cache ({gameRoot}) can't be written ({e.Message}); the game data is kept in a temporary folder this time");
                    inCache = false;
                    building = Path.Combine(Path.GetTempPath(), "galaxy-designer-" + unique);
                    try { Directory.CreateDirectory(building); }
                    catch (Exception e2) { return GameData.Failed($"No folder can be written for the game data's pictures ({e2.Message}); give one with --cache <folder>."); }
                }

                Catalogue c;
                try { c = build(building); }
                catch (Exception e)
                {
                    TryDelete(building);
                    return GameData.Failed($"The game's files in {data} can't be read: {e.Message}");
                }

                string served = building;
                if (inCache)
                    try
                    {
                        File.WriteAllText(Path.Combine(building, "catalogue.json"), c.ToJson(), new UTF8Encoding(false));
                        File.WriteAllText(Path.Combine(building, "key.txt"), key);
                        // In place of the folder for this key: the old one moved aside first, then removed.
                        string old = null;
                        if (Directory.Exists(folder))
                        {
                            old = Path.Combine(gameRoot, $"old-{Guid.NewGuid():N}");
                            Directory.Move(folder, old);
                        }
                        Directory.Move(building, folder);
                        served = folder;
                        if (old != null) TryDelete(old);
                        Tidy(gameRoot, folder);
                    }
                    catch (Exception e) { log?.Invoke($"The game data couldn't be cached ({e.Message}); it's read again next start"); }
                return new GameData { Catalogue = c, Folder = served, Took = clock.Elapsed };
            }
            finally { lockFile?.Dispose(); }
        }
    }

    // The cache's lock between programs (two designers started at once): a file held open with no sharing, waited
    // for up to 30 s. Without a writable cache, no lock (each then builds in a temporary folder of its own).
    static FileStream LockCache(string gameRoot, Action<string> log)
    {
        try { Directory.CreateDirectory(gameRoot); }
        catch (Exception) { return null; }
        string path = Path.Combine(gameRoot, ".lock");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException)
            {
                if (DateTime.UtcNow > until)
                {
                    log?.Invoke("Another galaxy designer is still reading the game's files; going ahead");
                    return null;
                }
                Thread.Sleep(200);
            }
        }
    }

    // Removes what's left of older game versions and of builds cut short (only folders an hour old: another
    // program may be building), keeping the current folder.
    static void Tidy(string gameRoot, string current)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(gameRoot))
            {
                if (NormalPath(d) == NormalPath(current)) continue;
                string name = Path.GetFileName(d);
                bool scratch = name.StartsWith("building-") || name.StartsWith("old-");
                if (!scratch || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(1)) TryDelete(d);
            }
        }
        catch (Exception) { }
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
    }

    // ---- Pictures ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts the pictures the game took (galaxies/thumbs.json and thumbs/) on the prefabs: a prefab's Thumb is its
    /// picture's file when that file is there, else null. True when pictures are still to come: no index yet, or
    /// one from a run cut short.
    /// </summary>
    public static bool MergePictures(Catalogue c, ThumbIndex index, Func<string, bool> pictureExists)
    {
        foreach (var p in c.Prefabs)
        {
            p.Thumb = null;
            if (index != null && p.Name != null && index.Pictures.TryGetValue(p.Name, out var pic)
                && IsPictureFile(pic.File) && pictureExists(pic.File))
                p.Thumb = pic.File;
        }
        return index == null || index.Format < ThumbIndex.CurrentFormat;
    }

    /// <summary>A plain PNG file name (letters, digits, '-', '_', '.'; no folders): what pictures and sprites are called.</summary>
    public static bool IsPictureFile(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 120 && name.EndsWith(".png", StringComparison.Ordinal) && !name.StartsWith(".")
        && name.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.') && !name.Contains("..");

    // ---- Pure parts -------------------------------------------------------------------------------------------

    /// <summary>A body's kind from its tags: star, station, moon, planet, asteroids, nebula or other.</summary>
    public static string Kind(ICollection<string> tags)
    {
        if (tags.Contains("Star")) return "star";
        if (tags.Contains("SpaceStation")) return "station";
        if (tags.Contains("Moon")) return "moon";
        if (tags.Contains("Planet")) return "planet";
        if (tags.Contains("Asteroid")) return "asteroids";
        if (tags.Contains("Nebula")) return "nebula";
        return "other";
    }

    /// <summary>
    /// A body's name as targeting shows it, in English: its TargetableComponent's _uiName ("5796|Space Station" gives
    /// "Space Station"); else the tidied prefab name ("Target" is the component's default, not a name).
    /// </summary>
    public static string Label(string uiName, string prefabName)
    {
        string text = CatalogueNames.Display(uiName);
        if (!string.IsNullOrEmpty(text) && text.Trim() != "Target") return text.Trim();
        return CatalogueNames.Tidy(prefabName);
    }

    /// <summary>
    /// A quaternion as the game's Unity (5.4) eulerAngles gives it: normalised first (some of the map's rotations are
    /// stored unnormalised), Z then X then Y, in degrees, each made positive as Unity does (a value just below 0 stays).
    /// </summary>
    public static float[] Euler(float x, float y, float z, float w)
    {
        double n = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
        if (n < 1e-12) return new float[3];
        double qx = x / n, qy = y / n, qz = z / n, qw = w / n;
        double sinX = 2 * (qw * qx - qy * qz), ex, ey, ez;
        if (Math.Abs(sinX) < 0.99995)
        {
            ex = Math.Asin(sinX);
            ey = Math.Atan2(2 * (qw * qy + qx * qz), 1 - 2 * (qx * qx + qy * qy));
            ez = Math.Atan2(2 * (qw * qz + qx * qy), 1 - 2 * (qx * qx + qz * qz));
        }
        else
        {
            // Looking straight up or down: Y and Z turn about the same axis; Unity puts it all in Y.
            double m01 = 2 * (qx * qy - qw * qz), m00 = 1 - 2 * (qy * qy + qz * qz);
            ex = Math.Sign(sinX) * Math.PI / 2;
            ey = Math.Atan2(sinX > 0 ? m01 : -m01, m00);
            ez = 0;
        }
        static float Deg(double r)
        {
            if (r < -0.0001) r += 2 * Math.PI;
            else if (r > 2 * Math.PI - 0.0001) r -= 2 * Math.PI;
            return (float)(r * 180 / Math.PI);
        }
        return new[] { Deg(ex), Deg(ey), Deg(ez) };
    }

    /// <summary>A node id from its name: lower case letters and digits, other runs as one '-'.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in (name ?? "").ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    // ---- Building ---------------------------------------------------------------------------------------------

    /// <summary>Reads the catalogue out of the game's files; sprite PNGs go to outDir/sprites, the map art to outDir/trench-map.png.</summary>
    public static Catalogue Build(string gameDir, string outDir, Action<string> log)
    {
        string data = Path.Combine(gameDir, "stbc_Data");
        string dll = Path.Combine(data, "Managed", "Assembly-CSharp.dll");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dll));
        using var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = resolver });
        using var objects = new ScriptObjects(data, asm.MainModule);
        var reader = new Reader(objects, log);
        var c = reader.Build(outDir);
        foreach (var w in objects.Warnings.Take(20)) log?.Invoke("  " + w);
        return c;
    }

    sealed class Reader
    {
        readonly ScriptObjects objects;
        readonly Action<string> log;
        readonly AssetFile resources;
        readonly Catalogue c = new Catalogue();
        // Prefab name -> its SectorBody (file, entry, type), in the order met: the Trench's first, then Resources/Sector.
        readonly Dictionary<string, (AssetFile file, AssetFile.Entry entry, TypeDefinition type)> bodies = new();
        readonly List<string> order = new();
        readonly Dictionary<string, List<string>> nodeTags = new();
        readonly Dictionary<string, List<string>> prefabTags = new();
        // Sprite name -> where it is, for its picture.
        readonly Dictionary<string, (AssetFile file, UnityAssets.Sprite sprite)> sprites = new();
        readonly HashSet<string> seenTags = new();
        string[] unitNames = GalaxyFile.Units;
        int problems;

        public Reader(ScriptObjects objects, Action<string> log)
        {
            this.objects = objects;
            this.log = log;
            resources = objects.Open("resources.assets");
        }

        readonly HashSet<string> warned = new();
        readonly HashSet<string> partial = new();

        void Warn(string message)
        {
            if (!warned.Add(message)) return;
            if (++problems <= 30) log?.Invoke("  " + message);
        }

        public Catalogue Build(string outDir)
        {
            c.SpawnTags.AddRange(SpawnTags);
            UnitNames();
            Systems();
            ResourcePrefabs();
            foreach (var name in order)
            {
                try { c.Prefabs.Add(Prefab(name)); }
                catch (Exception e) { Warn($"prefab {name}: {e.Message}"); }
            }
            TagNames();
            foreach (var s in c.TrenchSystems)
                if (!string.IsNullOrEmpty(s.Skybox) && !c.Skyboxes.Contains(s.Skybox)) c.Skyboxes.Add(s.Skybox);
            ExportSprites(Path.Combine(outDir, "sprites"));
            WarpMap(outDir);
            if (problems > 30) log?.Invoke($"  ... {problems - 30} more problems");
            if (partial.Count > 0) log?.Invoke($"  ({partial.Count} objects read in part: their fields after a Unity type this reader doesn't know aren't needed)");
            return c;
        }

        // ---- Data helpers (the decoded objects: dictionaries, lists, numbers, strings, "file:path" links) ------

        static string Str(D d, string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;
        static PPtr Link(D d, string k) => ScriptObjects.ParseLink(Str(d, k));
        static D Obj(D d, string k) => d != null && d.TryGetValue(k, out var v) ? v as D : null;
        static List<object> List(D d, string k) => d != null && d.TryGetValue(k, out var v) && v is List<object> l ? l : new List<object>();
        static float Num(D d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToSingle(v) : 0f;
        static double Dbl(D d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v) : 0;

        List<string> Tags(D tagList)
        {
            var result = new List<string>();
            foreach (var o in List(tagList, "_tagNames"))
                if (o is string n && n.Length > 0 && !result.Contains(n))
                {
                    result.Add(n);
                    seenTags.Add(n);
                }
            return result;
        }

        /// <summary>A script object by link: its file, entry and decoded data (null when it can't be read).</summary>
        (AssetFile file, AssetFile.Entry entry, D data, TypeDefinition type)? Object(AssetFile from, PPtr link)
        {
            var at = objects.Resolve(from, link);
            if (at == null || at.Value.entry.ClassId != 114) return null;
            var (f, e) = at.Value;
            var script = objects.ScriptOf(f, e);
            var t = script == null ? null : objects.TypeOf(script);
            if (t == null) return null;
            var d = objects.Read(f, e, t, out var problem);
            // Read only in part (a field of a Unity type not written out, such as an exploding body's
            // AnimationCurve): the fields before it are there, and they're the ones used. Counted, not listed.
            if (problem != null) partial.Add(KeyOf(f, e.PathId));
            return (f, e, d, t);
        }

        string GameObjectName(AssetFile from, PPtr link)
        {
            var at = objects.Resolve(from, link);
            if (at == null || at.Value.entry.ClassId != 1) return null;
            return GameObjectAsset.Read(at.Value.file.At(at.Value.entry)).Name;
        }

        /// <summary>A script object's name as Unity gives it: its GameObject's, else its own m_Name.</summary>
        string NameOf(AssetFile f, D d) => (Link(d, "m_GameObject") is { IsNull: false } go ? GameObjectName(f, go) : null) ?? Str(d, "m_Name") ?? "";

        void UnitNames()
        {
            // Map.SystemNode's Scale enum: its names by value (Kilometers, Skybox, Lightseconds, AstronomicalUnits).
            var t = objects.TypeNamed("Map.SystemNode");
            var field = t?.Fields.FirstOrDefault(f => f.Name == "Scale");
            var e = field?.FieldType.Resolve();
            if (e == null || !e.IsEnum) return;
            var names = new string[4];
            foreach (var f in e.Fields)
                if (f.IsStatic && f.Constant is int v && v >= 0 && v < names.Length) names[v] = f.Name;
            if (names.All(n => n != null)) unitNames = names;
        }

        void TagNames()
        {
            // The game's Tag enum (NovaTagList._tags' element type), then any name seen that it lacks.
            var t = objects.TypeNamed("NovaTagList");
            var field = t?.Fields.FirstOrDefault(f => f.Name == "_tags");
            var e = (field?.FieldType as ArrayType)?.ElementType.Resolve();
            if (e != null && e.IsEnum)
                foreach (var f in e.Fields)
                    if (f.IsStatic && f.IsLiteral) c.Tags.Add(f.Name);
            if (c.Tags.Count == 0) Warn("the game's tag names weren't found; only the names the map uses are listed");
            foreach (var n in seenTags.OrderBy(n => n, StringComparer.Ordinal))
                if (!c.Tags.Contains(n)) c.Tags.Add(n);
        }

        // ---- The Trench ----------------------------------------------------------------------------------------

        void Systems()
        {
            (AssetFile.Entry entry, D data)? trench = null;
            var roots = new List<(AssetFile.Entry, D, string)>();
            foreach (var e in resources.Objects)
            {
                if (e.ClassId != 114) continue;
                var script = objects.ScriptOf(resources, e);
                if (script?.FullName != "Map.SectorRoot") continue;
                var t = objects.TypeOf(script);
                var d = t == null ? null : objects.Read(resources, e, t, out var problem);
                if (d == null) continue;
                roots.Add((e, d, NameOf(resources, d)));
            }
            foreach (var (e, d, name) in roots) if (name == TrenchName) { trench = (e, d); break; }
            if (trench == null)
                foreach (var (e, d, name) in roots)
                    if (name.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) >= 0) { trench = (e, d); break; }
            if (trench == null) throw new InvalidDataException($"the Trench's map ('{TrenchName}') isn't in resources.assets");

            foreach (var lo in List(trench.Value.data, "Systems"))
            {
                var link = lo as D;
                string name = "?";
                try
                {
                    var root = Object(resources, Link(link, "System"));
                    if (root == null) continue;
                    var (rf, _, rd, _) = root.Value;
                    var pos = Obj(link, "Position");
                    name = Str(rd, "SystemName");
                    if (string.IsNullOrEmpty(name)) name = NameOf(rf, rd);
                    var system = new CatalogueSystem
                    {
                        Name = name,
                        DisplayName = CatalogueNames.Display(name),
                        X = Num(pos, "x"),
                        Z = Num(pos, "z"),
                        Skybox = Str(rd, "_skyboxPath"),
                        SystemTags = Tags(Obj(rd, "SystemTags")),
                    };
                    system.Nodes = Nodes(rf, rd, name);
                    system.Families = CatalogueFamilies.From(system.Nodes, PrefabTags, c.SpawnTags);
                    c.TrenchSystems.Add(system);
                }
                catch (Exception e) { Warn($"system {name}: {e.Message}"); }
            }
        }

        sealed class Node
        {
            public string Key;      // file:pathId
            public AssetFile File;
            public D Data;
            public string ParentKey;
            public List<string> ChildKeys = new();
        }

        static string KeyOf(AssetFile f, long pathId) => Path.GetFileName(f.Path) + ":" + pathId;

        Node ReadNode(AssetFile from, PPtr link, Dictionary<string, Node> cache)
        {
            var at = objects.Resolve(from, link);
            if (at == null) return null;
            string key = KeyOf(at.Value.file, at.Value.entry.PathId);
            if (cache.TryGetValue(key, out var known)) return known;
            var o = Object(from, link);
            Node n = null;
            if (o != null && o.Value.type.FullName == "Map.SystemNode")
            {
                var (f, _, d, _) = o.Value;
                n = new Node { Key = key, File = f, Data = d };
                var parent = objects.Resolve(f, Link(d, "_parent"));
                if (parent != null) n.ParentKey = KeyOf(parent.Value.file, parent.Value.entry.PathId);
                foreach (var ch in List(d, "_children"))
                {
                    var cl = objects.Resolve(f, ScriptObjects.ParseLink(ch as string));
                    if (cl != null) n.ChildKeys.Add(KeyOf(cl.Value.file, cl.Value.entry.PathId));
                }
            }
            cache[key] = n;
            return n;
        }

        // The system's node tree, parents before children: the nodes of its SystemTransforms, and their children.
        List<GalaxyNode> Nodes(AssetFile rf, D root, string systemName)
        {
            var cache = new Dictionary<string, Node>();
            var all = new List<Node>();
            var listed = new HashSet<string>();
            foreach (var so in List(root, "SystemTransforms"))
            {
                var st = Object(rf, ScriptObjects.ParseLink(so as string));
                if (st == null) continue;
                var n = ReadNode(st.Value.file, Link(st.Value.data, "_systemNode"), cache);
                if (n != null && listed.Add(n.Key)) all.Add(n);
            }
            // Children not listed as transforms of their own.
            for (int i = 0; i < all.Count; i++)
                foreach (var ch in List(all[i].Data, "_children"))
                {
                    var n = ReadNode(all[i].File, ScriptObjects.ParseLink(ch as string), cache);
                    if (n != null && listed.Add(n.Key)) all.Add(n);
                }

            var ids = new Dictionary<string, string>();
            var usedIds = new HashSet<string>();
            var names = new Dictionary<string, string>();
            for (int i = 0; i < all.Count; i++)
            {
                names[all[i].Key] = NameOf(all[i].File, all[i].Data);
                string id = Slug(names[all[i].Key]);
                if (id == "" || usedIds.Contains(id))
                {
                    string stem = id == "" ? "node" : id;
                    int k = i;
                    do id = stem + "-" + k++; while (usedIds.Contains(id));
                }
                usedIds.Add(id);
                ids[all[i].Key] = id;
            }

            var byKey = all.ToDictionary(n => n.Key);
            var result = new List<GalaxyNode>();
            var done = new HashSet<string>();
            foreach (var n in all)
                if (n.ParentKey == null || !ids.ContainsKey(n.ParentKey)) Add(n, null);
            foreach (var n in all) if (!done.Contains(n.Key)) Add(n, null); // a parent loop: shouldn't happen
            return result;

            void Add(Node n, string parent)
            {
                if (!done.Add(n.Key)) return;
                var d = n.Data;
                try
                {
                    string prefabName = "";
                    var body = Object(n.File, Link(d, "Prefab"));
                    if (body != null)
                    {
                        prefabName = NameOf(body.Value.file, body.Value.data);
                        if (!bodies.ContainsKey(prefabName))
                        {
                            bodies[prefabName] = (body.Value.file, body.Value.entry, body.Value.type);
                            order.Add(prefabName);
                        }
                    }
                    var pos = Obj(d, "Position");
                    var q = Obj(d, "Rotation");
                    var rot = Euler(Num(q, "x"), Num(q, "y"), Num(q, "z"), Num(q, "w"));
                    int scale = d.TryGetValue("Scale", out var sv) && sv is int iv ? iv : 0;
                    string unit = scale >= 0 && scale < unitNames.Length ? unitNames[scale] : null;
                    if (Array.IndexOf(GalaxyFile.Units, unit) < 0)
                    {
                        Warn($"{systemName}/{names[n.Key]}: unknown unit '{unit ?? scale.ToString()}', written as Kilometers");
                        unit = "Kilometers";
                    }
                    var tags = Tags(Obj(d, "Tags"));
                    if (prefabName != "")
                    {
                        if (!nodeTags.TryGetValue(prefabName, out var list)) nodeTags[prefabName] = list = new List<string>();
                        foreach (var tag in tags) if (!list.Contains(tag)) list.Add(tag);
                    }
                    string mapName = Str(d, "SystemMapName");
                    string name = names[n.Key];
                    if (string.IsNullOrEmpty(name)) name = mapName;
                    if (string.IsNullOrEmpty(name)) name = prefabName;
                    float landing = Num(d, "ImpulseLandingRadius");
                    result.Add(new GalaxyNode
                    {
                        Id = ids[n.Key],
                        Parent = parent,
                        Name = string.IsNullOrEmpty(name) ? ids[n.Key] : name,
                        Prefab = prefabName,
                        X = Num(pos, "x"), Y = Num(pos, "y"), Z = Num(pos, "z"),
                        Unit = unit,
                        RotX = rot[0], RotY = rot[1], RotZ = rot[2],
                        Tags = tags,
                        LandingRadius = float.IsNaN(landing) || float.IsInfinity(landing) ? 0 : landing,
                        MapName = string.IsNullOrEmpty(mapName) ? null : mapName,
                        FamilyBackground = SpriteName(n.File, Link(d, "FamilyMapBackground")),
                        MapIcon = SpriteName(n.File, Link(d, "SystemMapIconSprite")),
                    });
                }
                catch (Exception e)
                {
                    Warn($"{systemName}/{names[n.Key]}: {e.Message}");
                    return; // its children are written as top-level nodes
                }
                foreach (var ck in n.ChildKeys)
                    if (ids.ContainsKey(ck) && byKey.TryGetValue(ck, out var child)) Add(child, ids[n.Key]);
            }
        }

        // The sprite's name, added to the catalogue's sprite list (and kept for its picture); null for none.
        string SpriteName(AssetFile from, PPtr link)
        {
            var at = objects.Resolve(from, link);
            if (at == null || at.Value.entry.ClassId != UnityAssets.SpriteClass) return null;
            var s = UnityAssets.ReadSprite(at.Value.file.At(at.Value.entry), at.Value.entry);
            if (string.IsNullOrEmpty(s.Name)) return null;
            if (!c.Sprites.Contains(s.Name)) c.Sprites.Add(s.Name);
            if (!sprites.ContainsKey(s.Name)) sprites[s.Name] = (at.Value.file, s);
            return s.Name;
        }

        // ---- Prefabs -------------------------------------------------------------------------------------------

        // Every SectorBody prefab under the game's Resources/Sector (the Resources list in globalgamemanagers), after
        // the Trench's own.
        void ResourcePrefabs()
        {
            var ggm = objects.TryOpen("globalgamemanagers");
            var rm = ggm?.Objects.FirstOrDefault(o => o.ClassId == 147);
            if (rm == null)
            {
                Warn("the game's Resources list (globalgamemanagers) isn't readable; only the Trench's prefabs are listed");
                return;
            }
            int before = order.Count;
            foreach (var kv in UnityAssets.ReadResourceContainer(ggm.At(rm), rm))
            {
                if (!kv.Key.StartsWith("sector/", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var at = objects.Resolve(ggm, kv.Value);
                    if (at == null || at.Value.entry.ClassId != 1) continue;
                    var (f, e) = at.Value;
                    var go = GameObjectAsset.Read(f.At(e));
                    if (string.IsNullOrEmpty(go.Name) || bodies.ContainsKey(go.Name)) continue;
                    foreach (var comp in go.Components)
                    {
                        if (comp.Key != 114) continue;
                        var ce = objects.Resolve(f, comp.Value);
                        if (ce == null) continue;
                        var script = objects.ScriptOf(ce.Value.file, ce.Value.entry);
                        var t = script == null ? null : objects.TypeOf(script);
                        if (!ScriptObjects.Is(t, "Sector.SectorBody")) continue;
                        bodies[go.Name] = (ce.Value.file, ce.Value.entry, t);
                        order.Add(go.Name);
                        break;
                    }
                }
                catch (Exception ex) { Warn($"Resources/{kv.Key}: {ex.Message}"); }
            }
            log?.Invoke($"  {order.Count - before} more SectorBody prefabs under Resources/Sector");
        }

        IList<string> PrefabTags(string prefab)
        {
            if (prefabTags.TryGetValue(prefab, out var tags)) return tags;
            tags = new List<string>();
            if (bodies.TryGetValue(prefab, out var b))
            {
                var d = objects.Read(b.file, b.entry, b.type, out _);
                tags = Tags(Obj(d, "PrefabTags"));
            }
            return prefabTags[prefab] = tags;
        }

        CataloguePrefab Prefab(string name)
        {
            var b = bodies[name];
            var d = objects.Read(b.file, b.entry, b.type, out _); // in part for some (see Object)
            var tags = PrefabTags(name);
            double radius = Radius(b.type, d);
            if (double.IsNaN(radius) || double.IsInfinity(radius)) radius = 0;
            string kind = Kind(tags.ToList());
            if (kind == "other" && nodeTags.TryGetValue(name, out var used)) kind = Kind(used);
            string ui = null;
            try { ui = UiName(b.file, Link(d, "m_GameObject"), 0); }
            catch (Exception e) { Warn($"name of prefab {name}: {e.Message}"); }
            return new CataloguePrefab { Name = name, Kind = kind, Radius = radius, Label = Label(ui, name) };
        }

        // The body's Radius as the game works it out for a prefab: a mesh body's MeshRadius, an asteroid field's
        // _fieldRadius in metres; 0 for the rest (ships work theirs out from their colliders in the scene).
        static double Radius(TypeDefinition t, D d)
        {
            if (ScriptObjects.Is(t, "Sector.MeshSimBody") || ScriptObjects.Is(t, "Sector.MeshSkyboxBody")) return Dbl(d, "MeshRadius");
            if (ScriptObjects.Is(t, "AsteroidFieldGeneratorBody")) return Num(d, "_fieldRadius") * 10.0; // SimulationSpace.UnitToMeterScale
            return 0;
        }

        // The _uiName of the first TargetableComponent on the GameObject or under it (GetComponentInChildren's order).
        string UiName(AssetFile f, PPtr goLink, int depth)
        {
            if (depth > 12) return null;
            var at = objects.Resolve(f, goLink);
            if (at == null || at.Value.entry.ClassId != 1) return null;
            var (gf, ge) = at.Value;
            var go = GameObjectAsset.Read(gf.At(ge));
            PPtr transform = default;
            foreach (var comp in go.Components)
            {
                if (comp.Key is 4 or 224) transform = comp.Value;
                if (comp.Key != 114) continue;
                var ce = objects.Resolve(gf, comp.Value);
                if (ce == null) continue;
                var script = objects.ScriptOf(ce.Value.file, ce.Value.entry);
                var t = script == null ? null : objects.TypeOf(script);
                if (!ScriptObjects.Is(t, "Sector.TargetableComponent")) continue;
                var d = objects.Read(ce.Value.file, ce.Value.entry, t, out _);
                return Str(d, "_uiName"); // null when not read
            }
            var te = objects.Resolve(gf, transform);
            if (te == null) return null;
            var tr = TransformAsset.Read(te.Value.file.At(te.Value.entry));
            foreach (var child in tr.Children)
            {
                var ct = objects.Resolve(te.Value.file, child);
                if (ct == null) continue;
                var cgo = TransformAsset.Read(ct.Value.file.At(ct.Value.entry)).GameObject;
                var found = UiName(ct.Value.file, cgo, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        // ---- Pictures ------------------------------------------------------------------------------------------

        void ExportSprites(string folder)
        {
            Directory.CreateDirectory(folder);
            var pictures = new UnityAssets.SpritePictures(objects.DataDir, objects.TryOpen);
            var used = new HashSet<string>();
            foreach (var name in c.Sprites)
            {
                try
                {
                    if (!sprites.TryGetValue(name, out var s)) continue;
                    var png = pictures.Png(s.file, s.sprite);
                    if (png == null) continue;
                    string file = CatalogueNames.UniquePngFile(name, used);
                    File.WriteAllBytes(Path.Combine(folder, file), png);
                    c.SpriteFiles[name] = file;
                }
                catch (Exception e) { Warn($"sprite {name}: {e.Message}"); }
            }
            foreach (var w in pictures.Warnings) Warn(w);
        }

        // The warp map's art (the sprite "WarpMap", on the Aegis bridge's warp panel) as trench-map.png, and the
        // universe bounds from that bridge's WarpPanel (_universeWidth/_universeHeight, centred on 0 as its Awake has it).
        void WarpMap(string outDir)
        {
            c.UniverseWidth = DefaultUniverseWidth;
            c.UniverseHeight = DefaultUniverseHeight;
            c.UniverseMinX = -c.UniverseWidth / 2;
            c.UniverseMinZ = -c.UniverseHeight / 2;
            var numbers = new List<int> { 7 };
            for (int i = 0; i < 40; i++) if (i != 7) numbers.Add(i);
            foreach (int i in numbers)
            {
                var f = objects.TryOpen($"sharedassets{i}.assets");
                if (f == null) continue;
                foreach (var e in f.Objects)
                {
                    if (e.ClassId != UnityAssets.SpriteClass || f.NameOf(e) != "WarpMap") continue;
                    try
                    {
                        var s = UnityAssets.ReadSprite(f.At(e), e);
                        var pictures = new UnityAssets.SpritePictures(objects.DataDir, objects.TryOpen);
                        var png = pictures.Png(f, s);
                        foreach (var w in pictures.Warnings) Warn(w);
                        if (png == null) continue;
                        File.WriteAllBytes(Path.Combine(outDir, "trench-map.png"), png);
                        c.Background = "trench-map.png";
                        Universe($"level{i}");
                        return;
                    }
                    catch (Exception ex) { Warn($"warp map art: {ex.Message}"); }
                }
            }
            Warn("the warp map art (sprite WarpMap) wasn't found; no background");
        }

        void Universe(string level)
        {
            var f = objects.TryOpen(level);
            if (f == null) return;
            foreach (var e in f.Objects)
            {
                if (e.ClassId != 114) continue;
                var script = objects.ScriptOf(f, e);
                if (script?.FullName != "WarpPanel") continue;
                var t = objects.TypeOf(script);
                var d = t == null ? null : objects.Read(f, e, t, out var problem);
                float w = Num(d, "_universeWidth"), h = Num(d, "_universeHeight");
                if (w <= 0 || h <= 0) continue;
                c.UniverseWidth = w;
                c.UniverseHeight = h;
                break;
            }
            c.UniverseMinX = -c.UniverseWidth / 2;
            c.UniverseMinZ = -c.UniverseHeight / 2;
        }
    }
}
