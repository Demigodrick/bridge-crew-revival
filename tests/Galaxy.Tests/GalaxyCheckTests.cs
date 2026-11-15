namespace Galaxy.Tests;

public class GalaxyCheckTests
{
    static GalaxyNode Node(string id, string parent = null, string prefab = "Star_Yellow", params string[] tags) => new GalaxyNode
    {
        Id = id, Parent = parent, Name = id, Prefab = prefab, Unit = "Kilometers", Tags = new List<string>(tags), LandingRadius = 1000,
    };

    static GalaxySystem System(string id, float x, float z, params GalaxyNode[] nodes) => new GalaxySystem
    {
        Id = id, Name = id, X = x, Z = z, Template = "Venov", Nodes = new List<GalaxyNode>(nodes),
    };

    static Catalogue Cat() => new Catalogue
    {
        TrenchSystems = new List<CatalogueSystem> { new CatalogueSystem { Name = "Venov", Nodes = new List<GalaxyNode>() } },
        Prefabs = new List<CataloguePrefab>
        {
            new CataloguePrefab { Name = "Star_Yellow", Kind = "star", Radius = 10 },
            new CataloguePrefab { Name = "Planet_Rock", Kind = "planet", Radius = 2 },
        },
        Tags = new List<string> { "Player", "SpaceStation", "Planet" },
        Skyboxes = new List<string>(),
        SpawnTags = new List<string> { "Player" },
        UniverseWidth = 1000, UniverseHeight = 1000,
    };

    // One valid system: a jump point and a station.
    static GalaxyFile Valid() => new GalaxyFile
    {
        Format = 1, Name = "R", Page = new Page { Label = "R", Background = "trench-darkened" },
        Systems = new List<GalaxySystem>
        {
            System("kessar", 100, 100,
                Node("kessar-star", null, "Star_Yellow", "Player"),
                Node("kessar-station", "kessar-star", "Planet_Rock", "SpaceStation")),
        },
    };

    [Fact]
    public void Valid_NoProblems() => Assert.Empty(GalaxyCheck.Problems(Valid(), Cat()));

    [Fact]
    public void UnknownPrefab()
    {
        var f = Valid(); f.Systems[0].Nodes[0].Prefab = "X";
        Assert.Contains("kessar/kessar-star: no prefab 'X' in the game", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void UnknownTag()
    {
        var f = Valid(); f.Systems[0].Nodes[0].Tags.Add("X");
        Assert.Contains("kessar/kessar-star: no tag 'X' in the game", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void UnknownTemplate()
    {
        var f = Valid(); f.Systems[0].Template = "X";
        Assert.Contains("kessar: no Trench system 'X' to copy", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void ParentMissing()
    {
        var f = Valid(); f.Systems[0].Nodes.Add(Node("moon", "nope", "Planet_Rock"));
        Assert.Contains("kessar/moon: parent 'nope' isn't in this system", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void ParentCycle()
    {
        var f = Valid();
        f.Systems[0].Nodes.Add(Node("a", "b", "Planet_Rock"));
        f.Systems[0].Nodes.Add(Node("b", "a", "Planet_Rock"));
        Assert.Contains("kessar/a: is its own ancestor", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void NoJumpPoint()
    {
        var f = Valid(); f.Systems[0].Nodes[0].Tags.Clear();
        Assert.Contains("kessar: no jump point (a node tagged Player)", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void NoStationInRegion()
    {
        var f = Valid(); f.Systems[0].Nodes.RemoveAt(1);
        Assert.Contains("No station anywhere in the region: Patrol can't restock there", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void SystemNameClashesWithTrench()
    {
        var f = Valid(); f.Systems[0].Name = "Venov";
        Assert.Contains("kessar: 'Venov' is already a Trench system", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void SystemsOverlap()
    {
        var f = Valid();
        f.Systems.Add(System("dorn", 110, 105, Node("dorn-star", null, "Star_Yellow", "Player")));
        Assert.Contains("kessar and dorn overlap on the map", GalaxyCheck.Problems(f, Cat()));
        f.Systems[1].X = 200; f.Systems[1].Z = 200;
        Assert.DoesNotContain("kessar and dorn overlap on the map", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void NoSystems()
    {
        var f = Valid(); f.Systems.Clear();
        Assert.Contains("The region has no systems", GalaxyCheck.Problems(f, Cat()));
    }
}

public class CatalogueTests
{
    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var c = new Catalogue
        {
            TrenchSystems = new List<CatalogueSystem> { new CatalogueSystem {
                Name = "Venov", X = 5, Z = 6, Skybox = "Sky/A", SystemTags = new List<string> { "T" },
                Nodes = new List<GalaxyNode> { new GalaxyNode { Id = "n", Name = "N", Prefab = "P", X = 1, Y = 2, Z = 3, Unit = "Skybox", Tags = new List<string> { "Player" } } } } },
            Prefabs = new List<CataloguePrefab> { new CataloguePrefab { Name = "P", Kind = "planet", Radius = 2.5 } },
            Tags = new List<string> { "Player" }, Skyboxes = new List<string> { "Sky/A" }, SpawnTags = new List<string> { "Player" },
            UniverseMinX = -10, UniverseMinZ = -20, UniverseWidth = 1000, UniverseHeight = 800, Background = "map.png",
        };
        var d = Catalogue.Parse(c.ToJson());
        Assert.Equal(c.ToJson(), d.ToJson());
        Assert.Equal("Venov", d.TrenchSystems[0].Name);
        Assert.Equal(3f, d.TrenchSystems[0].Nodes[0].Z);
        Assert.Equal(2.5, d.Prefabs[0].Radius);
        Assert.Equal(800f, d.UniverseHeight);
    }
}
