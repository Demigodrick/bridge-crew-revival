using StbcServer.Game;

namespace StbcServer.Tests;

public class SqlFilterTests
{
    // Room properties as Bridge Crew sets them (OOLKIMEFIPC.DHIBADKNCEB in the game).
    private static readonly Dictionary<string, object?> Room = new()
    {
        ["C0"] = 1, ["C1"] = 2, ["C2"] = 4, ["C3"] = 3, ["C4"] = 0, ["C5"] = 1,
        ["C6"] = 1, ["C7"] = "room-abc", ["C8"] = 0, ["C9"] = 1,
    };

    private static bool Eval(string sql) => SqlFilter.Parse(sql).Matches(k => Room.GetValueOrDefault(k));

    [Fact]
    public void GameQuickJoinFilterMatches() =>
        Assert.True(Eval("C0 = 1 AND C3 = 3 AND C1 < 4 AND C4 = 0 AND C6 = 1 AND C8 < 5 AND C9 = 1"));

    [Fact]
    public void GameQuickJoinFilterRejectsFullOrDifferentMode()
    {
        Assert.False(Eval("C0 = 1 AND C3 = 3 AND C1 < 2 AND C4 = 0"));
        Assert.False(Eval("C0 = 1 AND C3 = 9 AND C1 < 4"));
    }

    [Fact]
    public void BannedRoomListClause()
    {
        Assert.False(Eval("C0 = 1 AND C7 NOT IN ('room-abc','other')"));
        Assert.True(Eval("C0 = 1 AND C7 NOT IN ('x','y')"));
        Assert.True(Eval("C7 IN ('x', 'room-abc')"));
    }

    [Theory]
    [InlineData("C1 <= 2", true)]
    [InlineData("C1 >= 3", false)]
    [InlineData("C1 <> 2", false)]
    [InlineData("C1 != 3", true)]
    [InlineData("C1 == 2", true)]
    [InlineData("(C1 = 5 OR C2 = 4) AND NOT C4 = 1", true)]
    [InlineData("C99 = 1", false)]
    [InlineData("NOT C99 = 1", false)]
    [InlineData("C99 IS NULL", true)]
    [InlineData("C7 = 'room-abc'", true)]
    [InlineData("c0 = 1 and c1 < 4", false)] // column names are case sensitive like property keys
    public void Expressions(string sql, bool expected) => Assert.Equal(expected, Eval(sql));

    [Fact]
    public void KeywordsAreCaseInsensitive() => Assert.True(Eval("C0 = 1 and C1 < 4 or C2 = 0"));

    [Fact]
    public void InvalidFilterThrows() => Assert.Throws<FormatException>(() => SqlFilter.Parse("C0 = = 1"));
}
