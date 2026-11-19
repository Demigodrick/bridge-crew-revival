using StbcRevival.Science;
using Xunit;

public class ScanBoardTests
{
    static ScanBoard Board(params string[] ids) { var b = new ScanBoard(); b.SetTargets(ids); return b; }

    [Fact]
    public void Progress_DropsTargetsNotListed()
    {
        var b = Board("a", "b");
        b.TryClaim(1, 0f);
        b.Report(1, "a", 40f, 0.1f); b.Report(1, "b", 20f, 0.1f);
        b.SetTargets(new[] { "b", "c" });
        Assert.Equal(0f, b.Progress("a"));
        Assert.Equal(20f, b.Progress("b"));
        Assert.Equal(0f, b.Progress("c"));
        var r = ScanBoard.FromWire(b.ToWire());
        Assert.Equal(20f, r.Progress("b"), 0.0001f);
        Assert.Equal(0f, r.Progress("a"));
    }

    [Fact]
    public void Claims_ExpireWithoutHeartbeat()
    {
        var b = Board("a");
        Assert.True(b.TryClaim(7, 0f));
        Assert.Equal(7, b.Claimant(2.9f));
        Assert.Equal(0, b.Claimant(3.1f));
        Assert.True(b.TryClaim(8, 3.2f));
        b.Heartbeat(8, 5f);
        Assert.Equal(8, b.Claimant(7.5f));
        b.Heartbeat(9, 7.6f); // not the claimant: no effect
        Assert.Equal(0, b.Claimant(8.1f));
    }

    [Fact]
    public void Claim_RefusedWhileAnotherHoldsIt_ThenReleased()
    {
        var b = Board("a");
        Assert.True(b.TryClaim(1, 0f));
        Assert.False(b.TryClaim(2, 1f));
        Assert.True(b.TryClaim(1, 1f));
        b.Release(2);
        Assert.Equal(1, b.Claimant(1.5f));
        b.Release(1);
        Assert.Equal(0, b.Claimant(1.5f));
        Assert.True(b.TryClaim(2, 1.5f));
    }

    [Fact]
    public void Host_RejectsReportsFromNonClaimant()
    {
        var b = Board("a");
        b.TryClaim(1, 0f);
        Assert.False(b.Report(2, "a", 50f, 1f));
        Assert.False(b.Report(1, "zzz", 50f, 1f));
        Assert.False(b.Report(1, "a", 50f, 4f)); // expired
        Assert.Equal(0f, b.Progress("a"));
        Assert.True(b.Report(1, "a", 50f, 2f));
        Assert.False(b.Report(1, "a", 30f, 2f)); // lower
        Assert.Equal(50f, b.Progress("a"));
    }

    [Fact]
    public void Host_ClampsProgress()
    {
        var b = Board("a");
        b.TryClaim(1, 0f);
        Assert.True(b.Report(1, "a", 250f, 1f));
        Assert.Equal(100f, b.Progress("a"));
        var c = Board("a");
        c.TryClaim(1, 0f);
        Assert.False(c.Report(1, "a", float.NaN, 1f));
        Assert.False(c.Report(1, "a", float.PositiveInfinity, 1f));
        Assert.True(c.Report(1, "a", -5f, 1f));
        Assert.Equal(0f, c.Progress("a"));
    }

    [Fact]
    public void Complete_CountsOncePerTarget()
    {
        var b = Board("a", "b");
        b.TryClaim(1, 0f);
        Assert.False(b.Complete(2, "a", 1f));
        Assert.False(b.Complete(1, "nope", 1f));
        Assert.True(b.Complete(1, "a", 1f));
        Assert.False(b.Complete(1, "a", 1f));
        Assert.Equal(100f, b.Progress("a"));
        b.SetTargets(new[] { "a", "b" });
        Assert.False(b.Complete(1, "a", 2f)); // stays done
        Assert.True(ScanBoard.FromWire(b.ToWire()).IsDone("a"));
        Assert.True(b.Complete(1, "b", 2f));
    }

    [Fact]
    public void SetTargets_SkipsUnusableIds()
    {
        var b = Board("", "a;b", "c,d", new string('x', 65), "ok");
        var r = ScanBoard.FromWire(b.ToWire());
        Assert.Equal(0f, r.Progress("ok"));
        Assert.Equal(1, b.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("junk")]
    [InlineData("SCB1;a,NaN,0")]
    [InlineData("SCB1;a,5")]
    public void Board_FromWire_IgnoresGarbage(string s)
    {
        Assert.Equal(0, ScanBoard.FromWire(s).Count);
    }
}

public class ScienceWireTests
{
    [Fact]
    public void Wire_RoundTripsEveryKind()
    {
        ScienceMsg m;
        Assert.True(ScienceWire.TryRead(ScienceWire.Claim(), out m)); Assert.Equal(ScienceMsgKind.Claim, m.Kind);
        Assert.True(ScienceWire.TryRead(ScienceWire.Release(), out m)); Assert.Equal(ScienceMsgKind.Release, m.Kind);
        Assert.True(ScienceWire.TryRead(ScienceWire.Heartbeat(), out m)); Assert.Equal(ScienceMsgKind.Heartbeat, m.Kind);
        Assert.True(ScienceWire.TryRead(ScienceWire.Report("t1", 42.5f), out m));
        Assert.Equal(ScienceMsgKind.Report, m.Kind); Assert.Equal("t1", m.Id); Assert.Equal(42.5f, m.Progress);
        Assert.True(ScienceWire.TryRead(ScienceWire.Complete("t1"), out m));
        Assert.Equal(ScienceMsgKind.Complete, m.Kind); Assert.Equal("t1", m.Id);
        Assert.True(ScienceWire.TryRead(ScienceWire.Complete(new string('x', 64)), out m));
    }

    [Fact]
    public void Wire_RejectsTruncatedAndOversizedPayloads()
    {
        ScienceMsg m;
        Assert.False(ScienceWire.TryRead(null, out m));
        Assert.False(ScienceWire.TryRead(new byte[0], out m));
        Assert.False(ScienceWire.TryRead(new byte[] { 99 }, out m));
        Assert.False(ScienceWire.TryRead(new byte[] { 1, 0 }, out m)); // trailing byte
        var rep = ScienceWire.Report("abc", 5f);
        var cut = new byte[rep.Length - 1]; System.Array.Copy(rep, cut, cut.Length);
        Assert.False(ScienceWire.TryRead(cut, out m));
        var longer = new byte[rep.Length + 1]; System.Array.Copy(rep, longer, rep.Length);
        Assert.False(ScienceWire.TryRead(longer, out m));
        Assert.Null(ScienceWire.Complete(new string('x', 65)));
        Assert.Null(ScienceWire.Report("", 1f));
        // hand-built oversize id length
        var big = new byte[2 + 65]; big[0] = 5; big[1] = 65;
        for (int i = 0; i < 65; i++) big[2 + i] = (byte)'x';
        Assert.False(ScienceWire.TryRead(big, out m));
        // zero length id
        Assert.False(ScienceWire.TryRead(new byte[] { 5, 0 }, out m));
        // non-finite progress
        var nan = ScienceWire.Report("a", float.NaN);
        Assert.False(ScienceWire.TryRead(nan, out m));
    }
}

public class ScienceWireHardeningTests
{
    [Fact]
    public void Encoders_ReturnNullForLoneSurrogate()
    {
        Assert.Null(ScienceWire.Report("a\ud800b", 1f));
        Assert.Null(ScienceWire.Complete("a\udc00"));
    }
}

public class ScanBoardIdsTests
{
    [Fact]
    public void Ids_InOrderAndSurviveTheWire()
    {
        var b = new ScanBoard();
        b.SetTargets(new[] { "b", "a", "bad;id", "c" });
        Assert.Equal(new[] { "b", "a", "c" }, b.Ids());
        Assert.Equal(new[] { "b", "a", "c" }, ScanBoard.FromWire(b.ToWire()).Ids());
        Assert.Empty(new ScanBoard().Ids());
    }
}
