namespace Galaxy.Tests;

public class MapIconsTests
{
    static GalaxyNode Fam(string icon = null) => new GalaxyNode { Id = "f", Prefab = GalaxyCheck.FamilyPrefab, MapIcon = icon };
    static GalaxyNode Poi(string icon = null) => new GalaxyNode { Id = "p", Parent = "f", Prefab = GalaxyCheck.PoiPrefab, MapIcon = icon };

    [Fact]
    public void OwnIconWins() => Assert.Equal("a", MapIcons.FallbackFor(Fam("a"), new List<GalaxyNode> { Fam("a"), Poi("b") }));

    [Fact]
    public void FamilyTakesItsFirstPoisIcon()
    {
        var f = Fam();
        Assert.Equal("b", MapIcons.FallbackFor(f, new List<GalaxyNode> { f, Poi(), Poi("b") }));
    }

    [Fact]
    public void FamilyWithNoIconsAndPoiAreAPlanet()
    {
        var f = Fam();
        Assert.Equal(MapIcons.Planet, MapIcons.FallbackFor(f, new List<GalaxyNode> { f, Poi() }));
        Assert.Equal(MapIcons.Planet, MapIcons.FallbackFor(Poi(), new List<GalaxyNode>()));
    }
}
