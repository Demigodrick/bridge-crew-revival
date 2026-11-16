namespace Galaxy.Tests;

public class CatalogueNamesTests
{
    [Theory]
    [InlineData("2824|Venov", "Venov")]
    [InlineData("5796|Space Station", "Space Station")]
    [InlineData("Silva", "Silva")]
    [InlineData("12|", "12|")]
    public void Display_TakesTheEnglishPart(string name, string expected) => Assert.Equal(expected, CatalogueNames.Display(name));

    [Theory]
    [InlineData("Ship - SpaceStationProcedural 22", "Space Station Procedural 22")]
    [InlineData("Set2ClassY014", "Set2 Class Y014")]
    [InlineData("DS4219_DarkStar", "DS4219 Dark Star")]
    [InlineData("AnomalyCloudCone_Alt3", "Anomaly Cloud Cone Alt3")]
    [InlineData("Debris - Medium 02", "Debris - Medium 02")]
    [InlineData("Family Group", "Family Group")]
    public void Tidy_MakesPrefabNamesReadable(string name, string expected) => Assert.Equal(expected, CatalogueNames.Tidy(name));

    [Fact]
    public void PngFile_SafeAndUnique()
    {
        Assert.Equal("Ship_-_SpaceStationProcedural_22.png", CatalogueNames.PngFile("Ship - SpaceStationProcedural 22"));
        Assert.Equal("unnamed.png", CatalogueNames.PngFile("../"));
        var used = new HashSet<string>();
        Assert.Equal("Debris_Big.png", CatalogueNames.UniquePngFile("Debris Big", used));
        Assert.Equal("Debris_Big-2.png", CatalogueNames.UniquePngFile("Debris_Big", used));
        Assert.Equal("debris_big-3.png", CatalogueNames.UniquePngFile("debris big", used));
    }

    [Fact]
    public void Catalogue_NewFieldsRoundTrip()
    {
        var c = new Catalogue
        {
            TrenchSystems = new List<CatalogueSystem> { new CatalogueSystem { Name = "2824|Venov", DisplayName = "Venov" } },
            Prefabs = new List<CataloguePrefab>
            {
                new CataloguePrefab { Name = "Ship - SpaceStation 01", Kind = "station", Radius = 1000, Label = "Space Station", Thumb = "Ship_-_SpaceStation_01.png" },
                new CataloguePrefab { Name = "Family Group", Label = "Family Group" },
            },
            Sprites = new List<string> { "FamilyMapBG_2", "spr_sys_map_icon_star" },
            SpriteFiles = new Dictionary<string, string> { { "FamilyMapBG_2", "FamilyMapBG_2.png" }, { "spr_sys_map_icon_star", "spr_sys_map_icon_star.png" } },
            Background = "trench-map.png",
        };
        var back = Catalogue.Parse(c.ToJson());
        Assert.Equal("Venov", back.TrenchSystems[0].DisplayName);
        Assert.Equal("Space Station", back.Prefabs[0].Label);
        Assert.Equal("Ship_-_SpaceStation_01.png", back.Prefabs[0].Thumb);
        Assert.Null(back.Prefabs[1].Thumb);
        Assert.Equal(2, back.SpriteFiles.Count);
        Assert.Equal("spr_sys_map_icon_star.png", back.SpriteFiles["spr_sys_map_icon_star"]);
        Assert.Equal("trench-map.png", back.Background);
        Assert.Equal(c.ToJson(), back.ToJson());
    }

    [Fact]
    public void Catalogue_OlderWithoutTheNewFields_StillParses()
    {
        const string json = @"{
  ""trenchSystems"": [ { ""name"": ""2813|Toth"", ""position"": [ 1, 2 ], ""nodes"": [], ""families"": [] } ],
  ""prefabs"": [ { ""name"": ""Set2ClassY014"", ""kind"": ""planet"", ""radius"": 3 } ],
  ""tags"": [], ""skyboxes"": [], ""spawnTags"": [], ""sprites"": [ ""FamilyMapBG_2"" ], ""background"": null
}";
        var c = Catalogue.Parse(json);
        Assert.Equal("Toth", c.TrenchSystems[0].DisplayName);
        Assert.Equal("Set2 Class Y014", c.Prefabs[0].Label);
        Assert.Null(c.Prefabs[0].Thumb);
        Assert.Empty(c.SpriteFiles);
        Assert.Null(c.Background);
    }

    [Fact]
    public void Catalogue_BadSpriteFile_SaysWhere()
    {
        var e = Assert.Throws<FormatException>(() => Catalogue.Parse(@"{ ""spriteFiles"": { ""a"": 3 } }"));
        Assert.Contains("spriteFiles", e.Message);
    }
}

public class CatalogueFormatTests
{
    [Fact]
    public void FormatOf_ReadsTheFieldFromTheStart()
    {
        Assert.Equal(2, Catalogue.FormatOf(new Catalogue().ToJson()));
        Assert.Equal(1, Catalogue.FormatOf(new Catalogue { Format = 1 }.ToJson()));
        Assert.Null(Catalogue.FormatOf(@"{ ""trenchSystems"": [] }"));
        Assert.Null(Catalogue.FormatOf(""));
        Assert.Null(Catalogue.FormatOf(@"{ ""catalogueFormat"": ""x"" }"));
    }

    [Fact]
    public void NeedsWrite_WhenMissingOrOlder()
    {
        Assert.True(Catalogue.NeedsWrite(null));
        Assert.True(Catalogue.NeedsWrite(@"{ ""trenchSystems"": [] }"));
        Assert.True(Catalogue.NeedsWrite(new Catalogue { Format = 1 }.ToJson()));
        Assert.False(Catalogue.NeedsWrite(new Catalogue().ToJson()));
        Assert.False(Catalogue.NeedsWrite(new Catalogue { Format = Catalogue.CurrentFormat + 1 }.ToJson()));
    }

    [Fact]
    public void Format_RoundTrips_AndMissingIsOne()
    {
        Assert.Equal(1, Catalogue.Parse(new Catalogue { Format = 1 }.ToJson()).Format);
        Assert.Equal(2, Catalogue.Parse(new Catalogue().ToJson()).Format);
        Assert.Equal(1, Catalogue.Parse("{}").Format);
    }
}
