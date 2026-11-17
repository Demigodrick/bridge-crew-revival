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
        TrenchSystems = new List<CatalogueSystem> { new CatalogueSystem { Name = "Venov", Nodes = new List<GalaxyNode>(), Families = new List<CatalogueFamily> {
            new CatalogueFamily { Name = "FAMILY Venov III", Pois = new List<string> { "POI Start Planet" }, HasSpawn = true },
            new CatalogueFamily { Name = "FAMILY Empty", Pois = new List<string>(), HasSpawn = false } } } },
        Prefabs = new List<CataloguePrefab>
        {
            new CataloguePrefab { Name = "Star_Yellow", Kind = "star", Radius = 10 },
            new CataloguePrefab { Name = "Planet_Rock", Kind = "planet", Radius = 2 },
            new CataloguePrefab { Name = "Family Group", Kind = "other" },
            new CataloguePrefab { Name = "Point of Interest", Kind = "other" },
            new CataloguePrefab { Name = "Region", Kind = "other" },
        },
        Sprites = new List<string> { "Icon_A", "Bg_A" },
        Tags = new List<string> { "Player", "SpaceStation", "Planet" },
        Skyboxes = new List<string>(),
        SpawnTags = new List<string> { "Player" },
        UniverseWidth = 1000, UniverseHeight = 1000,
    };

    // One valid system: family -> POI -> region -> a star with the jump point and a station.
    static GalaxyFile Valid() => new GalaxyFile
    {
        Format = 1, Name = "R", Page = new Page { Label = "R", Background = "trench-darkened" },
        Systems = new List<GalaxySystem>
        {
            System("kessar", 100, 100,
                Node("kessar-fam", null, "Family Group"),
                Node("kessar-poi", "kessar-fam", "Point of Interest"),
                Node("kessar-region", "kessar-poi", "Region"),
                Node("kessar-star", "kessar-region", "Star_Yellow", "Player"),
                Node("kessar-station", "kessar-star", "Planet_Rock", "SpaceStation")),
        },
    };

    static GalaxyNode Copy(string id, string sys, string fam) => new GalaxyNode
    {
        Id = id, Name = id, Unit = "Lightseconds", CopyFamily = new CopyFamily { System = sys, Family = fam },
    };

    [Fact]
    public void Valid_NoProblems() => Assert.Empty(GalaxyCheck.Problems(Valid(), Cat()));

    [Fact]
    public void NullCatalogue_OnlyStructuralProblems()
    {
        var f = Valid(); f.Systems[0].Nodes[3].Prefab = "X"; f.Systems[0].Nodes[3].Tags.Add("Y");
        f.Systems[0].Nodes[4].Parent = "ghost";
        var p = GalaxyCheck.Problems(f, null);
        Assert.Contains("kessar/kessar-station: parent 'ghost' isn't in this system", p);
        Assert.Single(p);
    }

    [Fact]
    public void UnknownPrefab()
    {
        var f = Valid(); f.Systems[0].Nodes[3].Prefab = "X";
        Assert.Contains("kessar/kessar-star: no prefab 'X' in the game", GalaxyCheck.Problems(f, Cat()));
    }

    static List<string> EmptyPrefabProblems(string parent, params string[] tags)
    {
        var f = Valid(); f.Systems[0].Nodes[3].Tags.Clear();
        f.Systems[0].Nodes[4].Tags.Clear(); f.Systems[0].Nodes[4].Tags.Add("SpaceStation");
        f.Systems[0].Nodes.Add(Node("kessar-x", parent, "", tags));
        var c = Cat(); c.Tags.Add("WarpDestination"); c.Tags.Add("SpawnPoint"); c.Tags.Add("AltWarpDestination");
        return GalaxyCheck.Problems(f, c);
    }

    [Fact]
    public void EmptyPrefab_OnJumpPoint_Ok()
        => Assert.DoesNotContain(EmptyPrefabProblems("kessar-region", "WarpDestination", "Player"), x => x.Contains("kessar-x"));

    [Theory]
    [InlineData("SpawnPoint")]
    [InlineData("AltWarpDestination")]
    public void EmptyPrefab_OnSpawnMarker_Ok(string tag)
        => Assert.DoesNotContain(EmptyPrefabProblems("kessar-region", tag), x => x.Contains("kessar-x"));

    [Fact]
    public void EmptyPrefab_OnRegionUnderPoi_Ok()
        => Assert.DoesNotContain(EmptyPrefabProblems("kessar-poi"), x => x.Contains("kessar-x"));

    [Fact]
    public void EmptyPrefab_OnBody_Reported()
        => Assert.Contains("kessar/kessar-x: no model chosen", EmptyPrefabProblems("kessar-region", "Planet"));

    [Fact]
    public void Parse_EmptyPrefab_Ok()
    {
        var f = GalaxyFile.Parse(@"{ ""format"": 1, ""name"": ""R"", ""systems"": [ { ""id"": ""a"", ""name"": ""A"", ""position"": [0,0], ""template"": ""V"",
          ""nodes"": [ { ""id"": ""w"", ""parent"": null, ""name"": ""Warp"", ""prefab"": """", ""position"": [0,0,0] } ] } ] }");
        Assert.Equal("", f.Systems[0].Nodes[0].Prefab);
    }

    [Fact]
    public void UnknownTag()
    {
        var f = Valid(); f.Systems[0].Nodes[3].Tags.Add("X");
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
        var f = Valid(); f.Systems[0].Nodes[3].Tags.Clear();
        Assert.Contains("kessar: no jump point (a node tagged Player)", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void BodyDirectlyOnFamily_IsBackdropScenery_NoProblemOrWarning()
    {
        var f = Valid();
        var nebula = Node("kessar-nebula", "kessar-fam", "Planet_Rock"); nebula.Unit = "Skybox"; nebula.X = 600;
        f.Systems[0].Nodes.Add(nebula);
        List<string> w;
        var problems = GalaxyCheck.Problems(f, Cat(), out w);
        Assert.DoesNotContain(problems, x => x.Contains("kessar-nebula"));
        Assert.DoesNotContain(w, x => x.Contains("kessar-nebula"));
        Assert.Empty(problems);
    }

    [Fact]
    public void NoStationInRegion_IsAWarningNotAProblem()
    {
        var f = Valid(); f.Systems[0].Nodes.RemoveAt(4);
        List<string> w;
        var problems = GalaxyCheck.Problems(f, Cat(), out w);
        Assert.DoesNotContain(GalaxyCheck.NoStationWarning, problems);
        Assert.Empty(problems);
        Assert.Contains(GalaxyCheck.NoStationWarning, w);
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
        f.Systems.Add(System("dorn", 110, 105, Node("dorn-fam", null, "Family Group"), Node("dorn-star", "dorn-fam", "Star_Yellow", "Player")));
        Assert.Contains("kessar and dorn overlap on the map", GalaxyCheck.Problems(f, Cat()));
        f.Systems[1].X = 200; f.Systems[1].Z = 200;
        Assert.DoesNotContain("kessar and dorn overlap on the map", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void TopLevelNotFamily()
    {
        var f = Valid(); f.Systems[0].Nodes.Add(Node("x", null, "Planet_Rock"));
        Assert.Contains("kessar/x: top-level nodes must be families (Family Group) or a copied family", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void PoiNotUnderFamily()
    {
        var f = Valid(); f.Systems[0].Nodes.Add(Node("x", "kessar-region", "Point of Interest"));
        Assert.Contains("kessar/x: a Point of Interest must sit directly under a family", GalaxyCheck.Problems(f, Cat()));
        f = Valid(); f.Systems[0].Nodes.Add(Node("y", null, "Point of Interest"));
        Assert.Contains("kessar/y: a Point of Interest must sit directly under a family", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void NoPoi()
    {
        var f = Valid(); f.Systems[0].Nodes.RemoveRange(1, 4); f.Systems[0].Nodes.Add(Node("kessar-s", "kessar-fam", "Star_Yellow", "Player", "SpaceStation"));
        Assert.Contains("kessar: no family with a Point of Interest: the impulse map would be empty", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void CopiedFamilyWithoutPoi_IsNoPoi()
    {
        var f = Valid(); f.Systems[0].Nodes.RemoveRange(0, 3);
        f.Systems[0].Nodes.Insert(0, Copy("c", "Venov", "FAMILY Empty"));
        f.Systems[0].Nodes[1].Parent = "c";
        Assert.Contains("kessar: no family with a Point of Interest: the impulse map would be empty", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void CopiedFamily_ValidAndCountsAsPoi()
    {
        var f = Valid(); f.Systems[0].Nodes.RemoveRange(0, 4);
        f.Systems[0].Nodes.Insert(0, Copy("c", "Venov", "FAMILY Venov III"));
        f.Systems[0].Nodes.Add(Node("kessar-station", null, "Planet_Rock", "SpaceStation"));
        f.Systems[0].Nodes.RemoveAt(1);
        f.Systems[0].Nodes.Add(Node("kessar-fam", null, "Family Group"));
        f.Systems[0].Nodes[1].Parent = "kessar-fam";
        Assert.Empty(GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void UnknownCopyFamily()
    {
        var f = Valid(); f.Systems[0].Nodes.Add(Copy("x", "Venov", "F"));
        Assert.Contains("kessar/x: no family 'F' in Trench system 'Venov'", GalaxyCheck.Problems(f, Cat()));
        f.Systems[0].Nodes[5] = Copy("x", "S", "F");
        Assert.Contains("kessar/x: no family 'F' in Trench system 'S'", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void UnknownSprite()
    {
        var f = Valid(); f.Systems[0].Nodes[0].FamilyBackground = "X"; f.Systems[0].Nodes[0].MapIcon = "Icon_A";
        var p = GalaxyCheck.Problems(f, Cat());
        Assert.Contains("kessar/kessar-fam: no sprite 'X' in the game", p);
        f.Systems[0].Nodes[0].FamilyBackground = "Bg_A"; f.Systems[0].Nodes[0].MapIcon = "Y";
        Assert.Contains("kessar/kessar-fam: no sprite 'Y' in the game", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void JumpPoint_FromCopiedFamily_Counts()
    {
        var f = Valid(); f.Systems[0].Nodes[3].Tags.Clear();
        f.Systems[0].Nodes.Add(Copy("c", "Venov", "FAMILY Venov III"));
        Assert.DoesNotContain("kessar: no jump point (a node tagged Player)", GalaxyCheck.Problems(f, Cat()));
        f.Systems[0].Nodes[5] = Copy("c", "Venov", "FAMILY Empty");
        Assert.Contains("kessar: no jump point (a node tagged Player)", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void KeyedName()
    {
        var f = Valid(); f.Systems[0].Name = "2824|Kessar";
        Assert.Contains("kessar: use a plain name, not a text key ('2824|Kessar')", GalaxyCheck.Problems(f, Cat()));
        f = Valid(); f.Systems[0].Nodes[3].Name = "1|x";
        Assert.Contains("kessar/kessar-star: use a plain name, not a text key ('1|x')", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void NullCatalogue_SkipsCopyFamilyAndSprites()
    {
        var f = Valid(); f.Systems[0].Nodes[0].MapIcon = "Nope"; f.Systems[0].Nodes.Add(Copy("x", "Nowhere", "F"));
        Assert.Empty(GalaxyCheck.Problems(f, null));
        f.Systems[0].Nodes.Add(Node("t", null, "Planet_Rock"));
        Assert.Contains("kessar/t: top-level nodes must be families (Family Group) or a copied family", GalaxyCheck.Problems(f, null));
    }

    [Fact]
    public void NoStation_CopiedFamilyWithStation_Counts()
    {
        var c = Cat(); c.TrenchSystems[0].Families[0].HasStation = true;
        var f = Valid(); f.Systems[0].Nodes.RemoveAt(4);
        Assert.Contains(GalaxyCheck.NoStationWarning, Warnings(f, c));
        f.Systems[0].Nodes.Add(Copy("c", "Venov", "FAMILY Venov III"));
        Assert.DoesNotContain(GalaxyCheck.NoStationWarning, Warnings(f, c));
    }

    [Fact]
    public void EmptySprites_SkipsSpriteCheck()
    {
        var c = Cat(); c.Sprites.Clear();
        var f = Valid(); f.Systems[0].Nodes[0].MapIcon = "Nope";
        Assert.Empty(GalaxyCheck.Problems(f, c));
    }

    [Fact]
    public void TrenchSystemWithoutFamilies_SkipsCopyFamilyCheck()
    {
        var c = Cat(); c.TrenchSystems[0].Families.Clear();
        var f = Valid(); f.Systems[0].Nodes.Add(Copy("x", "Venov", "Anything"));
        Assert.Empty(GalaxyCheck.Problems(f, c));
    }

    [Fact]
    public void NoSystems()
    {
        var f = Valid(); f.Systems.Clear();
        Assert.Contains("The region has no systems", GalaxyCheck.Problems(f, Cat()));
    }

    [Fact]
    public void Station_FromPrefabKind_Counts()
    {
        var c = Cat(); c.Prefabs.Add(new CataloguePrefab { Name = "Station_X", Kind = "station", Radius = 1 });
        var f = Valid(); var st = f.Systems[0].Nodes[4]; st.Prefab = "Station_X"; st.Tags.Clear();
        Assert.Empty(GalaxyCheck.Problems(f, c));
    }

    [Fact]
    public void Station_FromPrefabKind_IgnoredWithoutCatalogue_ButNodeTagStillCounts()
    {
        var f = Valid(); var st = f.Systems[0].Nodes[4]; st.Prefab = "Station_X"; st.Tags.Clear();
        Assert.Contains(GalaxyCheck.NoStationWarning, Warnings(f, null));
        st.Tags.Add("SpaceStation");
        Assert.Empty(GalaxyCheck.Problems(f, null));
    }

    [Fact]
    public void CopiedFamily_UnknownFamilies_ExcusesJumpAndStation()
    {
        var c = Cat(); c.TrenchSystems[0].Families.Clear();
        var f = new GalaxyFile
        {
            Format = 1, Name = "R", Page = new Page { Label = "R", Background = "trench-darkened" },
            Systems = new List<GalaxySystem> { System("kessar", 100, 100, Copy("c", "Venov", "FAMILY Venov III")) },
        };
        Assert.Empty(GalaxyCheck.Problems(f, c));
    }

    // ---- The region check without all of Resources/Sector (GalaxyCatalogue.Build with a lookup) ----------------

    // The mod's catalogue for the region check: the Trench's prefabs, plus those of PrefabNames the lookup finds.
    static Catalogue TrenchPlusFound(Catalogue trenchOnly, GalaxyFile f, Dictionary<string, CataloguePrefab> game)
    {
        var have = new HashSet<string>(trenchOnly.Prefabs.Select(x => x.Name));
        foreach (var name in GalaxyCheck.PrefabNames(f))
            if (!have.Contains(name) && game.TryGetValue(name, out var found)) { trenchOnly.Prefabs.Add(found); have.Add(name); }
        return trenchOnly;
    }

    // Every prefab the game has (the old full catalogue), by name: the Trench's and some only under Resources/Sector.
    static Dictionary<string, CataloguePrefab> Game()
    {
        var all = Cat().Prefabs.ToDictionary(x => x.Name);
        all["Planet_Ice"] = new CataloguePrefab { Name = "Planet_Ice", Kind = "planet", Radius = 3 };
        all["Station_X"] = new CataloguePrefab { Name = "Station_X", Kind = "station", Radius = 1 };
        return all;
    }

    // A system with no design hints at all: family -> POI -> region (arrival + spawn) with a station.
    static GalaxyFile Tidy()
    {
        var sys = System("kessar", 100, 100,
            Node("fam", null, "Family Group"), Node("poi", "fam", "Point of Interest"), Node("reg", "poi", "Region"),
            Node("arr", "reg", "Impulse Destination"), Node("sp", "reg", "", "SpawnPoint"), Node("jp", "reg", "", "Player"),
            Node("stn", "reg", "Planet_Rock", "SpaceStation"));
        foreach (var n in sys.Nodes) n.Name = "Kessar " + n.Id;
        sys.Name = "Kessar";
        foreach (var n in sys.Nodes) if (n.Id == "jp") n.Tags.Add("WarpDestination");
        return new GalaxyFile { Format = 1, Name = "R", Systems = new List<GalaxySystem> { sys } };
    }

    static GalaxyNode Find(GalaxyFile f, string id) => f.Systems[0].Nodes.Find(n => n.Id == id);
    static bool Has(List<string> w, string text) => w.Exists(x => x.Contains(text));

    [Fact]
    public void Hints_TidyHasNone()
    {
        var f = Tidy();
        Assert.Empty(GalaxyCheck.Problems(f, HintCat()));
        Assert.Empty(Warnings(f, HintCat()));
    }

    [Fact]
    public void Hint_DefaultNames()
    {
        var f = Tidy();
        f.Systems[0].Name = "New system 2"; Find(f, "fam").Name = "New family 1"; Find(f, "poi").Name = "New POI"; Find(f, "reg").Name = "New region 3";
        var w = Warnings(f, HintCat());
        Assert.Contains("kessar: still has its default name 'New system 2'", w);
        Assert.Contains("kessar/fam: still has its default name 'New family 1'", w);
        Assert.Contains("kessar/poi: still has its default name 'New POI'", w);
        Assert.Contains("kessar/reg: still has its default name 'New region 3'", w);
        Find(f, "reg").Name = "New regional"; Assert.False(Has(Warnings(f, HintCat()), "kessar/reg: still"));
    }

    [Fact]
    public void Hint_NotARegionButHoldsOne()
    {
        var f = Tidy();
        Find(f, "reg").Prefab = "AlienDebrisCluster";
        Assert.Contains("kessar/reg: holds an arrival point and spawn points but its model is 'AlienDebrisCluster', not Region: make it a Region and put the AlienDebrisCluster inside it", Warnings(f, null));
        Assert.False(Has(Warnings(Tidy(), null), "holds an arrival"));
        // A body inside a region that has no markers is fine.
        var g = Tidy(); g.Systems[0].Nodes.Add(Node("moon", "reg", "Planet_Rock")); Assert.False(Has(Warnings(g, null), "moon:"));
    }

    [Fact]
    public void Hint_PoiFarFromFamily()
    {
        var f = Tidy();
        var poi = Find(f, "poi"); poi.Unit = "Lightseconds"; poi.X = -40;
        Assert.Contains("kessar/poi: is about 11,991,698 km from its family's centre; Trench POIs are 10,000-150,000 km (check its Unit: Skybox = 1,000 km)", Warnings(f, null));
        poi.Unit = "Skybox"; poi.X = 100; Assert.False(Has(Warnings(f, null), "from its family's centre"));
        poi.X = 2000; Assert.True(Has(Warnings(f, null), "from its family's centre"));
    }

    [Fact]
    public void Hint_FamilyWithoutPoi()
    {
        var f = Tidy();
        f.Systems[0].Nodes.Add(Node("fam2", null, "Family Group"));
        Assert.Contains("kessar/fam2: has no Points of Interest: its impulse map page will be empty", Warnings(f, HintCat()));
        Assert.False(Has(Warnings(f, HintCat()), "kessar/fam:"));
        f.Systems[0].Nodes.RemoveAll(n => n.Id == "fam2");
        f.Systems[0].Nodes.Add(new GalaxyNode { Id = "cp", Name = "cp", Unit = "Lightseconds", CopyFamily = new CopyFamily { System = "Venov", Family = "FAMILY Venov III" } });
        Assert.False(Has(Warnings(f, HintCat()), "kessar/cp"));
    }

    [Fact]
    public void Hint_PoiWithNoArrivalOrSpawn()
    {
        var f = Tidy();
        f.Systems[0].Nodes.Add(Node("poi2", "fam", "Point of Interest"));
        var w = Warnings(f, HintCat());
        Assert.Contains("kessar/poi2: has nowhere to arrive: add a region (Add region gives it an Impulse Destination)", w);
        Assert.Contains("kessar/poi2: has no spawn points: Patrol's ships have nowhere to appear", w);
        Assert.False(Has(w, "kessar/poi:"));
        // Deeper descendants count.
        f.Systems[0].Nodes.Add(Node("r2", "poi2", "Region")); f.Systems[0].Nodes.Add(Node("a2", "r2", "Impulse Destination")); f.Systems[0].Nodes.Add(Node("s2", "r2", "", "SpawnPoint"));
        Assert.False(Has(Warnings(f, HintCat()), "kessar/poi2:"));
    }

    [Fact]
    public void Hint_TrenchAdditionsAreChecked()
    {
        var f = Tidy();
        f.Trench.Add(new TrenchAddition { System = "Venov", Nodes = new List<GalaxyNode> { Node("tf", null, "Family Group") } });
        Assert.True(Has(Warnings(f, HintCat()), "trench Venov/tf: has no Points of Interest"));
    }

    const string Jagged = @"{""format"":1,""name"":""jagged"",""systems"":[{""id"":""s1"",""name"":""Thorn"",""position"":[1,1],""template"":""Venov"",""nodes"":[
      {""id"":""f1"",""name"":""Star"",""prefab"":""Family Group"",""unit"":""Lightseconds"",""position"":[0,0,0]},
      {""id"":""f2"",""name"":""Retreat"",""prefab"":""Family Group"",""unit"":""Lightseconds"",""position"":[3,0,2]},
      {""id"":""p1"",""parent"":""f1"",""name"":""Star"",""prefab"":""Point of Interest"",""unit"":""Lightseconds"",""position"":[0,0,0]},
      {""id"":""d1"",""parent"":""p1"",""name"":""Dying Star"",""prefab"":""Star_Yellow"",""unit"":""Kilometers"",""position"":[0,0,0]},
      {""id"":""p2"",""parent"":""f1"",""name"":""Battleground"",""prefab"":""Point of Interest"",""unit"":""Lightseconds"",""position"":[-40.4,0,-1]},
      {""id"":""r1"",""parent"":""p2"",""name"":""New region"",""prefab"":""AlienDebrisCluster"",""unit"":""Kilometers"",""position"":[0,0,1]},
      {""id"":""a1"",""parent"":""r1"",""name"":""Impulse Destination"",""prefab"":""Impulse Destination"",""unit"":""Kilometers"",""position"":[0,0,0]},
      {""id"":""sp1"",""parent"":""r1"",""name"":""Spawn"",""prefab"":"""",""unit"":""Kilometers"",""position"":[4,0,0],""tags"":[""SpawnPoint""]},
      {""id"":""r2"",""parent"":""p1"",""name"":""New region"",""prefab"":""Region"",""unit"":""Kilometers"",""position"":[0,0,0]},
      {""id"":""a2"",""parent"":""r2"",""name"":""Impulse Destination"",""prefab"":""Impulse Destination"",""unit"":""Kilometers"",""position"":[0,0,0]},
      {""id"":""sp2"",""parent"":""r2"",""name"":""Spawn"",""prefab"":"""",""unit"":""Kilometers"",""position"":[4,0,0],""tags"":[""SpawnPoint""]},
      {""id"":""w2"",""parent"":""r2"",""name"":""Warp in"",""prefab"":"""",""unit"":""Kilometers"",""position"":[8,0,0],""tags"":[""WarpDestination"",""Player""]},
      {""id"":""st"",""parent"":""r1"",""name"":""Station"",""prefab"":""Planet_Rock"",""unit"":""Kilometers"",""position"":[5,0,6],""tags"":[""SpaceStation""]}]}]}";

    [Fact]
    public void Hint_JaggedSwordShape()
    {
        var f = GalaxyFile.Parse(Jagged);
        Assert.Empty(GalaxyCheck.Problems(f, HintCat()));
        var w = Warnings(f, HintCat());
        Assert.Contains("s1/r1: holds an arrival point and spawn points but its model is 'AlienDebrisCluster', not Region: make it a Region and put the AlienDebrisCluster inside it", w);
        Assert.True(Has(w, "s1/p2: is about 12,1"));
        Assert.Contains("s1/f2: has no Points of Interest: its impulse map page will be empty", w);
        Assert.Contains("s1/r1: still has its default name 'New region'", w);
        Assert.Contains("s1/r2: still has its default name 'New region'", w);
    }

    static Catalogue HintCat()
    {
        var c = Cat();
        c.Prefabs.Add(new CataloguePrefab { Name = "Impulse Destination", Kind = "other" });
        c.Prefabs.Add(new CataloguePrefab { Name = "AlienDebrisCluster", Kind = "other" });
        c.Tags.AddRange(new[] { "SpawnPoint", "WarpDestination" });
        return c;
    }

    static List<string> Warnings(GalaxyFile f, Catalogue c) { List<string> w; GalaxyCheck.Problems(f, c, out w); return w; }

    static Catalogue Full() { var c = Cat(); c.Prefabs = Game().Values.ToList(); return c; }

    [Fact]
    public void PrefabNames_OwnNodesOnce_NotCopiesOrEmpty()
    {
        var f = Valid();
        f.Systems[0].Nodes.Add(Copy("kessar-copy", "Venov", "FAMILY Venov III"));
        f.Systems[0].Nodes.Add(Node("kessar-empty", "kessar-region", "", "Player"));
        f.Systems[0].Nodes.Add(Node("kessar-star2", "kessar-region", "Star_Yellow"));
        Assert.Equal(new[] { "Family Group", "Point of Interest", "Region", "Star_Yellow", "Planet_Rock" }, GalaxyCheck.PrefabNames(f));
    }

    [Fact]
    public void RegionCheck_WithoutResourcePrefabs_StillRejectsUnknownPrefab()
    {
        var f = Valid(); f.Systems[0].Nodes[3].Prefab = "X";
        var p = GalaxyCheck.Problems(f, TrenchPlusFound(HintCat(), f, Game()));
        Assert.Contains("kessar/kessar-star: no prefab 'X' in the game", p);
        Assert.Equal(GalaxyCheck.Problems(f, Full()), p);
    }

    [Fact]
    public void RegionCheck_WithoutResourcePrefabs_AcceptsAPrefabOnlyUnderResources()
    {
        // Planet_Ice and the station's prefab aren't the Trench's: found by name, the file passes as with every prefab.
        var f = Valid(); f.Systems[0].Nodes[4].Prefab = "Station_X"; f.Systems[0].Nodes[4].Tags.Clear();
        f.Systems[0].Nodes.Add(Node("kessar-ice", "kessar-region", "Planet_Ice"));
        Assert.Empty(GalaxyCheck.Problems(f, Full()));
        Assert.Empty(GalaxyCheck.Problems(f, TrenchPlusFound(HintCat(), f, Game())));
    }

    [Fact]
    public void RegionCheck_WithoutResourcePrefabs_SameAnswerAsEveryPrefab()
    {
        var files = new List<GalaxyFile>();
        var a = Valid(); a.Systems[0].Nodes[1].Prefab = "Nope"; files.Add(a);
        var b = Valid(); b.Systems[0].Nodes[4].Prefab = "Station_X"; b.Systems[0].Nodes[4].Tags.Clear(); files.Add(b);
        var c = Valid(); c.Systems[0].Nodes[4].Prefab = "Planet_Ice"; c.Systems[0].Nodes[4].Tags.Clear(); files.Add(c); // no station
        var d = Valid(); d.Systems[0].Nodes[3].Prefab = null; files.Add(d);
        foreach (var f in files)
            Assert.Equal(GalaxyCheck.Problems(f, Full()), GalaxyCheck.Problems(f, TrenchPlusFound(HintCat(), f, Game())));
    }

    // The impulse map's system overview spans +/-2e9 m on each axis around ROOT (FamilyMap._maxOrbitRadius); a family
    // further out is drawn off the panel. The Trench's own reach 6.45 lightseconds (Silva VI).
    [Fact]
    public void CopiedFamily_WithAPosition_IsHeldToTheImpulseMap()
    {
        var f = Valid(); var c = Copy("kessar-copy", "Venov", "FAMILY Venov III");
        f.Systems[0].Nodes.Add(c);
        Assert.DoesNotContain(GalaxyCheck.Problems(f, HintCat()), x => x.Contains("kessar-copy: too far out"));   // no position: the Trench's own
        c.HasPosition = true; c.X = 25; c.Z = 0;
        Assert.Contains(GalaxyCheck.Problems(f, HintCat()), x => x.StartsWith("kessar/kessar-copy: too far out for the impulse map"));
        c.X = 3;
        Assert.DoesNotContain(GalaxyCheck.Problems(f, HintCat()), x => x.Contains("kessar-copy: too far out"));
    }

    [Fact]
    public void Family_BeyondTheImpulseMap_IsAProblem()
    {
        var f = Valid(); var fam = f.Systems[0].Nodes[0];
        fam.Unit = "Lightseconds"; fam.X = 25; fam.Z = -25;
        Assert.Contains(GalaxyCheck.Problems(f, HintCat()), x => x.StartsWith("kessar/kessar-fam: too far out for the impulse map"));
    }

    [Fact]
    public void Family_InsideTheImpulseMap_Ok()
    {
        var f = Valid(); var fam = f.Systems[0].Nodes[0];
        fam.Unit = "Lightseconds"; fam.X = 6.45f; fam.Z = -6.45f;
        Assert.Empty(GalaxyCheck.Problems(f, HintCat()));
    }

    [Fact]
    public void TrenchAddition_FamilyBeyondTheImpulseMap_IsAProblem()
    {
        var fam = Node("outpost", null, "Family Group"); fam.Unit = "Lightseconds"; fam.X = 25; fam.Z = -25;
        var f = new GalaxyFile { Format = 1, Name = "R", Systems = new List<GalaxySystem>(),
            Trench = new List<TrenchAddition> { new TrenchAddition { System = "Venov", Nodes = new List<GalaxyNode> { fam } } } };
        Assert.Contains(GalaxyCheck.Problems(f, HintCat()), x => x.StartsWith("trench Venov/outpost: too far out for the impulse map"));
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
                Nodes = new List<GalaxyNode> { new GalaxyNode { Id = "n", Name = "N", Prefab = "P", X = 1, Y = 2, Z = 3, Unit = "Skybox", Tags = new List<string> { "Player" } } },
                Families = new List<CatalogueFamily> { new CatalogueFamily { Name = "FAMILY V", Pois = new List<string> { "POI A" }, HasSpawn = true } } } },
            Prefabs = new List<CataloguePrefab> { new CataloguePrefab { Name = "P", Kind = "planet", Radius = 2.5 } },
            Sprites = new List<string> { "Icon_A" },
            Tags = new List<string> { "Player" }, Skyboxes = new List<string> { "Sky/A" }, SpawnTags = new List<string> { "Player" },
            UniverseMinX = -10, UniverseMinZ = -20, UniverseWidth = 1000, UniverseHeight = 800, Background = "map.png",
        };
        var d = Catalogue.Parse(c.ToJson());
        Assert.Equal(c.ToJson(), d.ToJson());
        Assert.Equal("Venov", d.TrenchSystems[0].Name);
        Assert.Equal(3f, d.TrenchSystems[0].Nodes[0].Z);
        Assert.Equal(2.5, d.Prefabs[0].Radius);
        Assert.Equal(800f, d.UniverseHeight);
        Assert.Equal("FAMILY V", d.TrenchSystems[0].Families[0].Name);
        Assert.Equal(new[] { "POI A" }, d.TrenchSystems[0].Families[0].Pois);
        Assert.True(d.TrenchSystems[0].Families[0].HasSpawn);
        Assert.Equal(new[] { "Icon_A" }, d.Sprites);
    }

    [Fact]
    public void Parse_OlderCatalogueWithoutFamiliesOrSprites_Ok()
    {
        var d = Catalogue.Parse(@"{ ""trenchSystems"": [ { ""name"": ""V"", ""position"": [0, 0] } ] }");
        Assert.Empty(d.TrenchSystems[0].Families);
        Assert.Empty(d.Sprites);
    }
}
