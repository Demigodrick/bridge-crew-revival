using System.Text;

namespace Galaxy.Tests;

public class DesignerApiTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "galaxy-designer-" + Guid.NewGuid().ToString("N"));
    readonly DesignerApi api;

    public DesignerApiTests()
    {
        Directory.CreateDirectory(dir);
        api = new DesignerApi(dir);
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

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
                        new GalaxyNode { Id = "star", Name = "Star", Prefab = prefab, Tags = new List<string> { "Player" } },
                        new GalaxyNode { Id = "stn", Parent = "star", Name = "Stn", Prefab = "Planet_Rock", Tags = new List<string> { "SpaceStation" } },
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
        },
        Tags = new List<string> { "Player", "SpaceStation" },
        SpawnTags = new List<string> { "Player" },
        UniverseWidth = 1000, UniverseHeight = 1000,
    }.ToJson();

    [Fact]
    public void Catalogue_MissingGives404WithHowTo()
    {
        var (status, type, body) = api.Handle("GET", "/api/catalogue", null);
        Assert.Equal(404, status);
        Assert.Contains("json", type);
        Assert.Contains("No catalogue yet", S(body));
        Assert.Contains("CatalogueKey", S(body));
    }

    [Fact]
    public void Catalogue_ServedWhenPresent()
    {
        File.WriteAllText(Path.Combine(dir, "catalogue.json"), CatalogueJson());
        var (status, _, body) = api.Handle("GET", "/api/catalogue", null);
        Assert.Equal(200, status);
        Assert.Contains("Star_Yellow", S(body));
    }

    [Fact]
    public void TrenchMap_MissingIs404Json()
    {
        var (status, type, body) = api.Handle("GET", "/api/trench-map.png", null);
        Assert.Equal(404, status);
        Assert.Contains("error", S(body));
        File.WriteAllBytes(Path.Combine(dir, "trench-map.png"), new byte[] { 1, 2, 3 });
        var ok = api.Handle("GET", "/api/trench-map.png", null);
        Assert.Equal(200, ok.status);
        Assert.Equal("image/png", ok.contentType);
    }

    [Fact]
    public void List_SortedWithoutCatalogue()
    {
        File.WriteAllText(Path.Combine(dir, "b.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "a.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "catalogue.json"), "{}");
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

    [Theory]
    [InlineData("..%2Fx")]
    [InlineData("A%20B")]
    [InlineData("..")]
    [InlineData("catalogue")]
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
        File.WriteAllText(Path.Combine(dir, "catalogue.json"), CatalogueJson());
        Assert.Empty(Problems(api.Handle("POST", "/api/check", B(GalaxyJson())).body));
        var bad = Problems(api.Handle("POST", "/api/check", B(GalaxyJson("Nope"))).body);
        Assert.Contains("kessar/star: no prefab 'Nope' in the game", bad.Cast<string>());
    }

    [Fact]
    public void Check_WithoutCatalogue_OnlyStructuralProblems()
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
