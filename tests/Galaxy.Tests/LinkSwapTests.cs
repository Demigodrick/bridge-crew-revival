namespace Galaxy.Tests;

/// <summary>Trench systems with additions: a link points at its system's copy for Patrol/Free Roam, back at the
/// original for every other mission (LinkSwap), tested with plain objects.</summary>
public class LinkSwapTests
{
    sealed class Sys
    {
        public string Name;
        public override string ToString() => Name;
    }

    sealed class Link
    {
        public Sys System;
    }

    static readonly Sys Venov = new Sys { Name = "Venov" }, Silva = new Sys { Name = "Silva" }, Toth = new Sys { Name = "Toth" };
    static readonly Sys VenovCopy = new Sys { Name = "Venov+" }, SilvaCopy = new Sys { Name = "Silva+" };

    static Dictionary<Sys, Sys> Copies() => new Dictionary<Sys, Sys> { { Venov, VenovCopy }, { Silva, SilvaCopy } };

    static List<Link> Links() => new List<Link> { new Link { System = Venov }, new Link { System = Toth }, new Link { System = Silva } };

    static void Apply(List<Link> links, bool on) => LinkSwap.Apply(links, l => l.System, (l, s) => l.System = s, Copies(), on);

    static Sys[] Now(List<Link> links) => links.Select(l => l.System).ToArray();

    [Fact]
    public void TrenchSwap_OnReplaces()
    {
        var links = Links();
        Apply(links, true);
        Assert.Equal(new[] { VenovCopy, Toth, SilvaCopy }, Now(links));
    }

    [Fact]
    public void TrenchSwap_OffRestores()
    {
        var links = Links();
        Apply(links, true);
        Apply(links, false);
        Assert.Equal(new[] { Venov, Toth, Silva }, Now(links));
    }

    [Fact]
    public void TrenchSwap_OnTwice_NoChange()
    {
        var links = Links();
        Apply(links, true);
        Apply(links, true);
        Assert.Equal(new[] { VenovCopy, Toth, SilvaCopy }, Now(links));
    }

    [Fact]
    public void TrenchSwap_OnOffOn_SameAsOn()
    {
        var links = Links();
        Apply(links, true);
        Apply(links, false);
        Apply(links, false);
        Apply(links, true);
        Assert.Equal(new[] { VenovCopy, Toth, SilvaCopy }, Now(links));
        Assert.Equal(3, links.Count);
    }

    [Fact]
    public void TrenchSwap_UnrelatedLinksUntouched()
    {
        var other = new Sys { Name = "Region system" };
        var links = new List<Link> { new Link { System = Toth }, new Link { System = other }, new Link { System = null } };
        var before = links.ToArray();
        Apply(links, true);
        Assert.Equal(new[] { Toth, other, null }, Now(links));
        Apply(links, false);
        Assert.Equal(new[] { Toth, other, null }, Now(links));
        Assert.Equal(before, links.ToArray()); // the same link objects, in the same order
    }

    [Fact]
    public void TrenchSwap_OffWithNoCopies_NoChange()
    {
        var links = Links();
        LinkSwap.Apply(links, l => l.System, (l, s) => l.System = s, null, false);
        Assert.Equal(new[] { Venov, Toth, Silva }, Now(links));
    }
}

public class SystemNumberingTests
{
    sealed class Sys { public int Id = -1; public Sys Original; }
    sealed class Link { public Sys System; }

    static void Number(List<Link> links) =>
        LinkSwap.Number(links, l => l.System, s => s.Original ?? s, (s, i) => s.Id = i);

    [Fact]
    public void Number_GivesSwappedOutOriginalItsCopysId()
    {
        var a = new Sys(); var b = new Sys(); var bOriginal = new Sys { Id = 0 };
        var bCopy = new Sys { Original = bOriginal };
        var links = new List<Link> { new Link { System = a }, new Link { System = b }, new Link { System = bCopy } };
        Number(links);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { a.Id, b.Id, bCopy.Id });
        Assert.Equal(2, bOriginal.Id);
    }

    [Fact]
    public void Number_PlainListAsTheGameDoes()
    {
        var links = new List<Link> { new Link { System = new Sys() }, new Link(), new Link { System = new Sys() } };
        LinkSwap.Number(links, l => l.System, null, (s, i) => s.Id = i);
        Assert.Equal(0, links[0].System.Id);
        Assert.Equal(2, links[2].System.Id);
    }
}
