using System.Numerics;
using System.Security.Cryptography;
using StbcServer.Net;

namespace StbcServer.Protocol;

/// <summary>
/// Server side of Photon's DiffieHellmanCryptoProvider: Oakley group 1 (RFC 2409, 768-bit) with
/// generator 22, 160-bit secret. The AES-256 key is SHA-256 of the shared secret's minimal
/// big-endian bytes; CBC, zero IV, PKCS7.
/// </summary>
public sealed class Encryption : ICryptoSession
{
    private static readonly BigInteger Prime = new(Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF"),
        isUnsigned: true, isBigEndian: true);
    private static readonly BigInteger Generator = 22;

    private Aes? aes;

    public byte[] Establish(byte[] clientPublicKey)
    {
        BigInteger secret;
        do secret = new BigInteger(RandomNumberGenerator.GetBytes(20), isUnsigned: true);
        while (secret.IsZero || secret >= Prime - 1);

        var shared = BigInteger.ModPow(new BigInteger(clientPublicKey, isUnsigned: true, isBigEndian: true), secret, Prime);
        aes = Aes.Create();
        aes.Key = SHA256.HashData(ToBytes(shared));
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return ToBytes(BigInteger.ModPow(Generator, secret, Prime));
    }

    public byte[] Decrypt(byte[] data, int offset, int count) =>
        (aes ?? throw new InvalidOperationException("no key")).DecryptCbc(data.AsSpan(offset, count), aes.IV, PaddingMode.PKCS7);

    // Photon's BigInteger.GetBytes(): minimal big-endian, and a single 0 byte for zero.
    private static byte[] ToBytes(BigInteger v) => v.IsZero ? new byte[1] : v.ToByteArray(isUnsigned: true, isBigEndian: true);
}
