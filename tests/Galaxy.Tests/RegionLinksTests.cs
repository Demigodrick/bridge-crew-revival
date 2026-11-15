namespace Galaxy.Tests;

public class RegionLinksTests
{
    static readonly int[] Trench = { 1, 2, 3 };
    static readonly int[] Region = { 10, 11 };

    [Fact]
    public void RegionLinks_OnAppends()
    {
        var list = new List<int>(Trench);
        RegionLinks.Apply(list, Trench, Region, true);
        Assert.Equal(new[] { 1, 2, 3, 10, 11 }, list);
    }

    [Fact]
    public void RegionLinks_OffRestores()
    {
        var list = new List<int> { 1, 2, 3, 10, 11 };
        RegionLinks.Apply(list, Trench, Region, false);
        Assert.Equal(Trench, list);
    }

    [Fact]
    public void RegionLinks_OnTwiceNoDuplicates()
    {
        var list = new List<int>(Trench);
        RegionLinks.Apply(list, Trench, Region, true);
        RegionLinks.Apply(list, Trench, Region, true);
        Assert.Equal(new[] { 1, 2, 3, 10, 11 }, list);
    }

    [Fact]
    public void RegionLinks_OnOffOn_SameAsOn()
    {
        var list = new List<int>(Trench);
        RegionLinks.Apply(list, Trench, Region, true);
        RegionLinks.Apply(list, Trench, Region, false);
        RegionLinks.Apply(list, Trench, Region, true);
        Assert.Equal(new[] { 1, 2, 3, 10, 11 }, list);
    }

    [Fact]
    public void RegionLinks_WhateverItHeldBefore()
    {
        // Leftovers (a stale region link, a missing Trench entry) never survive.
        var list = new List<int> { 3, 99, 1 };
        RegionLinks.Apply(list, Trench, Region, true);
        Assert.Equal(new[] { 1, 2, 3, 10, 11 }, list);
        RegionLinks.Apply(list, Trench, Region, false);
        Assert.Equal(Trench, list);
    }
}
