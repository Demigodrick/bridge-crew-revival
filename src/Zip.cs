using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StbcRevival
{
    /// <summary>
    /// Reading a zip a piece at a time, as the updater fetches it from a release (ReleaseZip): the index at the end
    /// (the central directory), then one entry's bytes. No Unity and no downloading here, so it can be tested.
    /// Plain zips only (stored or deflated, under 4 GB): what tools/package.sh writes.
    /// </summary>
    internal static class Zip
    {
        internal sealed class Entry
        {
            internal string Name;
            internal int Method;          // 0 stored, 8 deflated
            internal uint Crc;
            internal long CompressedSize, Size, LocalOffset;
            internal int NameLength;      // the local header has the same name; its extra field can differ
        }

        internal const int EndRecordSize = 22;
        internal const int LocalHeaderSize = 30;

        /// <summary>
        /// Where the central directory is, from the end of the zip: tail holds the zip's last bytes (from tailStart
        /// on; total its size). Throws InvalidDataException when there's no end record in it.
        /// </summary>
        internal static void FindDirectory(byte[] tail, long tailStart, long total, out long dirOffset, out long dirSize)
        {
            for (int i = tail.Length - EndRecordSize; i >= 0; i--)
            {
                if (tail[i] != 0x50 || tail[i + 1] != 0x4b || tail[i + 2] != 5 || tail[i + 3] != 6) continue;
                int commentLength = U16(tail, i + 20);
                if (i + EndRecordSize + commentLength != tail.Length) continue; // a PK0506 inside the comment
                dirSize = U32(tail, i + 12);
                dirOffset = U32(tail, i + 16);
                if (dirOffset == 0xFFFFFFFF || dirSize == 0xFFFFFFFF) throw new InvalidDataException("zip64 isn't supported");
                if (dirOffset + dirSize > tailStart + i) throw new InvalidDataException("zip directory runs past its end record");
                return;
            }
            throw new InvalidDataException("no zip end record");
        }

        /// <summary>The entries in a central directory (its bytes).</summary>
        internal static List<Entry> ReadDirectory(byte[] dir)
        {
            var entries = new List<Entry>();
            int at = 0;
            while (at + 46 <= dir.Length)
            {
                if (U32(dir, at) != 0x02014b50) throw new InvalidDataException("bad zip directory entry");
                int flags = U16(dir, at + 8);
                int nameLength = U16(dir, at + 28), extraLength = U16(dir, at + 30), commentLength = U16(dir, at + 32);
                if (at + 46 + nameLength > dir.Length) throw new InvalidDataException("zip directory cut short");
                var e = new Entry
                {
                    Method = U16(dir, at + 10),
                    Crc = U32(dir, at + 16),
                    CompressedSize = U32(dir, at + 20),
                    Size = U32(dir, at + 24),
                    LocalOffset = U32(dir, at + 42),
                    NameLength = nameLength,
                    Name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.ASCII).GetString(dir, at + 46, nameLength),
                };
                if ((flags & 1) != 0) throw new InvalidDataException(e.Name + " is encrypted");
                entries.Add(e);
                at += 46 + nameLength + extraLength + commentLength;
            }
            return entries;
        }

        /// <summary>
        /// How many bytes from the entry's local header to fetch: the header, its name, the data, and room for a
        /// local extra field up to extraRoom long (Extract says when that wasn't enough).
        /// </summary>
        internal static long FetchLength(Entry e, int extraRoom) => LocalHeaderSize + e.NameLength + extraRoom + e.CompressedSize;

        /// <summary>
        /// The entry's file, from bytes fetched at its local header (FetchLength of them, or the rest of the zip):
        /// unpacked and checked against its CRC. Returns null when the local extra field is longer than what was
        /// fetched allows for (needExtra then says how long it is: fetch again with that much room).
        /// </summary>
        internal static byte[] Extract(Entry e, byte[] local, out int needExtra)
        {
            needExtra = 0;
            if (local.Length < LocalHeaderSize || U32(local, 0) != 0x04034b50) throw new InvalidDataException("no local header for " + e.Name);
            int nameLength = U16(local, 26), extraLength = U16(local, 28);
            long start = LocalHeaderSize + nameLength + extraLength;
            if (start + e.CompressedSize > local.Length)
            {
                needExtra = extraLength;
                return null;
            }
            if (e.Size > int.MaxValue || e.CompressedSize > int.MaxValue) throw new InvalidDataException(e.Name + " is too big");
            byte[] data;
            if (e.Method == 0)
            {
                if (e.CompressedSize != e.Size) throw new InvalidDataException(e.Name + ": stored sizes differ");
                data = new byte[e.Size];
                Buffer.BlockCopy(local, (int)start, data, 0, (int)e.Size);
            }
            else if (e.Method == 8) data = Gzip.Inflate(local, (int)start, (int)e.Size);
            else throw new InvalidDataException($"{e.Name}: compression method {e.Method} isn't supported");
            if (Gzip.Crc(data) != e.Crc) throw new InvalidDataException(e.Name + " doesn't match its CRC");
            return data;
        }

        /// <summary>
        /// The entry for a path inside the release's folder ("plugins/StbcRevival.dll" is
        /// "BridgeCrewRevival-0.7.47/plugins/StbcRevival.dll"), or null.
        /// </summary>
        internal static Entry Find(List<Entry> entries, string path)
        {
            foreach (var e in entries)
            {
                int slash = e.Name.IndexOf('/');
                if (slash > 0 && e.Name.Length - slash - 1 == path.Length
                    && string.CompareOrdinal(e.Name, slash + 1, path, 0, path.Length) == 0)
                    return e;
            }
            return null;
        }

        private static int U16(byte[] b, int at) => b[at] | b[at + 1] << 8;
        private static uint U32(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
    }
}
