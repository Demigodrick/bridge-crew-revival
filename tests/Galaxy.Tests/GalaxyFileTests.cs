namespace Galaxy.Tests;

public class GalaxyFileTests
{
    public const string Sample = @"{
  ""format"": 1,
  ""name"": ""Example Reach"",
  ""page"": { ""label"": ""Example Reach"", ""background"": ""trench-darkened"" },
  ""systems"": [
    {
      ""id"": ""kessar"", ""name"": ""Kessar"", ""position"": [120.5, -40.25],
      ""template"": ""Venov"", ""skybox"": ""Skyboxes/Nebula2"", ""systemTags"": [""Hostile"", ""Dark""],
      ""nodes"": [
        { ""id"": ""kessar-star"", ""parent"": null, ""name"": ""Kessar"", ""prefab"": ""Star_Yellow"",
          ""position"": [0, 0, 0], ""unit"": ""AstronomicalUnits"", ""rotation"": [0, 0, 0],
          ""tags"": [""Star""], ""landingRadius"": 5000 },
        { ""id"": ""kessar-2"", ""parent"": ""kessar-star"", ""name"": ""Kessar II"", ""prefab"": ""Planet_Rock"",
          ""position"": [1.5, 0.1, -2.25], ""unit"": ""Skybox"", ""rotation"": [10, 20.5, 30],
          ""tags"": [""Planet"", ""Player""], ""landingRadius"": 1000 }
      ]
    },
    { ""id"": ""dorn"", ""name"": ""Dorn"", ""position"": [300, 10], ""template"": ""Venov"", ""nodes"": [] }
  ]
}";

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var a = GalaxyFile.Parse(Sample);
        var b = GalaxyFile.Parse(a.ToJson());
        Assert.Equal(1, b.Format);
        Assert.Equal("Example Reach", b.Name);
        Assert.Equal("Example Reach", b.Page.Label);
        Assert.Equal("trench-darkened", b.Page.Background);
        Assert.Equal(2, b.Systems.Count);
        var s = b.Systems[0];
        Assert.Equal("kessar", s.Id);
        Assert.Equal("Kessar", s.Name);
        Assert.Equal(120.5f, s.X);
        Assert.Equal(-40.25f, s.Z);
        Assert.Equal("Venov", s.Template);
        Assert.Equal("Skyboxes/Nebula2", s.Skybox);
        Assert.Equal(new[] { "Hostile", "Dark" }, s.SystemTags);
        Assert.Equal(2, s.Nodes.Count);
        var n = s.Nodes[1];
        Assert.Equal("kessar-2", n.Id);
        Assert.Equal("kessar-star", n.Parent);
        Assert.Equal("Kessar II", n.Name);
        Assert.Equal("Planet_Rock", n.Prefab);
        Assert.Equal(1.5f, n.X); Assert.Equal(0.1f, n.Y); Assert.Equal(-2.25f, n.Z);
        Assert.Equal("Skybox", n.Unit);
        Assert.Equal(10f, n.RotX); Assert.Equal(20.5f, n.RotY); Assert.Equal(30f, n.RotZ);
        Assert.Equal(new[] { "Planet", "Player" }, n.Tags);
        Assert.Equal(5000f, s.Nodes[0].LandingRadius);
        Assert.Null(s.Nodes[0].Parent);
        Assert.Null(b.Systems[1].Skybox);
        // And the text itself is stable once written.
        Assert.Equal(a.ToJson(), b.ToJson());
    }

    [Fact]
    public void Parse_RejectsDuplicateSystemIds()
    {
        var json = @"{""format"":1,""name"":""x"",""page"":{""label"":""x"",""background"":""trench-darkened""},""systems"":[
          {""id"":""twin"",""name"":""A"",""position"":[0,0],""template"":""T"",""nodes"":[]},
          {""id"":""twin"",""name"":""B"",""position"":[1,1],""template"":""T"",""nodes"":[]}]}";
        var e = Assert.Throws<FormatException>(() => GalaxyFile.Parse(json));
        Assert.Contains("twin", e.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicateNodeIds()
    {
        var json = @"{""format"":1,""name"":""x"",""page"":{""label"":""x"",""background"":""trench-darkened""},""systems"":[
          {""id"":""s"",""name"":""A"",""position"":[0,0],""template"":""T"",""nodes"":[
            {""id"":""dup"",""name"":""a"",""prefab"":""P"",""position"":[0,0,0]},
            {""id"":""dup"",""name"":""b"",""prefab"":""P"",""position"":[0,0,0]}]}]}";
        var e = Assert.Throws<FormatException>(() => GalaxyFile.Parse(json));
        Assert.Contains("dup", e.Message);
    }

    [Fact]
    public void Parse_UnknownUnit_Throws()
    {
        var json = @"{""format"":1,""name"":""x"",""page"":{""label"":""x"",""background"":""trench-darkened""},""systems"":[
          {""id"":""s"",""name"":""A"",""position"":[0,0],""template"":""T"",""nodes"":[
            {""id"":""odd-node"",""name"":""a"",""prefab"":""P"",""position"":[0,0,0],""unit"":""Parsecs""}]}]}";
        var e = Assert.Throws<FormatException>(() => GalaxyFile.Parse(json));
        Assert.Contains("odd-node", e.Message);
        Assert.Contains("Parsecs", e.Message);
    }

    [Fact]
    public void Parse_MissingOptionalFields_Defaults()
    {
        var json = @"{""format"":1,""name"":""x"",""page"":{""label"":""x"",""background"":""trench-darkened""},""systems"":[
          {""id"":""s"",""name"":""A"",""position"":[3,4],""template"":""T"",""nodes"":[
            {""id"":""n"",""name"":""a"",""prefab"":""P"",""position"":[1,2,3]}]}]}";
        var f = GalaxyFile.Parse(json);
        var s = f.Systems[0];
        Assert.Null(s.Skybox);
        Assert.Null(s.SystemTags);
        var n = s.Nodes[0];
        Assert.Null(n.Parent);
        Assert.Equal(1000f, n.LandingRadius);
        Assert.Equal(0f, n.RotX); Assert.Equal(0f, n.RotY); Assert.Equal(0f, n.RotZ);
        Assert.Equal("Kilometers", n.Unit);
        Assert.NotNull(n.Tags);
        Assert.Empty(n.Tags);
    }

    [Theory]
    [InlineData(@"{""format"":1,""name"":""x"",""systems"":[],}")]
    [InlineData(@"{""format"":1,""name"":""x"",""systems"":[1,]}")]
    [InlineData("// a comment\n{\"format\":1}")]
    [InlineData(@"{""format"":1, /* c */ ""name"":""x""}")]
    [InlineData("")]
    public void Parse_TrailingCommaOrComment_ThrowsFormatException(string json)
    {
        Assert.Throws<FormatException>(() => GalaxyFile.Parse(json));
    }

    [Fact]
    public void Parse_WrongTypedField_ThrowsFormatExceptionNamingTheSystem()
    {
        var json = @"{""format"":1,""name"":""x"",""page"":{""label"":""x"",""background"":""trench-darkened""},""systems"":[
          {""id"":""bad-sys"",""name"":""A"",""position"":""nowhere"",""template"":""T"",""nodes"":[]}]}";
        var e = Assert.Throws<FormatException>(() => GalaxyFile.Parse(json));
        Assert.Contains("bad-sys", e.Message);
    }

    [Fact]
    public void Hash_IgnoresLineEndings()
    {
        Assert.Equal(GalaxyFile.Hash("a\nb"), GalaxyFile.Hash("a\r\nb"));
        Assert.Matches("^[0-9a-f]{64}$", GalaxyFile.Hash("a"));
    }

    [Fact]
    public void Hash_DiffersOnContent()
    {
        Assert.NotEqual(GalaxyFile.Hash("a"), GalaxyFile.Hash("b"));
    }

    [Fact]
    public void MiniJson_WriteThenParse_RoundTrips()
    {
        var input = new Dictionary<string, object>
        {
            ["name"] = "He said \"hi\" \\ \n tab\t é ✓ \u0001",
            ["pi"] = Math.PI,
            ["small"] = 1e-7,
            ["n"] = 3.0,
            ["yes"] = true,
            ["nothing"] = null,
            ["list"] = new List<object> { 1.0, "two", new List<object>(), new Dictionary<string, object>() },
            ["nested"] = new Dictionary<string, object> { ["z"] = 1.0, ["a"] = new List<object> { false } },
        };
        var text = MiniJson.Write(input);
        var back = (Dictionary<string, object>)MiniJson.Parse(text);
        Assert.Equal(input["name"], back["name"]);
        Assert.Equal(Math.PI, (double)back["pi"]);
        Assert.Equal(1e-7, (double)back["small"]);
        Assert.Equal(true, back["yes"]);
        Assert.Null(back["nothing"]);
        Assert.Equal(4, ((List<object>)back["list"]).Count);
        var nested = (Dictionary<string, object>)back["nested"];
        Assert.Equal(new[] { "z", "a" }, nested.Keys.ToArray());
        Assert.Equal(text, MiniJson.Write(back));
    }
}
