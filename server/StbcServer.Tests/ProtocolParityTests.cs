#if HAS_PHOTON_CLIENT
using StbcServer.Protocol;
using PhotonProtocol = ExitGames.Client.Photon.Protocol;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcServer.Tests;

/// <summary>
/// Our Protocol16 against the game's own Photon3Unity3D serializer: decode what the real client
/// produces and re-encode it to identical bytes; and make sure what we build decodes in the client.
/// </summary>
public class ProtocolParityTests
{
    static ProtocolParityTests() => TestTypes.Register();

    public static IEnumerable<object[]> ClientValues()
    {
        var nested = new PhotonHashtable { { (byte)0, 1001 }, { "name", "Kirk" }, { (byte)4, new object[] { 1, "two", 3.5f, null! } } };
        yield return [42];
        yield return [(byte)7];
        yield return [(short)-3];
        yield return [123456789012L];
        yield return [1.25f];
        yield return [Math.PI];
        yield return [true];
        yield return ["Engage! ünïcödé"];
        yield return [""];
        yield return [new byte[] { 1, 2, 3 }];
        yield return [new[] { 1, 2, 3 }];
        yield return [new[] { "a", "b" }];
        yield return [new object[] { 1, "x", null!, new[] { 5, 6 } }];
        yield return [nested];
        yield return [new PhotonHashtable { { (byte)255, (byte)4 }, { (byte)253, true }, { "C0", 1 }, { "C7", "room" } }];
        yield return [new Dictionary<string, int> { { "a", 1 }, { "b", 2 } }];
        yield return [new Dictionary<object, object> { { 1, "x" }, { "y", 2.0 } }];
        yield return [new TestTypes.Vec3(1, 2, 3)];
        yield return [new[] { new TestTypes.Vec3(1, 2, 3), new TestTypes.Vec3(4, 5, 6) }];
        yield return [new PhotonHashtable { { (byte)0, 5 }, { (byte)1, new object[] { new TestTypes.Vec3(1, 0, 0), new TestTypes.Quat(0, 0, 0, 1) } } }];
        yield return [new float[] { 1, 2 }];
        yield return [new[] { new[] { 1, 2 }, new[] { 3 } }];
        yield return [new byte[][] { [1], [2, 3] }];
        yield return [new[] { new Dictionary<string, int> { { "k", 1 } } }];
    }

    [Theory]
    [MemberData(nameof(ClientValues))]
    public void DecodesAndReencodesClientBytesExactly(object value)
    {
        byte[] bytes = PhotonProtocol.Serialize(value);
        var reader = new Reader(bytes);
        var decoded = Protocol16.ReadTyped(reader);
        Assert.Equal(bytes.Length, reader.Position);
        Assert.Equal(bytes, Protocol16.Encode(decoded));
    }

    [Fact]
    public void ModifiedHashtableStillDecodesInClient()
    {
        var original = new PhotonHashtable { { "C1", 2 }, { (byte)255, (byte)4 }, { (byte)9, new TestTypes.Vec3(7, 8, 9) } };
        var reader = new Reader(PhotonProtocol.Serialize(original));
        var ours = new PHashtable((PHashtable)Protocol16.ReadTyped(reader)!) { ["C1"] = 3 };

        var back = (PhotonHashtable)PhotonProtocol.Deserialize(Protocol16.Encode(ours));
        Assert.Equal(3, back["C1"]);
        Assert.Equal((byte)4, back[(byte)255]);
        Assert.Equal(new TestTypes.Vec3(7, 8, 9), back[(byte)9]);
    }

    [Fact]
    public void ServerBuiltValuesDecodeInClient()
    {
        Assert.Equal(new[] { true, false }, PhotonProtocol.Deserialize(Protocol16.Encode(new[] { true, false })));
        Assert.Equal(new[] { 1, 2, 3 }, PhotonProtocol.Deserialize(Protocol16.Encode(new[] { 1, 2, 3 })));
        Assert.Equal(new[] { "a", "" }, PhotonProtocol.Deserialize(Protocol16.Encode(new[] { "a", "" })));

        var table = new PHashtable { [1] = new PHashtable { [(byte)255] = "Picard" }, ["x"] = null };
        var h = (PhotonHashtable)PhotonProtocol.Deserialize(Protocol16.Encode(table));
        Assert.Equal("Picard", ((PhotonHashtable)h[1])[(byte)255]);
        Assert.True(h.ContainsKey("x"));
    }
}
#endif
