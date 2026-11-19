using System;
using System.Collections.Generic;
using System.IO;

namespace StbcRevival
{
    /// <summary>
    /// Gzip in managed code. The game's Mono has no MonoPosixHelper, which System.IO.Compression's GZipStream and
    /// DeflateStream need, so they throw DllNotFoundException in game. Pack writes a gzip that any gzip reader
    /// takes (the deflate data in stored blocks: no compression, for small data like a bridge layout); Unpack reads
    /// any gzip (stored, fixed and dynamic Huffman blocks), so it also takes what GZipStream wrote.
    /// </summary>
    internal static class Gzip
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        internal static byte[] Pack(byte[] data)
        {
            var o = new MemoryStream(data.Length + 32);
            o.Write(new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff }, 0, 10); // header: deflate, no flags, unknown OS
            int at = 0;
            do
            {
                int n = Math.Min(65535, data.Length - at);
                bool last = at + n == data.Length;
                o.WriteByte((byte)(last ? 1 : 0)); // BFINAL, BTYPE 00 (stored), padded to the byte
                o.WriteByte((byte)n); o.WriteByte((byte)(n >> 8));
                o.WriteByte((byte)~n); o.WriteByte((byte)(~n >> 8));
                o.Write(data, at, n);
                at += n;
            } while (at < data.Length);
            WriteUInt(o, Crc(data));
            WriteUInt(o, (uint)data.Length);
            return o.ToArray();
        }

        /// <summary>The data in a gzip; InvalidDataException if it isn't one, or would unpack past maxBytes.</summary>
        internal static byte[] Unpack(byte[] gz, int maxBytes)
        {
            if (gz.Length < 18 || gz[0] != 0x1f || gz[1] != 0x8b || gz[2] != 8) throw new InvalidDataException("not a gzip");
            int flags = gz[3], pos = 10;
            if ((flags & 4) != 0) { Need(gz, pos + 2); pos += 2 + (gz[pos] | gz[pos + 1] << 8); } // FEXTRA
            if ((flags & 8) != 0) pos = SkipZero(gz, pos); // FNAME
            if ((flags & 16) != 0) pos = SkipZero(gz, pos); // FCOMMENT
            if ((flags & 2) != 0) pos += 2; // FHCRC
            Need(gz, pos);
            var inflater = new Inflater(gz, pos, maxBytes, 0);
            var data = inflater.Run();
            int end = inflater.BytePos;
            Need(gz, end + 8);
            uint crc = ReadUInt(gz, end), size = ReadUInt(gz, end + 4);
            if (crc != Crc(data) || size != (uint)data.Length) throw new InvalidDataException("gzip check failed");
            return data;
        }

        /// <summary>
        /// Raw deflate data (a zip entry's) from src[pos], unpacking to exactly size bytes; InvalidDataException if
        /// it's bad or unpacks to any other size.
        /// </summary>
        internal static byte[] Inflate(byte[] src, int pos, int size)
        {
            var data = new Inflater(src, pos, size, size).Run();
            if (data.Length != size) throw new InvalidDataException("deflate data unpacks to the wrong size");
            return data;
        }

        private sealed class Inflater
        {
            private readonly byte[] src;
            private readonly int maxBytes;
            private int pos, bitBuf, bitCount;
            private byte[] outp;
            private int count;

            private static readonly int[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            private static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            private static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
            private static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
            private static readonly int[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

            // size: the expected output, when known (a zip entry), so the buffer is the right size from the start.
            internal Inflater(byte[] src, int pos, int maxBytes, int size)
            {
                this.src = src; this.pos = pos; this.maxBytes = maxBytes;
                outp = new byte[size > 0 ? size : Math.Min(maxBytes, Math.Max(1024, src.Length * 4))];
            }

            /// <summary>The byte after the deflate data (the gzip trailer's start).</summary>
            internal int BytePos => pos;

            internal byte[] Run()
            {
                bool last;
                do
                {
                    last = Bits(1) == 1;
                    int type = Bits(2);
                    if (type == 0) Stored();
                    else if (type == 1) Codes(Fixed(true), Fixed(false));
                    else if (type == 2) Dynamic();
                    else throw new InvalidDataException("bad deflate block");
                } while (!last);
                bitBuf = 0; bitCount = 0; // the rest of the last byte is padding
                if (count == outp.Length) return outp;
                var result = new byte[count];
                Buffer.BlockCopy(outp, 0, result, 0, count);
                return result;
            }

            private int Bits(int n)
            {
                while (bitCount < n)
                {
                    if (pos >= src.Length) throw new InvalidDataException("gzip cut short");
                    bitBuf |= src[pos++] << bitCount;
                    bitCount += 8;
                }
                int v = bitBuf & ((1 << n) - 1);
                bitBuf >>= n; bitCount -= n;
                return v;
            }

            private void Put(byte b)
            {
                if (count == outp.Length)
                {
                    if (count >= maxBytes) throw new InvalidDataException("unpacks too big");
                    var bigger = new byte[(int)Math.Min(maxBytes, Math.Max(1024L, count * 2L))];
                    Buffer.BlockCopy(outp, 0, bigger, 0, count);
                    outp = bigger;
                }
                outp[count++] = b;
            }

            private void Stored()
            {
                bitBuf = 0; bitCount = 0; // to the byte
                Need(src, pos + 4);
                int len = src[pos] | src[pos + 1] << 8, nlen = src[pos + 2] | src[pos + 3] << 8;
                pos += 4;
                if ((len ^ 0xffff) != nlen) throw new InvalidDataException("bad stored block");
                Need(src, pos + len);
                for (int i = 0; i < len; i++) Put(src[pos + i]);
                pos += len;
            }

            // A canonical Huffman code: how many codes of each length, and the symbols in code order.
            private sealed class Huffman
            {
                internal readonly short[] Counts = new short[16];
                internal readonly short[] Symbols;

                internal Huffman(byte[] lengths, int n)
                {
                    Symbols = new short[n];
                    for (int i = 0; i < n; i++) Counts[lengths[i]]++;
                    Counts[0] = 0;
                    var offs = new short[16];
                    for (int len = 1; len < 16; len++) offs[len] = (short)(offs[len - 1] + Counts[len - 1]);
                    for (int i = 0; i < n; i++) if (lengths[i] != 0) Symbols[offs[lengths[i]]++] = (short)i;
                }
            }

            private int Decode(Huffman h)
            {
                int code = 0, first = 0, index = 0;
                for (int len = 1; len < 16; len++)
                {
                    code |= Bits(1);
                    int count = h.Counts[len];
                    if (code - count < first) return h.Symbols[index + (code - first)];
                    index += count; first += count;
                    first <<= 1; code <<= 1;
                }
                throw new InvalidDataException("bad deflate code");
            }

            private static Huffman fixedLit, fixedDist;

            private static Huffman Fixed(bool literals)
            {
                if (fixedLit == null)
                {
                    var l = new byte[288];
                    for (int i = 0; i < 288; i++) l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
                    var d = new byte[30];
                    for (int i = 0; i < 30; i++) d[i] = 5;
                    fixedDist = new Huffman(d, 30);
                    fixedLit = new Huffman(l, 288);
                }
                return literals ? fixedLit : fixedDist;
            }

            private void Dynamic()
            {
                int nlen = Bits(5) + 257, ndist = Bits(5) + 1, ncode = Bits(4) + 4;
                if (nlen > 286 || ndist > 30) throw new InvalidDataException("bad deflate counts");
                var lengths = new byte[320];
                for (int i = 0; i < ncode; i++) lengths[CodeOrder[i]] = (byte)Bits(3);
                var lencode = new Huffman(lengths, 19);
                Array.Clear(lengths, 0, lengths.Length);
                int index = 0;
                while (index < nlen + ndist)
                {
                    int sym = Decode(lencode);
                    if (sym < 16) { lengths[index++] = (byte)sym; continue; }
                    int len = 0, repeat;
                    if (sym == 16) { if (index == 0) throw new InvalidDataException("bad deflate repeat"); len = lengths[index - 1]; repeat = 3 + Bits(2); }
                    else if (sym == 17) repeat = 3 + Bits(3);
                    else repeat = 11 + Bits(7);
                    if (index + repeat > nlen + ndist) throw new InvalidDataException("bad deflate lengths");
                    while (repeat-- > 0) lengths[index++] = (byte)len;
                }
                var lit = new Huffman(lengths, nlen);
                var distLengths = new byte[30];
                Array.Copy(lengths, nlen, distLengths, 0, ndist);
                Codes(lit, new Huffman(distLengths, ndist));
            }

            private void Codes(Huffman lit, Huffman dist)
            {
                while (true)
                {
                    int sym = Decode(lit);
                    if (sym < 256) { Put((byte)sym); continue; }
                    if (sym == 256) return;
                    sym -= 257;
                    if (sym >= 29) throw new InvalidDataException("bad deflate length");
                    int len = LenBase[sym] + Bits(LenExtra[sym]);
                    int d = Decode(dist);
                    if (d >= 30) throw new InvalidDataException("bad deflate distance");
                    int back = DistBase[d] + Bits(DistExtra[d]);
                    if (back > count) throw new InvalidDataException("deflate distance too far");
                    int from = count - back;
                    for (int i = 0; i < len; i++) Put(outp[from + i]);
                }
            }
        }

        private static void Need(byte[] b, int end) { if (end > b.Length) throw new InvalidDataException("gzip cut short"); }

        private static int SkipZero(byte[] b, int pos)
        {
            while (pos < b.Length && b[pos] != 0) pos++;
            return pos + 1;
        }

        private static void WriteUInt(Stream s, uint v)
        {
            for (int i = 0; i < 4; i++) s.WriteByte((byte)(v >> (8 * i)));
        }

        private static uint ReadUInt(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

        internal static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
