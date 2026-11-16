namespace Galaxy.Tests;

public class GalaxyGateTests
{
    static List<KeyValuePair<string, string>> Players(params (string name, string hash)[] ps)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var p in ps) list.Add(new KeyValuePair<string, string>(p.name, p.hash));
        return list;
    }

    [Fact]
    public void Gate_OffWhenMissionDoesNotWantIt()
    {
        var r = GalaxyGate.Decide(false, "Example Reach", "abc", Players(("Sam", "abc")));
        Assert.False(r.On);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Gate_OffWhenHostHasNone()
    {
        var none = GalaxyGate.Decide(true, "Example Reach", null, Players(("Sam", "abc")));
        Assert.False(none.On);
        Assert.Null(none.Reason);

        var empty = GalaxyGate.Decide(true, "Example Reach", "", Players(("Sam", "abc")));
        Assert.False(empty.On);
        Assert.Null(empty.Reason);
    }

    [Fact]
    public void Gate_OnWhenAllMatch()
    {
        var r = GalaxyGate.Decide(true, "Example Reach", "abc", Players(("Sam", "abc"), ("Alex", "abc")));
        Assert.True(r.On);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Gate_OffWhenOnePlayerMissing()
    {
        var r = GalaxyGate.Decide(true, "Example Reach", "abc", Players(("Sam", null)));
        Assert.False(r.On);
        Assert.Equal("Example Reach is off: Sam doesn't have it", r.Reason);
    }

    [Fact]
    public void Gate_OffWhenOnePlayerDiffers()
    {
        var r = GalaxyGate.Decide(true, "Example Reach", "abc", Players(("Sam", "old"), ("Alex", "abc")));
        Assert.False(r.On);
        Assert.Equal("Example Reach is off: Sam doesn't have it", r.Reason);
    }

    [Fact]
    public void Gate_TwoPlayersMissing()
    {
        var r = GalaxyGate.Decide(true, "Example Reach", "abc", Players(("Sam", null), ("Alex", "x")));
        Assert.False(r.On);
        Assert.Equal("Example Reach is off: Sam and Alex don't have it", r.Reason);
    }

    [Fact]
    public void Gate_ThreePlayersMissing()
    {
        var r = GalaxyGate.Decide(true, "Example Reach", "abc", Players(("Sam", null), ("Alex", "x"), ("Kim", null)));
        Assert.False(r.On);
        Assert.Equal("Example Reach is off: Sam, Alex and Kim don't have it", r.Reason);
    }

    [Fact]
    public void IsRegionSystem_Boundary()
    {
        Assert.True(GalaxyGate.IsRegionSystem(14, 14));
        Assert.False(GalaxyGate.IsRegionSystem(13, 14));
    }

    // ---- The map hook's quiet case (the default config) ----------------------------------------------------------

    [Fact]
    public void NothingToDecide_DefaultConfig_Hosting()
        => Assert.True(GalaxyGate.NothingToDecide("", false, true, null, null));

    [Fact]
    public void NothingToDecide_DefaultConfig_JoiningAHostWithoutARegion()
    {
        Assert.True(GalaxyGate.NothingToDecide("", false, false, "", null));
        Assert.True(GalaxyGate.NothingToDecide(null, false, false, null, ""));
        Assert.True(GalaxyGate.NothingToDecide("  ", false, false, "", "off"));
    }

    [Fact]
    public void NothingToDecide_NotWithARegionSetOrBuilt()
    {
        Assert.False(GalaxyGate.NothingToDecide("test-reach", false, true, null, null));
        Assert.False(GalaxyGate.NothingToDecide("", true, true, null, null)); // its links may still be in the Trench's list
    }

    [Fact]
    public void NothingToDecide_NotWhenTheHostHasARegion()
    {
        // A player without the file still decides, so they leave a crew flying the region.
        Assert.False(GalaxyGate.NothingToDecide("", false, false, "abc", null));
        Assert.False(GalaxyGate.NothingToDecide("", false, false, "", "abc"));
        Assert.False(GalaxyGate.NothingToDecide("", false, false, "abc", "off"));
    }
}
