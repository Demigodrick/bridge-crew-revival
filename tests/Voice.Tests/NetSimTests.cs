namespace Voice.Tests;

public class NetSimTests
{
    static int Drain(NetSim sim, double nowMs, List<int> got)
    {
        int n = 0;
        while (sim.TryTake(nowMs, out var p, out int len)) { got.Add(BitConverter.ToInt32(p, 0)); n++; if (len != 4) throw new Exception("len"); }
        return n;
    }

    static void Offer(NetSim sim, int i, double now)
    {
        sim.Offer(BitConverter.GetBytes(i), 4, now);
    }

    [Fact]
    public void OffIsNull()
    {
        Assert.Null(NetSim.Parse(null, 1));
        Assert.Null(NetSim.Parse("", 1));
        Assert.Null(NetSim.Parse("loss=0,jitter=0", 1));
        Assert.Null(NetSim.Parse("bogus=5", 1));
        Assert.NotNull(NetSim.Parse("loss=5, junk=x ,jitter=abc", 1));
    }

    [Fact]
    public void LossRateRoughlyMatches()
    {
        var sim = NetSim.Parse("loss=5", 42);
        var got = new List<int>();
        for (int i = 0; i < 10000; i++) { Offer(sim, i, i * 20.0); Drain(sim, i * 20.0, got); }
        double dropped = 100.0 * (10000 - got.Count) / 10000;
        Assert.InRange(dropped, 4.0, 6.0);
    }

    [Fact]
    public void BurstDropsRuns()
    {
        var sim = NetSim.Parse("loss=5,burst=3", 7);
        var got = new List<int>();
        for (int i = 0; i < 10000; i++) { Offer(sim, i, i * 20.0); Drain(sim, i * 20.0, got); }
        var seen = new HashSet<int>(got);
        int run = 0, runs = 0, shortRuns = 0;
        for (int i = 0; i <= 10000; i++)
        {
            if (i < 10000 && !seen.Contains(i)) run++;
            else if (run > 0) { runs++; if (run < 4) shortRuns++; run = 0; }
        }
        Assert.True(runs > 0);
        // Every drop starts a run of at least 1+3 (back-to-back runs merge into longer ones; only the tail may be cut).
        Assert.True(shortRuns <= 1, "short runs: " + shortRuns);
    }

    [Fact]
    public void JitterDelaysWithinBound()
    {
        var sim = NetSim.Parse("jitter=50", 3);
        bool anyDelayed = false;
        for (int i = 0; i < 200; i++)
        {
            Offer(sim, i, 0);
            // find the release time by stepping 1 ms
            double t = 0;
            while (!sim.TryTake(t, out _, out _)) { t += 1; Assert.True(t <= 50, "delay beyond bound"); }
            if (t > 0) anyDelayed = true;
        }
        Assert.True(anyDelayed);
    }

    [Fact]
    public void ReorderSwaps()
    {
        var sim = NetSim.Parse("reorder=50", 5);
        var got = new List<int>();
        // Packets at the real 20 ms spacing: one held back a slot plus 1 ms lands behind the next one.
        for (int i = 0; i < 200; i++) { Offer(sim, i, i * 20.0); Drain(sim, i * 20.0, got); }
        Drain(sim, 1e9, got);
        Assert.Equal(200, got.Count);
        Assert.Equal(200, new HashSet<int>(got).Count);
        int inversions = 0;
        for (int i = 1; i < got.Count; i++) if (got[i] < got[i - 1]) inversions++;
        Assert.True(inversions > 10, "inversions: " + inversions);
    }

    [Fact]
    public void CopiesAndOrdersByReleaseTime()
    {
        var sim = NetSim.Parse("jitter=1", 1);
        var buf = new byte[4];
        buf[0] = 9; sim.Offer(buf, 4, 0);
        buf[0] = 8;
        Assert.True(sim.TryTake(1e6, out var p, out _));
        Assert.Equal(9, p[0]);
    }
}
