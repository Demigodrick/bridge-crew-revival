using System;
using StbcRevival.Science;
using Xunit;

public class SciencePlacementFileTests
{
    const string Valid = @"{
  ""format"": 1,
  ""added"": [ { ""id"": ""1"", ""scene"": ""Bridge_scene_1701D"", ""level"": 7, ""path"": ""/Bridge/Console"", ""pivot"": [1, 0.5, 2], ""move"": [0, 0, 0], ""turn"": 90, ""scale"": 1 } ],
  ""screen"": { ""pivot"": [1.5, 1.2, 2.25], ""turn"": 90, ""tilt"": 20, ""size"": [0.6, 0.4] },
  ""stand"": [0.8, 0, 2.25]
}";

    [Fact]
    public void Parse_ReadsScreenAndStand()
    {
        var f = SciencePlacementFile.Parse(Valid);
        Assert.Equal(new[] { 1.5f, 1.2f, 2.25f }, f.ScreenPivot);
        Assert.Equal(90f, f.ScreenTurn);
        Assert.Equal(20f, f.ScreenTilt);
        Assert.Equal(0.6f, f.ScreenWidth);
        Assert.Equal(0.4f, f.ScreenHeight);
        Assert.Equal(new[] { 0.8f, 0f, 2.25f }, f.Stand);
        Assert.Equal(1, f.AddedCount);
    }

    [Fact]
    public void Parse_TurnAndTiltDefaultToZero_AddedOptional()
    {
        var f = SciencePlacementFile.Parse(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5] }, ""stand"": [0, 0, -1] }");
        Assert.Equal(0f, f.ScreenTurn);
        Assert.Equal(0f, f.ScreenTilt);
        Assert.Equal(0, f.AddedCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData(@"[1, 2]")]
    [InlineData(@"{ ""format"": 2, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5] }, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1], ""size"": [1, 0.5] }, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [0, 0.5] }, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [9, 0.5] }, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5], ""tilt"": ""up"" }, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5] } }")]
    [InlineData(@"{ ""format"": 1, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5] }, ""stand"": [0, ""a"", 0] }")]
    [InlineData(@"{ ""format"": 1, ""added"": [ 3 ], ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5] }, ""stand"": [0, 0, 0] }")]
    [InlineData(@"{ ""format"": 1, ""added"": {}, ""screen"": { ""pivot"": [0, 1, 0], ""size"": [1, 0.5] }, ""stand"": [0, 0, 0] }")]
    public void Parse_RejectsBrokenFiles(string text)
    {
        Assert.Throws<FormatException>(() => SciencePlacementFile.Parse(text));
    }
}
