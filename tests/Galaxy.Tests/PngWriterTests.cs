using System.IO.Compression;

namespace Galaxy.Tests;

public class PngWriterTests
{
    // Independent of the encoder's own table: the bitwise CRC-32 from the PNG specification.
    static uint Crc(byte[] b, int at, int length)
    {
        uint c = 0xFFFFFFFF;
        for (int i = at; i < at + length; i++)
        {
            c ^= b[i];
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    static uint U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    // Chunks in order, each CRC checked; the IDAT data joined.
    static (List<string> types, byte[] ihdr, byte[] idat) Chunks(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        var types = new List<string>();
        byte[] ihdr = null;
        var idat = new MemoryStream();
        int at = 8;
        while (at < png.Length)
        {
            int len = (int)U32(png, at);
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            Assert.Equal(U32(png, at + 8 + len), Crc(png, at + 4, 4 + len));
            if (type == "IHDR") ihdr = png.Skip(at + 8).Take(len).ToArray();
            if (type == "IDAT") idat.Write(png, at + 8, len);
            types.Add(type);
            at += 12 + len;
        }
        Assert.Equal(png.Length, at);
        return (types, ihdr, idat.ToArray());
    }

    static byte[] Pixels(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)(i * 7);
            rgba[i * 4 + 1] = (byte)(i * 13);
            rgba[i * 4 + 2] = (byte)(i * 29);
            rgba[i * 4 + 3] = (byte)(255 - i);
        }
        return rgba;
    }

    [Theory]
    [InlineData(5, 3, true)]
    [InlineData(5, 3, false)]
    [InlineData(200, 120, true)] // more than one 65535-byte stored block
    public void Encode_IsAValidPngWithThePixels(int w, int h, bool alpha)
    {
        var rgba = Pixels(w, h);
        var png = PngWriter.Encode(rgba, w, h, bottomUp: true, alpha: alpha);
        var (types, ihdr, idat) = Chunks(png);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
        Assert.Equal((uint)w, U32(ihdr, 0));
        Assert.Equal((uint)h, U32(ihdr, 4));
        Assert.Equal(8, ihdr[8]);
        Assert.Equal(alpha ? 6 : 2, ihdr[9]);
        Assert.Equal(new byte[] { 0, 0, 0 }, ihdr.Skip(10).ToArray());

        // .NET's own zlib reader checks the stream and its Adler-32.
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();
        int channels = alpha ? 4 : 3;
        Assert.Equal(h * (w * channels + 1), data.Length);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, data[y * (w * channels + 1)]);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < channels; c++)
                    Assert.Equal(rgba[((h - 1 - y) * w + x) * 4 + c], data[y * (w * channels + 1) + 1 + x * channels + c]); // flipped: bottom-up in
        }
    }

    [Fact]
    public void Encode_RefusesTooFewPixels() =>
        Assert.Throws<ArgumentException>(() => PngWriter.Encode(new byte[10], 2, 2, false, true));
}
