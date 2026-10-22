#if HAS_PHOTON_CLIENT
using System.Buffers.Binary;
using ExitGames.Client.Photon;

namespace StbcServer.Tests;

/// <summary>Stand-ins for PUN's custom types (Vector3 'V', Quaternion 'Q'), registered with the real client.</summary>
public static class TestTypes
{
    public sealed record Vec3(float X, float Y, float Z);
    public sealed record Quat(float X, float Y, float Z, float W);

    private static int registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) != 0) return;
        PhotonPeer.RegisterType(typeof(Vec3), (byte)'V', o => Floats(((Vec3)o).X, ((Vec3)o).Y, ((Vec3)o).Z),
            b => new Vec3(F(b, 0), F(b, 1), F(b, 2)));
        PhotonPeer.RegisterType(typeof(Quat), (byte)'Q', o => { var q = (Quat)o; return Floats(q.X, q.Y, q.Z, q.W); },
            b => new Quat(F(b, 0), F(b, 1), F(b, 2), F(b, 3)));
    }

    private static byte[] Floats(params float[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    private static float F(byte[] b, int i) => BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(i * 4));
}
#endif
