namespace Galaxy.Tests;

/// <summary>The hand-made test region for the in-game check (assets/galaxies/test-reach.json; a development file, not shipped).</summary>
public class TestReachTests
{
    static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "assets", "galaxies"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root (assets/galaxies) not found");
    }

    static GalaxyFile File_() => GalaxyFile.Parse(File.ReadAllText(Path.Combine(Repo(), "assets", "galaxies", "test-reach.json")));

    static Catalogue RealCatalogue() =>
        Catalogue.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "galaxy-designer", "testdata", "catalogue-real.json")));

    [Fact]
    public void TestReach_IsValid()
    {
        var problems = GalaxyCheck.Problems(File_(), RealCatalogue());
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TestReach_ThreeSystemsWithPlainNamesOnTheMapClearOfTheTrench()
    {
        var f = File_();
        var c = RealCatalogue();
        Assert.Equal(3, f.Systems.Count);
        foreach (var s in f.Systems)
        {
            Assert.DoesNotContain("|", s.Name);
            Assert.InRange(s.X, c.UniverseMinX + 10, c.UniverseMinX + c.UniverseWidth - 10);
            Assert.InRange(s.Z, c.UniverseMinZ + 10, c.UniverseMinZ + c.UniverseHeight - 10);
            foreach (var t in c.TrenchSystems)
                Assert.True(Math.Sqrt((s.X - t.X) * (s.X - t.X) + (s.Z - t.Z) * (s.Z - t.Z)) > 30, s.Name + " is close to " + t.Name);
        }
    }

    [Fact]
    public void TestReach_CopiedHandMadeAndMixed()
    {
        var f = File_();
        Assert.All(f.Systems[0].Nodes, n => Assert.NotNull(n.CopyFamily));
        Assert.All(f.Systems[1].Nodes, n => Assert.Null(n.CopyFamily));
        Assert.Contains(f.Systems[2].Nodes, n => n.CopyFamily != null);
        Assert.Contains(f.Systems[2].Nodes, n => n.CopyFamily == null);
    }
}
