namespace Galaxy.Tests;

/// <summary>The hand-made additions file for the in-game check (assets/galaxies/test-additions.json; a development file, not shipped).</summary>
public class TestAdditionsTests
{
    static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "assets", "galaxies"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root (assets/galaxies) not found");
    }

    static GalaxyFile File_() => GalaxyFile.Parse(File.ReadAllText(Path.Combine(Repo(), "assets", "galaxies", "test-additions.json")));

    static Catalogue RealCatalogue() =>
        Catalogue.Parse(File.ReadAllText(Path.Combine(Repo(), "tests", "Galaxy.Tests", "testdata", "catalogue-real.json")));

    [Fact]
    public void TestAdditions_IsValid()
    {
        var problems = GalaxyCheck.Problems(File_(), RealCatalogue());
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TestAdditions_AdditionsOnly_VenovHandMade_SilvaCopied()
    {
        var f = File_();
        var c = RealCatalogue();
        Assert.Empty(f.Systems);
        Assert.Equal(2, f.Trench.Count);
        Assert.Equal("2824|Venov", GalaxyCheck.FindTrench(c, f.Trench[0].System).Name);
        Assert.Equal("Silva", GalaxyCheck.FindTrench(c, f.Trench[1].System).Name);
        Assert.All(f.Trench[0].Nodes, n => Assert.Null(n.CopyFamily));
        Assert.Contains(f.Trench[0].Nodes, n => n.Parent == null && n.Name == "Venov Outpost");
        var copied = Assert.Single(f.Trench[1].Nodes);
        Assert.NotNull(copied.CopyFamily);
        // The copied family exists in the real catalogue under that system.
        var from = GalaxyCheck.FindTrench(c, copied.CopyFamily.System);
        Assert.Contains(from.Families, fam => fam.Name == copied.CopyFamily.Family);
    }

    [Fact]
    public void TestAdditions_NewFamilyNamesAreNotTheSystemsOwn()
    {
        var f = File_();
        var c = RealCatalogue();
        foreach (var a in f.Trench)
        {
            var sys = GalaxyCheck.FindTrench(c, a.System);
            foreach (var n in a.Nodes)
                if (n.Parent == null) Assert.DoesNotContain(sys.Families, fam => fam.Name == n.Name);
        }
    }
}
