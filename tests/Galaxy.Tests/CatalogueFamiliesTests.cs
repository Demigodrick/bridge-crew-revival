namespace Galaxy.Tests;

public class CatalogueFamiliesTests
{
    static GalaxyNode N(string id, string parent, string prefab, string name = null, params string[] tags) => new GalaxyNode
    {
        Id = id, Parent = parent, Name = name ?? id, Prefab = prefab, Tags = new List<string>(tags),
    };

    static readonly string[] Spawn = { "Player" };

    static IList<string> NoTags(string prefab) => prefab == "Station_X" ? new List<string> { "SpaceStation" } : new List<string>();

    static List<CatalogueFamily> From(params GalaxyNode[] nodes) => CatalogueFamilies.From(nodes, NoTags, Spawn);

    [Fact]
    public void UntaggedFamilyGroup_UnderRoot_Counts()
    {
        var f = From(N("r", null, "Region", "r", "ROOT"), N("f", "r", "Family Group", "Fam"));
        Assert.Single(f);
        Assert.Equal("Fam", f[0].Name);
    }

    [Fact]
    public void FamilyTaggedNode_Counts()
    {
        var f = From(N("r", null, "Region", "r", "ROOT"), N("f", "r", "Region", "Fam", "FAMILY"));
        Assert.Single(f);
    }

    [Fact]
    public void NonFamilyRootChildren_Ignored()
    {
        var f = From(N("r", null, "Region", "r", "ROOT"), N("x", "r", "Planet_Rock", "Planet"));
        Assert.Empty(f);
    }

    [Fact]
    public void Pois_AreDirectChildrenWithPoiPrefabOnly()
    {
        var f = From(N("r", null, "Region", "r", "ROOT"), N("f", "r", "Family Group", "Fam"),
            N("p1", "f", "Point of Interest", "P1"), N("m", "f", "Region", "M"),
            N("p2", "m", "Point of Interest", "Deep"), N("q", "f", "Planet_Rock", "Q"));
        Assert.Equal(new[] { "P1" }, f[0].Pois);
    }

    [Fact]
    public void HasSpawn_FromDescendantsOwnTag()
    {
        var f = From(N("r", null, "Region", "r", "ROOT"), N("f", "r", "Family Group", "Fam"),
            N("a", "f", "Region"), N("b", "a", "Star", "B", "Player"));
        Assert.True(f[0].HasSpawn);
        Assert.False(f[0].HasStation);
    }

    [Fact]
    public void HasStation_FromDescendantsPrefabTagsOnly()
    {
        var f = From(N("r", null, "Region", "r", "ROOT"), N("f", "r", "Family Group", "Fam"),
            N("a", "f", "Region"), N("s", "a", "Station_X"));
        Assert.True(f[0].HasStation);
        Assert.False(f[0].HasSpawn);
    }
}
