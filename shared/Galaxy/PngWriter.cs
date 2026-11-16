using System;
using System.IO;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// A minimal PNG encoder that needs nothing but managed code, for encoding off Unity's main thread (Texture2D's
    /// EncodeToPNG is main-thread only, and the game's Mono has no working System.IO.Compression): 8-bit RGB or RGBA,
    /// filter 0 on every row, zlib with uncompressed ("stored") deflate blocks. Big, but quick and correct.
    /// </summary>
    public static class PngWriter
    {
        private static readonly uint[] CrcTable = MakeCrcTable();

        /// <summary>
        /// rgba: width × height × 4 bytes. bottomUp: rows from the bottom (as Unity's GetPixels32 gives them).
        /// alpha false writes RGB, dropping it.
        /// </summary>
        public static byte[] Encode(byte[] rgba, int width, int height, bool bottomUp, bool alpha)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("an empty image");
            if (rgba == null || rgba.Length < (long)width * height * 4) throw new ArgumentException("too few pixels");
            int channels = alpha ? 4 : 3;
            int stride = width * channels + 1;
            var raw = new byte[(long)stride * height];
            for (int y = 0; y < height; y++)
            {
                int src = (bottomUp ? height - 1 - y : y) * width * 4;
                int dst = y * stride;
                raw[dst++] = 0; // filter: none
                for (int x = 0; x < width; x++, src += 4)
                {
                    raw[dst++] = rgba[src];
                    raw[dst++] = rgba[src + 1];
                    raw[dst++] = rgba[src + 2];
                    if (alpha) raw[dst++] = rgba[src + 3];
                }
            }

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                BigEndian(ihdr, 0, (uint)width);
                BigEndian(ihdr, 4, (uint)height);
                ihdr[8] = 8;                          // bit depth
                ihdr[9] = (byte)(alpha ? 6 : 2);      // colour type: RGBA or RGB
                Chunk(png, "IHDR", ihdr, ihdr.Length);
                var idat = Zlib(raw);
                Chunk(png, "IDAT", idat, idat.Length);
                Chunk(png, "IEND", new byte[0], 0);
                return png.ToArray();
            }
        }

        // zlib: header (deflate, no preset dictionary, check bits right), stored blocks of up to 65535 bytes, Adler-32.
        private static byte[] Zlib(byte[] data)
        {
            int blocks = Math.Max(1, (data.Length + 65534) / 65535);
            var z = new byte[2 + data.Length + blocks * 5 + 4];
            int o = 0;
            z[o++] = 0x78;
            z[o++] = 0x01;
            int at = 0;
            do
            {
                int len = Math.Min(65535, data.Length - at);
                bool last = at + len >= data.Length;
                z[o++] = (byte)(last ? 1 : 0); // BFINAL, BTYPE 00 (stored)
                z[o++] = (byte)len;
                z[o++] = (byte)(len >> 8);
                z[o++] = (byte)~len;
                z[o++] = (byte)(~len >> 8);
                Buffer.BlockCopy(data, at, z, o, len);
                o += len;
                at += len;
            }
            while (at < data.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            BigEndian(z, o, (b << 16) | a);
            o += 4;
            if (o == z.Length) return z;
            var exact = new byte[o];
            Buffer.BlockCopy(z, 0, exact, 0, o);
            return exact;
        }

        private static void Chunk(Stream s, string type, byte[] data, int length)
        {
            var head = new byte[8];
            BigEndian(head, 0, (uint)length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            s.Write(data, 0, length);
            uint crc = 0xFFFFFFFF;
            for (int i = 4; i < 8; i++) crc = CrcTable[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            var tail = new byte[4];
            BigEndian(tail, 0, crc ^ 0xFFFFFFFF);
            s.Write(tail, 0, 4);
        }

        private static void BigEndian(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
