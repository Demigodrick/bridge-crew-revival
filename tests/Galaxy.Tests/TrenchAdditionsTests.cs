namespace Galaxy.Tests;

public class TrenchAdditionsTests
{
    static GalaxyNode Node(string id, string parent = null, string prefab = "Star_Yellow", params string[] tags) => new GalaxyNode
    {
        Id = id, Parent = parent, Name = id, Prefab = prefab, Unit = "Kilometers", Tags = new List<string>(tags), LandingRadius = 1000,
    };

    static Catalogue Cat() => new Catalogue
    {
        TrenchSystems = new List<CatalogueSystem> { new CatalogueSystem { Name = "2824|Venov", DisplayName = "Venov", Nodes = new List<GalaxyNode>(), Families = new List<CatalogueFamily>() } },
        Prefabs = new List<CataloguePrefab>
        {
            new CataloguePrefab { Name = "Star_Yellow", Kind = "star", Radius = 10 },
            new CataloguePrefab { Name = "Family Group", Kind = "other" },
        },
        Sprites = new List<string>(), Tags = new List<string>(), Skyboxes = new List<string>(),
        SpawnTags = new List<string> { "Player" },
        UniverseWidth = 1000, UniverseHeight = 1000,
    };

    static GalaxyFile Only(string system, params GalaxyNode[] nodes) => new GalaxyFile
    {
        Name = "R",
        Trench = new List<TrenchAddition> { new TrenchAddition { System = system, Nodes = new List<GalaxyNode>(nodes) } },
    };

    [Fact]
    public void RoundTrip_KeepsTrenchAdditions()
    {
        var f = Only("2824|Venov", Node("fam", null, "Family Group"), Node("x", "fam"));
        var back = GalaxyFile.Parse(f.ToJson());
        Assert.Single(back.Trench);
        Assert.Equal("2824|Venov", back.Trench[0].System);
        Assert.Equal(new[] { "fam", "x" }, back.Trench[0].Nodes.ConvertAll(n => n.Id).ToArray());
        Assert.Equal("fam", back.Trench[0].Nodes[1].Parent);
    }

    [Fact]
    public void ToJson_OmitsEmptyTrench() => Assert.DoesNotContain("\"trench\"", new GalaxyFile { Name = "R" }.ToJson());

    [Fact]
    public void Parse_TrenchDuplicateNodeId_Throws()
    {
        var f = Only("2824|Venov", Node("x", null, "Family Group"), Node("x", null, "Family Group"));
        var e = Assert.Throws<FormatException>(() => GalaxyFile.Parse(f.ToJson()));
        Assert.Contains("trench/2824|Venov/x", e.Message);
    }

    [Fact]
    public void Trench_UnknownSystem()
        => Assert.Contains("trench Nope: no Trench system 'Nope'",
            GalaxyCheck.Problems(Only("Nope", Node("fam", null, "Family Group")), Cat()));

    [Fact]
    public void Trench_UnknownSystem_NullCatalogueSkipsIt()
        => Assert.DoesNotContain(GalaxyCheck.Problems(Only("Nope", Node("fam", null, "Family Group")), null), x => x.Contains("no Trench system"));

    [Fact]
    public void Trench_PlainNameMatches()
        => Assert.DoesNotContain(GalaxyCheck.Problems(Only("venov", Node("fam", null, "Family Group")), Cat()), x => x.Contains("no Trench system"));

    [Fact]
    public void Trench_ExactNameMatches()
        => Assert.DoesNotContain(GalaxyCheck.Problems(Only("2824|Venov", Node("fam", null, "Family Group")), Cat()), x => x.Contains("no Trench system"));

    [Fact]
    public void Trench_TopLevelNotFamily()
        => Assert.Contains("trench Venov/x: top-level nodes must be families (Family Group) or a copied family",
            GalaxyCheck.Problems(Only("2824|Venov", Node("x")), Cat()));

    [Fact]
    public void Trench_UnknownPrefab()
        => Assert.Contains("trench Venov/x: no prefab 'Nope' in the game",
            GalaxyCheck.Problems(Only("2824|Venov", Node("fam", null, "Family Group"), Node("x", "fam", "Nope")), Cat()));

    [Fact]
    public void AdditionsOnly_Valid()
        => Assert.Empty(GalaxyCheck.Problems(Only("2824|Venov", Node("fam", null, "Family Group"), Node("x", "fam")), Cat()));

    [Fact]
    public void Empty_File_StillNoSystems()
        => Assert.Equal(new[] { "The region has no systems" }, GalaxyCheck.Problems(new GalaxyFile { Name = "R" }, Cat()).ToArray());

    [Fact]
    public void PrefabNames_IncludeAdditions()
        => Assert.Contains("Star_Yellow", GalaxyCheck.PrefabNames(Only("2824|Venov", Node("fam", null, "Family Group"), Node("x", "fam"))));
}
