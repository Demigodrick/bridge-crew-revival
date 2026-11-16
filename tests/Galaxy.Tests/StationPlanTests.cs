namespace Galaxy.Tests;

public class StationPlanTests
{
    // Scripted draws, checked against the order the game makes them in; each range draw records its bounds.
    sealed class Draws
    {
        readonly Queue<object> script;
        public readonly List<string> Seen = new();
        public Draws(params object[] s) => script = new Queue<object>(s);
        public int Range(int min, int max)
        {
            Seen.Add($"range({min},{max})");
            return (int)script.Dequeue();
        }
        public float Value()
        {
            Seen.Add("value");
            return (float)script.Dequeue();
        }
        public bool Done => script.Count == 0;
    }

    static StationPlan Make(int parts, Draws d) => StationPlan.Make(parts, bases: 2, bodies: 3, domes: 2, rings: 2, arms: 4, d.Range, d.Value);

    [Fact]
    public void NoParts_NoStation() => Assert.Null(StationPlan.Make(0, 1, 1, 1, 1, 1, (a, b) => 0, () => 0f));

    [Fact]
    public void OnePart_BodyOrDome()
    {
        var d = new Draws(4);
        var plan = Make(1, d);
        Assert.Equal(new[] { "range(0,5)" }, d.Seen); // 3 bodies + 2 domes
        Assert.Equal("domes[1]", plan.First.ToString());
        Assert.Empty(plan.Parts);
    }

    [Fact]
    public void FourParts_TheGamesDrawOrder()
    {
        // first: body+base (5) → base[0]; part 1: body → body[2], value 0.1 → ring[1];
        // part 2: body → body[0], value 0.5, value 0.4 → 5 arms of arm[3]; part 3 (last): dome → domes[0], value 0.9, value 0.7 → nothing.
        var d = new Draws(3, 2, 0.1f, 1, 0, 0.5f, 0.4f, 5, 3, 0, 0.9f, 0.7f);
        var plan = Make(4, d);
        Assert.True(d.Done);
        Assert.Equal(new[]
        {
            "range(0,5)",
            "range(0,3)", "value", "range(0,2)",
            "range(0,3)", "value", "value", "range(3,6)", "range(0,4)",
            "range(0,2)", "value", "value",
        }, d.Seen);
        Assert.Equal("base[0], body[2] +ring rings[1], body[0] +5×arms[3], domes[0]", plan.ToString());
    }

    [Fact]
    public void EmptyList_FailsClearly()
    {
        var e = Assert.Throws<InvalidOperationException>(() => StationPlan.Make(2, 1, 1, 0, 1, 1, (a, b) => 0, () => 0.9f));
        Assert.Contains("domes", e.Message);
    }
}
