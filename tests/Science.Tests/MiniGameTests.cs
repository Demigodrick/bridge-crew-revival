using StbcRevival.Science;
using Xunit;

public class MiniGameTests
{
    static readonly MiniGameKind[] Kinds = { MiniGameKind.Signal, MiniGameKind.Sweep, MiniGameKind.Tuning };

    static float Wrap(float x) { x -= (float)System.Math.Floor(x); return x; }

    // Sweep rings nudge each other, so settle by repeating.
    static void SetAll(MiniGame g, System.Func<int, float> value)
    {
        for (int round = 0; round < 12; round++)
            for (int i = 0; i < g.ControlCount; i++) g.SetControl(i, value(i));
    }

    static void Perfect(MiniGame g) { SetAll(g, i => g.Target(i)); }
    static void Opposite(MiniGame g)
    {
        if (g.Kind == MiniGameKind.Sweep) SetAll(g, i => Wrap(g.Target(i) + 0.5f));
        else SetAll(g, i => 1f - g.Target(i));
    }

    [Fact]
    public void Pick_TwoDistinctKindsDeterministic()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int s = 0; s < 1000; s++)
        {
            var a = MiniGamePick.For(s);
            var b = MiniGamePick.For(s);
            Assert.Equal(2, a.Length);
            Assert.NotEqual(a[0], a[1]);
            Assert.Equal(a, b);
            var sorted = (MiniGameKind[])a.Clone();
            System.Array.Sort(sorted);
            seen.Add(sorted[0] + "/" + sorted[1]);
        }
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void SeedOf_IsStable()
    {
        Assert.Equal(440920331, MiniGamePick.SeedOf("abc"));
        Assert.NotEqual(MiniGamePick.SeedOf("abc"), MiniGamePick.SeedOf("abd"));
    }

    [Theory]
    [InlineData(MiniGameKind.Signal)]
    [InlineData(MiniGameKind.Sweep)]
    [InlineData(MiniGameKind.Tuning)]
    public void Match_IsOneOnTarget_ZeroFarOff(MiniGameKind k)
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var g = MiniGame.Create(k, seed);
            Assert.Equal(3, g.ControlCount);
            Perfect(g);
            Assert.True(g.Match > 0.95f, k + " seed " + seed + " match " + g.Match);
            Opposite(g);
            Assert.True(g.Match < 0.1f, k + " seed " + seed + " match " + g.Match);
        }
    }

    [Fact]
    public void Sweep_TurningOneRingNudgesNeighbour()
    {
        var g = MiniGame.Create(MiniGameKind.Sweep, 1);
        g.SetControl(1, 0.5f);
        g.SetControl(0, 0.2f);
        float before = g.GetControl(1);
        g.SetControl(0, 0.5f);
        Assert.Equal(0.1, (double)Wrap(g.GetControl(1) - before), 3);
    }

    [Theory]
    [InlineData(MiniGameKind.Signal)]
    [InlineData(MiniGameKind.Sweep)]
    [InlineData(MiniGameKind.Tuning)]
    public void Drift_IsSlow(MiniGameKind k)
    {
        var g = MiniGame.Create(k, 7);
        g.Tick(0f);
        Perfect(g);
        for (float t = 0f; t <= 1f; t += 0.1f) g.Tick(t);
        Assert.True(g.Match > 0.85f, "after 1 s: " + g.Match);

        g.Tick(0f);
        float min = 1f;
        for (float t = 0f; t <= 60f; t += 0.1f) { g.Tick(t); if (g.Match < min) min = g.Match; }
        Assert.True(min < 0.7f, "min over 60 s: " + min);
    }
}
