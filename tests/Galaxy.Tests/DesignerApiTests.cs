using System.Text;

namespace Galaxy.Tests;

public class DesignerApiTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "galaxy-designer-" + Guid.NewGuid().ToString("N"));
    // The game data's folder (sprites/, trench-map.png), as GameCatalogue's cache has it.
    readonly string gameFolder;
    readonly DesignerApi api;   // with no game data
    readonly DesignerApi withGame;

    public DesignerApiTests()
    {
        Directory.CreateDirectory(dir);
        gameFolder = Path.Combine(dir, "cache");
        Directory.CreateDirectory(Path.Combine(gameFolder, "sprites"));
        api = new DesignerApi(dir, GameData.Failed("The game's files in /x/stbc_Data can't be read: broken"));
        withGame = new DesignerApi(dir, new GameData { Catalogue = Catalogue.Parse(CatalogueJson()), Folder = gameFolder });
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Guide_IsServedAsMarkdown()
    {
        var g = new DesignerApi(dir, GameData.Failed("x"), null, () => "# Guide\n\nText");
        var r = g.Handle("GET", "/api/guide", null);
        Assert.Equal(200, r.status);
        Assert.StartsWith("text/markdown", r.contentType);
        Assert.Equal("# Guide\n\nText", S(r.body));
    }

    [Fact]
    public void Guide_Absent_Is404() => Assert.Equal(404, api.Handle("GET", "/api/guide", null).status);

    static Dictionary<string, object> Obj(byte[] body) => (Dictionary<string, object>)MiniJson.Parse(S(body));

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    static string S(byte[] b) => Encoding.UTF8.GetString(b);

    static string GalaxyJson(string prefab = "Star_Yellow")
    {
        var f = new GalaxyFile
        {
            Name = "R",
            Systems = new List<GalaxySystem>
            {
                new GalaxySystem
                {
                    Id = "kessar", Name = "Kessar", X = 100, Z = 100, Template = "Venov",
                    Nodes = new List<GalaxyNode>
                    {
                        new GalaxyNode { Id = "star", Parent = "poi", Name = "Star", Prefab = prefab, Tags = new List<string> { "Player" } },
                        new GalaxyNode { Id = "stn", Parent = "star", Name = "Stn", Prefab = "Planet_Rock", Tags = new List<string> { "SpaceStation" } },
                        new GalaxyNode { Id = "fam", Name = "Fam", Prefab = "Family Group" },
                        new GalaxyNode { Id = "poi", Parent = "fam", Name = "Poi", Prefab = "Point of Interest" },
                    },
                },
            },
        };
        return f.ToJson();
    }

    static string CatalogueJson() => new Catalogue
    {
        TrenchSystems = new List<CatalogueSystem> { new CatalogueSystem { Name = "Venov" } },
        Prefabs = new List<CataloguePrefab>
        {
            new CataloguePrefab { Name = "Star_Yellow", Kind = "star", Radius = 10 },
            new CataloguePrefab { Name = "Planet_Rock", Kind = "planet", Radius = 2 },
            new CataloguePrefab { Name = "Family Group" },
            new CataloguePrefab { Name = "Point of Interest" },
        },
        Tags = new List<string> { "Player", "SpaceStation" },
        SpawnTags = new List<string> { "Player" },
        UniverseWidth = 1000, UniverseHeight = 1000,
    }.ToJson();

    [Fact]
    public void Catalogue_GameUnreadable_SaysWhy()
    {
        var (status, type, body) = api.Handle("GET", "/api/catalogue", null);
        Assert.Equal(503, status);
        Assert.Contains("json", type);
        Assert.Contains("can't be read: broken", S(body));
        Assert.DoesNotContain("CatalogueKey", S(body));
    }

    [Fact]
    public void Catalogue_NoPicturesYet_WorksAndSaysSo()
    {
        var (status, _, body) = withGame.Handle("GET", "/api/catalogue", null);
        Assert.Equal(200, status);
        var c = Obj(body);
        Assert.Equal(true, c["picturesPending"]);
        var prefabs = (List<object>)c["prefabs"];
        Assert.Equal(4, prefabs.Count);
        Assert.All(prefabs, p => Assert.Null(((Dictionary<string, object>)p)["thumb"]));
        Assert.Equal("Star_Yellow", Catalogue.Parse(S(body)).Prefabs[0].Name);
    }

    [Fact]
    public void Catalogue_PicturesMergedFromThumbs()
    {
        var index = new ThumbIndex();
        index.Pictures["Star_Yellow"] = new ThumbIndex.Picture { File = "Star_Yellow.png", Kind = "star", Label = "Star" };
        index.Pictures["Planet_Rock"] = new ThumbIndex.Picture { File = "Planet_Rock.png", Kind = "planet", Label = "Rock" }; // no file
        File.WriteAllText(Path.Combine(dir, "thumbs.json"), index.ToJson());
        Directory.CreateDirectory(Path.Combine(dir, "thumbs"));
        File.WriteAllBytes(Path.Combine(dir, "thumbs", "Star_Yellow.png"), new byte[] { 9 });
        var (status, _, body) = withGame.Handle("GET", "/api/catalogue", null);
        Assert.Equal(200, status);
        Assert.Equal(false, Obj(body)["picturesPending"]);
        var c = Catalogue.Parse(S(body));
        Assert.Equal("Star_Yellow.png", c.Prefabs.Single(p => p.Name == "Star_Yellow").Thumb);
        Assert.Null(c.Prefabs.Single(p => p.Name == "Planet_Rock").Thumb);
        // The picture itself, through the API.
        var pic = withGame.Handle("GET", "/api/thumb/Star_Yellow.png", null);
        Assert.Equal(200, pic.status);
        Assert.Equal("image/png", pic.contentType);
        Assert.Equal(new byte[] { 9 }, pic.body);
    }

    [Fact]
    public void Catalogue_UnfinishedPictureRun_StillPending()
    {
        File.WriteAllText(Path.Combine(dir, "thumbs.json"), new ThumbIndex { Format = 0 }.ToJson());
        Assert.Equal(true, Obj(withGame.Handle("GET", "/api/catalogue", null).body)["picturesPending"]);
    }

    [Fact]
    public void TrenchMap_FromTheGameDataFolder()
    {
        Assert.Equal(404, api.Handle("GET", "/api/trench-map.png", null).status);
        var (status, _, body) = withGame.Handle("GET", "/api/trench-map.png", null);
        Assert.Equal(404, status);   // the catalogue names no background
        Assert.Contains("error", S(body));
        var c = Catalogue.Parse(CatalogueJson());
        c.Background = "trench-map.png";
        var api2 = new DesignerApi(dir, new GameData { Catalogue = c, Folder = gameFolder });
        Assert.Equal(404, api2.Handle("GET", "/api/trench-map.png", null).status);
        File.WriteAllBytes(Path.Combine(gameFolder, "trench-map.png"), new byte[] { 1, 2, 3 });
        var ok = api2.Handle("GET", "/api/trench-map.png", null);
        Assert.Equal(200, ok.status);
        Assert.Equal("image/png", ok.contentType);
    }

    [Fact]
    public void Sprite_ServedFromTheGameDataFolder()
    {
        File.WriteAllBytes(Path.Combine(gameFolder, "sprites", "FamilyMapBG_3.png"), new byte[] { 7 });
        var ok = withGame.Handle("GET", "/api/sprite/FamilyMapBG_3.png", null);
        Assert.Equal(200, ok.status);
        Assert.Equal(new byte[] { 7 }, ok.body);
        Assert.Equal(404, withGame.Handle("GET", "/api/sprite/Other.png", null).status);
        Assert.Equal(404, api.Handle("GET", "/api/sprite/FamilyMapBG_3.png", null).status);
    }

    [Theory]
    [InlineData("/api/sprite/..%2F..%2Fkey.txt")]
    [InlineData("/api/sprite/../trench-map.png")]
    [InlineData("/api/sprite/x.txt")]
    [InlineData("/api/sprite/a%2Fb.png")]
    [InlineData("/api/sprite/..png")]
    [InlineData("/api/thumb/..%2Fthumbs.json")]
    [InlineData("/api/thumb/..%5C..%5Cx.png")]
    [InlineData("/api/thumb/%2Fetc%2Fpasswd")]
    public void Pictures_RejectPathsOutOfTheirFolder(string path)
        => Assert.Equal(400, withGame.Handle("GET", path, null).status);

    [Fact]
    public void Reload_ReplacesTheGameData()
    {
        int calls = 0;
        var api2 = new DesignerApi(dir, GameData.Failed("not yet"), () => { calls++; return new GameData { Catalogue = Catalogue.Parse(CatalogueJson()), Folder = gameFolder }; });
        Assert.Equal(503, api2.Handle("GET", "/api/catalogue", null).status);
        Assert.Equal(200, api2.Handle("POST", "/api/reload", B("{}")).status);
        Assert.Equal(1, calls);
        Assert.Equal(200, api2.Handle("GET", "/api/catalogue", null).status);
        Assert.Equal(404, api.Handle("POST", "/api/reload", B("{}")).status); // not offered
    }

    [Fact]
    public void List_SortedWithoutCatalogue()
    {
        File.WriteAllText(Path.Combine(dir, "b.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "a.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "catalogue.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "thumbs.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "a.json.bak"), "{}");
        var (status, _, body) = api.Handle("GET", "/api/galaxies", null);
        Assert.Equal(200, status);
        Assert.Equal(new List<object> { "a", "b" }, (List<object>)MiniJson.Parse(S(body)));
    }

    [Fact]
    public void Put_WritesBakOfPrevious()
    {
        string first = GalaxyJson(), second = GalaxyJson("Star_Other");
        Assert.Equal(200, api.Handle("PUT", "/api/galaxy/test-reach", B(first)).status);
        Assert.False(File.Exists(Path.Combine(dir, "test-reach.json.bak")));
        Assert.Equal(200, api.Handle("PUT", "/api/galaxy/test-reach", B(second)).status);
        Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "test-reach.json.bak")));
        Assert.Equal(second, File.ReadAllText(Path.Combine(dir, "test-reach.json")));
        Assert.Equal(second, S(api.Handle("GET", "/api/galaxy/test-reach", null).body));
    }

    [Fact]
    public void Put_SavedAsSent_ThroughATmpFile()
    {
        // Odd spacing and a BOM-free UTF-8 body: the file is these bytes exactly, so the mod's hash is the sent text's.
        byte[] sent = B(GalaxyJson().Replace("\n", "\r\n  "));
        File.WriteAllText(Path.Combine(dir, "test-reach.json.tmp"), "left by a crash");
        Assert.Equal(200, api.Handle("PUT", "/api/galaxy/test-reach", sent).status);
        Assert.Equal(sent, File.ReadAllBytes(Path.Combine(dir, "test-reach.json")));
        Assert.Equal(GalaxyFile.Hash(S(sent)), GalaxyFile.Hash(File.ReadAllText(Path.Combine(dir, "test-reach.json"))));
        Assert.False(File.Exists(Path.Combine(dir, "test-reach.json.tmp")));
    }

    [Fact]
    public void Put_FailedWrite_LeavesTheOldFile()
    {
        string first = GalaxyJson();
        Assert.Equal(200, api.Handle("PUT", "/api/galaxy/test-reach", B(first)).status);
        Directory.CreateDirectory(Path.Combine(dir, "test-reach.json.tmp")); // the write can't happen
        Assert.Equal(500, api.Handle("PUT", "/api/galaxy/test-reach", B(GalaxyJson("Star_Other"))).status);
        Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "test-reach.json")));
    }

    [Theory]
    [InlineData("..%2Fx")]
    [InlineData("A%20B")]
    [InlineData("..")]
    [InlineData("catalogue")]
    [InlineData("thumbs")]
    public void Put_RejectsBadName(string name)
    {
        Assert.Equal(400, api.Handle("PUT", "/api/galaxy/" + name, B(GalaxyJson())).status);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Put_RejectsUnparsableBody()
    {
        var (status, _, body) = api.Handle("PUT", "/api/galaxy/x", B("not json"));
        Assert.Equal(400, status);
        Assert.Contains("error", S(body));
        Assert.False(File.Exists(Path.Combine(dir, "x.json")));
    }

    [Fact]
    public void Get_UnknownGalaxyIs404()
        => Assert.Equal(404, api.Handle("GET", "/api/galaxy/nope", null).status);

    static List<object> Problems(byte[] body)
        => (List<object>)((Dictionary<string, object>)MiniJson.Parse(S(body)))["problems"];

    [Fact]
    public void Check_ReturnsProblems()
    {
        Assert.Empty(Problems(withGame.Handle("POST", "/api/check", B(GalaxyJson())).body));
        var bad = Problems(withGame.Handle("POST", "/api/check", B(GalaxyJson("Nope"))).body);
        Assert.Contains("kessar/star: no prefab 'Nope' in the game", bad.Cast<string>());
    }

    [Fact]
    public void Check_WithoutGameData_OnlyStructuralProblems()
    {
        var f = GalaxyFile.Parse(GalaxyJson("Nope"));
        f.Systems[0].Nodes[1].Parent = "ghost";
        var (status, _, body) = api.Handle("POST", "/api/check", B(f.ToJson()));
        Assert.Equal(200, status);
        var p = Problems(body).Cast<string>().ToList();
        Assert.Contains("kessar/stn: parent 'ghost' isn't in this system", p);
        Assert.Single(p);
    }

    [Fact]
    public void Check_BadJsonIsOneProblem()
    {
        var (status, _, body) = api.Handle("POST", "/api/check", B("{ nope"));
        Assert.Equal(200, status);
        Assert.Single(Problems(body));
    }
}
