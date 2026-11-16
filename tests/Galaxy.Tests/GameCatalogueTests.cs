namespace Galaxy.Tests;

public class GameCatalogueTests
{
    [Theory]
    [InlineData("2824|Venov", "Venov")]
    [InlineData("5796|Space Station", "Space Station")]
    [InlineData("Ryburn", "Ryburn")]
    public void TextKeys_GiveTheEnglish(string key, string english) => Assert.Equal(english, CatalogueNames.Display(key));

    [Theory]
    [InlineData("5796|Space Station", "Ship - SpaceStationProcedural 22", "Space Station")]
    [InlineData("1234|Target", "Ship - Freighter A", "Freighter A")]       // the component's default isn't a name
    [InlineData(null, "AsteroidClusterNew", "Asteroid Cluster New")]
    [InlineData("", "Prop - Buoy", "Buoy")]
    public void Label_FromTheTargetingName(string ui, string prefab, string label) => Assert.Equal(label, GameCatalogue.Label(ui, prefab));

    [Theory]
    [InlineData("star", "Star", "Planet")]
    [InlineData("station", "SpaceStation", "Ship")]
    [InlineData("moon", "Moon", "Planet")]
    [InlineData("planet", "Planet")]
    [InlineData("asteroids", "Asteroid")]
    [InlineData("nebula", "Nebula")]
    [InlineData("other", "Ship", "Mission")]
    [InlineData("other")]
    public void Kind_FromTags(string kind, params string[] tags) => Assert.Equal(kind, GameCatalogue.Kind(tags));

    [Fact]
    public void Euler_AsUnityGivesIt()
    {
        Assert.Equal(new float[] { 0, 0, 0 }, GameCatalogue.Euler(0, 0, 0, 1));
        // Unnormalised, as some of the map's are: normalised first (the game shows 134.93 for this one).
        Assert.Equal(134.929184f, GameCatalogue.Euler(0, 0, 2.41f, 1)[2], 0.001f);
        var e = GameCatalogue.Euler(0, (float)Math.Sin(Math.PI / 4), 0, (float)Math.Cos(Math.PI / 4)); // 90° about Y
        Assert.Equal(90f, e[1], 0.001f);
        // Negative angles come out positive.
        Assert.Equal(270f, GameCatalogue.Euler(0, (float)Math.Sin(-Math.PI / 4), 0, (float)Math.Cos(-Math.PI / 4))[1], 0.001f);
        // Looking straight up: all the turn in Y, none in Z.
        var up = GameCatalogue.Euler((float)Math.Sin(Math.PI / 4), 0, 0, (float)Math.Cos(Math.PI / 4));
        Assert.Equal(90f, up[0], 0.001f);
        Assert.Equal(0f, up[2]);
    }

    [Theory]
    [InlineData("FAMILY Venov III", "family-venov-iii")]
    [InlineData("POI Start Planet", "poi-start-planet")]
    [InlineData("--", "")]
    public void Slug_ForNodeIds(string name, string id) => Assert.Equal(id, GameCatalogue.Slug(name));

    [Theory]
    [InlineData("Star_Yellow.png", true)]
    [InlineData("Ship_-_Aegis-2.png", true)]
    [InlineData("../x.png", false)]
    [InlineData("a/b.png", false)]
    [InlineData("a\\b.png", false)]
    [InlineData(".png", false)]
    [InlineData("x..png", false)]
    [InlineData("x.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PictureFile_OnlyPlainPngNames(string name, bool ok) => Assert.Equal(ok, GameCatalogue.IsPictureFile(name));

    static Catalogue Small() => new Catalogue
    {
        Prefabs = new List<CataloguePrefab>
        {
            new CataloguePrefab { Name = "Star_Yellow", Kind = "star" },
            new CataloguePrefab { Name = "Planet_Rock", Kind = "planet", Thumb = "old.png" },
            new CataloguePrefab { Name = "Odd" },
        },
    };

    [Fact]
    public void MergePictures_NoIndex_NoThumbsAndPending()
    {
        var c = Small();
        Assert.True(GameCatalogue.MergePictures(c, null, _ => true));
        Assert.All(c.Prefabs, p => Assert.Null(p.Thumb));
    }

    [Fact]
    public void MergePictures_OnlyFilesThatAreThere()
    {
        var c = Small();
        var index = new ThumbIndex();
        index.Pictures["Star_Yellow"] = new ThumbIndex.Picture { File = "Star_Yellow.png" };
        index.Pictures["Planet_Rock"] = new ThumbIndex.Picture { File = "Planet_Rock.png" };
        index.Pictures["Odd"] = new ThumbIndex.Picture { File = "../../evil.png" };
        Assert.False(GameCatalogue.MergePictures(c, index, f => f != "Planet_Rock.png"));
        Assert.Equal("Star_Yellow.png", c.Prefabs[0].Thumb);
        Assert.Null(c.Prefabs[1].Thumb);
        Assert.Null(c.Prefabs[2].Thumb);
    }

    [Fact]
    public void MergePictures_RunCutShort_IsPending()
    {
        var c = Small();
        var index = new ThumbIndex { Format = 0 };
        index.Pictures["Star_Yellow"] = new ThumbIndex.Picture { File = "Star_Yellow.png" };
        Assert.True(GameCatalogue.MergePictures(c, index, _ => true));
        Assert.Equal("Star_Yellow.png", c.Prefabs[0].Thumb);
    }

    [Fact]
    public void Load_NoGameFolder_SaysSoPlainly()
    {
        var g = GameCatalogue.Load(Path.Combine(Path.GetTempPath(), "no-game-" + Guid.NewGuid().ToString("N")), Path.GetTempPath(), false, null);
        Assert.Null(g.Catalogue);
        Assert.Contains("The game wasn't found", g.Error);
        Assert.Contains("--game", g.Error);
    }

    [Fact]
    public void Load_UnreadableFiles_SaysSoPlainly()
    {
        string game = Path.Combine(Path.GetTempPath(), "fake-game-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(game, "cache");
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "stbc_Data", "Managed"));
            File.WriteAllText(Path.Combine(game, "stbc_Data", "resources.assets"), "not an asset file");
            var g = GameCatalogue.Load(game, cache, false, null);
            Assert.Null(g.Catalogue);
            Assert.Contains("can't be read", g.Error);
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    [Fact]
    public void CacheKey_ChangesWithTheGameFiles()
    {
        string game = Path.Combine(Path.GetTempPath(), "fake-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(game, "stbc_Data");
            Directory.CreateDirectory(Path.Combine(data, "Managed"));
            File.WriteAllText(Path.Combine(data, "resources.assets"), "a");
            string k1 = GameCatalogue.CacheKey(game);
            Assert.Equal(k1, GameCatalogue.CacheKey(game));
            File.WriteAllText(Path.Combine(data, "resources.assets"), "ab");
            Assert.NotEqual(k1, GameCatalogue.CacheKey(game));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    // ---- The cache, with a small catalogue built in place of reading a game ---------------------------------

    sealed class FakeGame : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "fake-game-" + Guid.NewGuid().ToString("N"));
        public readonly string Cache;
        public int Builds;

        public FakeGame()
        {
            Directory.CreateDirectory(Path.Combine(Dir, "stbc_Data", "Managed"));
            File.WriteAllText(Path.Combine(Dir, "stbc_Data", "resources.assets"), "x");
            Cache = Path.Combine(Path.GetTempPath(), "fake-cache-" + Guid.NewGuid().ToString("N"));
        }

        // What GameCatalogue.Build makes: the catalogue, its sprite pictures and the background in the folder.
        public Catalogue Build(string outDir)
        {
            Interlocked.Increment(ref Builds);
            Directory.CreateDirectory(Path.Combine(outDir, "sprites"));
            File.WriteAllBytes(Path.Combine(outDir, "sprites", "Icon.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(outDir, "trench-map.png"), new byte[] { 2 });
            var c = new Catalogue { Background = "trench-map.png" };
            c.Sprites.Add("Icon");
            c.SpriteFiles["Icon"] = "Icon.png";
            c.TrenchSystems.Add(new CatalogueSystem { Name = "Venov" });
            return c;
        }

        public GameData Load(bool rebuild = false) => GameCatalogue.Load(Dir, Cache, rebuild, null, Build);

        public void Dispose()
        {
            foreach (var d in new[] { Dir, Cache })
                try { MakeWritable(d); Directory.Delete(d, true); } catch { }
        }
    }

    static void MakeWritable(string dir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(dir)) return;
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    static void AssertServable(GameData g)
    {
        Assert.NotNull(g.Catalogue);
        Assert.True(File.Exists(Path.Combine(g.Folder, "trench-map.png")));
        Assert.True(File.Exists(Path.Combine(g.Folder, "sprites", "Icon.png")));
    }

    [Fact]
    public void Cache_SecondLoadFromIt()
    {
        using var game = new FakeGame();
        var first = game.Load();
        AssertServable(first);
        Assert.False(first.FromCache);
        var second = game.Load();
        AssertServable(second);
        Assert.True(second.FromCache);
        Assert.Equal(first.Folder, second.Folder);
        Assert.Equal(1, game.Builds);
    }

    [Fact]
    public void Cache_Unwritable_StillGivesTheCatalogue()
    {
        if (OperatingSystem.IsWindows()) return; // read-only folders work differently there
        using var game = new FakeGame();
        Directory.CreateDirectory(game.Cache);
        File.SetUnixFileMode(game.Cache, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (Environment.UserName == "root") return; // root writes anyway
        var g = game.Load();
        Assert.Null(g.Error);
        AssertServable(g);
        Assert.False(GameCatalogue.IsInside(g.Folder, game.Cache));
        try { Directory.Delete(g.Folder, true); } catch { }
    }

    [Fact]
    public void Cache_MissingPicture_BuildsAgain()
    {
        using var game = new FakeGame();
        var first = game.Load();
        File.Delete(Path.Combine(first.Folder, "sprites", "Icon.png"));
        var second = game.Load();
        Assert.False(second.FromCache);
        Assert.Equal(2, game.Builds);
        AssertServable(second);
        File.Delete(Path.Combine(second.Folder, "trench-map.png"));
        Assert.False(game.Load().FromCache);
        Assert.Equal(3, game.Builds);
    }

    [Fact]
    public void Cache_LoadsAtOnce_DontSpoilEachOther()
    {
        using var game = new FakeGame();
        var results = new GameData[8];
        Parallel.For(0, results.Length, i => results[i] = game.Load(rebuild: i % 2 == 0));
        foreach (var g in results) AssertServable(g);
        // Only the key's folder is left (the builds were moved into it or removed).
        var root = Directory.GetDirectories(game.Cache).Single();
        Assert.Single(Directory.GetDirectories(root));
        Assert.True(game.Load().FromCache);
    }

    [Fact]
    public void Cache_GamePathWithOrWithoutSlash_SameCache()
    {
        using var game = new FakeGame();
        game.Load();
        var g = GameCatalogue.Load(game.Dir + Path.DirectorySeparatorChar, game.Cache, false, null, game.Build);
        Assert.True(g.FromCache);
    }

    [Theory]
    [InlineData("/games/stbc/cache", "/games/stbc", true)]
    [InlineData("/games/stbc", "/games/stbc/", true)]
    [InlineData("/games/stbc-cache", "/games/stbc", false)]
    [InlineData("/home/me/.cache/galaxy-designer", "/games/stbc", false)]
    public void IsInside_ForTheCacheFolder(string dir, string parent, bool inside)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(inside, GameCatalogue.IsInside(dir, parent));
    }

    /// <summary>The real game's files (only with GAME_DIR set to the game's folder): what the in-game catalogue had.</summary>
    [Fact]
    public void Smoke_RealGameFiles()
    {
        string game = Environment.GetEnvironmentVariable("GAME_DIR");
        if (string.IsNullOrEmpty(game)) return; // no game here: nothing to read
        string cache = Path.Combine(Path.GetTempPath(), "galaxy-designer-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = GameCatalogue.Load(game, cache, false, null);
            Assert.True(g.Catalogue != null, g.Error);
            Assert.False(g.FromCache);
            var c = g.Catalogue;
            Assert.Equal(15, c.TrenchSystems.Count);
            Assert.Equal(55, c.TrenchSystems.Sum(s => s.Families.Count));
            Assert.Contains(c.TrenchSystems, s => s.Name == "2824|Venov" && s.DisplayName == "Venov");
            var venovIII = c.TrenchSystems.Single(s => s.Name == "2824|Venov").Families.Single(f => f.Name == "FAMILY Venov III");
            Assert.True(venovIII.HasSpawn);
            Assert.Equal(new[] { "POI Start Planet", "POI Moon A", "POI Moon B" }, venovIII.Pois);
            Assert.True(c.Prefabs.Count >= 400);
            Assert.Equal(24, c.Sprites.Count);
            Assert.Equal(24, c.SpriteFiles.Count);
            Assert.Equal(new[] { "Player", "AltPlayerSpawn" }, c.SpawnTags);
            Assert.Equal(530, c.UniverseWidth);
            Assert.Equal(-115, c.UniverseMinZ);
            Assert.Contains("SpaceStation", c.Tags);
            byte[] png = File.ReadAllBytes(Path.Combine(g.Folder, c.Background));
            int w = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19], h = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            Assert.Equal((2048, 1024), (w, h));
            // Again: from the cache, the same.
            var again = GameCatalogue.Load(game, cache, false, null);
            Assert.True(again.FromCache);
            Assert.Equal(c.ToJson(), again.Catalogue.ToJson());
        }
        finally { try { Directory.Delete(cache, true); } catch { } }
    }
}
