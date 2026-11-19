using StbcRevival.Science;
using Xunit;

public class ScanProgressTests
{
    static float TimeToFinish(float a, float b)
    {
        var p = new ScanProgress();
        float t = 0;
        while (!p.Done && t < 1000f) { p.Advance(a, b, 0.1f); t += 0.1f; }
        return t;
    }

    [Fact]
    public void Advance_DoesNothingBelowThresholdOnEither()
    {
        var p = new ScanProgress();
        p.Advance(0.69f, 1f, 10f);
        Assert.Equal(0f, p.Value);
        Assert.False(p.Locked);
        p.Advance(1f, 0.69f, 10f);
        Assert.Equal(0f, p.Value);
    }

    [Fact]
    public void Advance_NeverGoesBackwards()
    {
        var p = new ScanProgress();
        p.Advance(0.9f, 0.9f, 1f);
        float v = p.Value;
        Assert.True(v > 0f);
        p.Advance(0.1f, 0.1f, 1f);
        Assert.Equal(v, p.Value);
    }

    [Fact]
    public void Advance_PerfectFinishesIn30To45Seconds()
    {
        float perfect = TimeToFinish(1f, 1f);
        Assert.InRange(perfect, 30f, 45f);
        float bare = TimeToFinish(0.7f, 0.7f);
        Assert.True(bare >= perfect);
        Assert.True(bare <= 120f);
    }

    [Fact]
    public void Advance_StopsAt100()
    {
        var p = new ScanProgress();
        p.Advance(1f, 1f, 1000f);
        Assert.Equal(100f, p.Value);
        Assert.True(p.Done);
    }
}
