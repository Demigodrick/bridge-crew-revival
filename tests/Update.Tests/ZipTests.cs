using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using StbcRevival;
using Xunit;
using Xunit.Abstractions;

namespace Update.Tests;

public class ZipTests
{
    private readonly ITestOutputHelper output;
    public ZipTests(ITestOutputHelper output) => this.output = output;

    // Reads a zip the way the updater does: the tail, then the directory, then each entry's own range of bytes.
    private static (List<Zip.Entry> entries, Func<Zip.Entry, byte[]> extract) Open(byte[] zip, int tailLength = 65536 + 22)
    {
        long total = zip.Length;
        long tailStart = Math.Max(0, total - tailLength);
        var tail = zip[(int)tailStart..];
        Zip.FindDirectory(tail, tailStart, total, out var dirOffset, out var dirSize);
        var dir = zip[(int)dirOffset..(int)(dirOffset + dirSize)];
        var entries = Zip.ReadDirectory(dir);
        byte[] Extract(Zip.Entry e)
        {
            int room = 64;
            while (true)
            {
                long end = Math.Min(total, e.LocalOffset + Zip.FetchLength(e, room));
                var local = zip[(int)e.LocalOffset..(int)end];
                var data = Zip.Extract(e, local, out var need);
                if (data != null) return data;
                Assert.True(need > room, "asked again for no more room");
                room = need;
            }
        }
        return (entries, Extract);
    }

    private static byte[] MakeZip(Dictionary<string, byte[]> files, CompressionLevel level, string comment = null)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            if (comment != null) z.Comment = comment;
            foreach (var kv in files)
            {
                var e = z.CreateEntry(kv.Key, level);
                using var s = e.Open();
                s.Write(kv.Value);
            }
        }
        return ms.ToArray();
    }

    private static Dictionary<string, byte[]> Sample()
    {
        var rnd = new Random(7);
        var noise = new byte[200_000]; rnd.NextBytes(noise);
        var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 50_000).Select(i => $"v {i % 97} {i % 13}.5 {i}\n")));
        return new Dictionary<string, byte[]>
        {
            ["BridgeCrewRevival-9.9.9/plugins/StbcRevival.dll"] = noise,
            ["BridgeCrewRevival-9.9.9/ships/Nova/Nova.obj"] = text,
            ["BridgeCrewRevival-9.9.9/galaxies/jagged-sword.json"] = System.Text.Encoding.UTF8.GetBytes("{\"name\":\"Jagged Sword\"}"),
            ["BridgeCrewRevival-9.9.9/empty.txt"] = Array.Empty<byte>(),
        };
    }

    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.NoCompression)]
    public void EveryEntryComesBackAsWritten(CompressionLevel level)
    {
        var files = Sample();
        var (entries, extract) = Open(MakeZip(files, level));
        Assert.Equal(files.Count, entries.Count);
        foreach (var kv in files)
        {
            var e = Zip.Find(entries, kv.Key[(kv.Key.IndexOf('/') + 1)..]);
            Assert.NotNull(e);
            Assert.Equal(kv.Value, extract(e));
        }
    }

    [Fact]
    public void FindMatchesTheWholePathOnly()
    {
        var (entries, _) = Open(MakeZip(Sample(), CompressionLevel.Optimal));
        Assert.NotNull(Zip.Find(entries, "ships/Nova/Nova.obj"));
        Assert.Null(Zip.Find(entries, "Nova/Nova.obj"));
        Assert.Null(Zip.Find(entries, "ships/Nova/Nova.ob"));
        Assert.Null(Zip.Find(entries, "BridgeCrewRevival-9.9.9/ships/Nova/Nova.obj"));
    }

    [Fact]
    public void AZipCommentIsSkipped()
    {
        var files = Sample();
        var (entries, extract) = Open(MakeZip(files, CompressionLevel.Optimal, "PK\u0005\u0006 a comment with an end record's signature in it"));
        Assert.Equal(files["BridgeCrewRevival-9.9.9/galaxies/jagged-sword.json"], extract(Zip.Find(entries, "galaxies/jagged-sword.json")));
    }

    [Fact]
    public void AShortTailStillFindsTheEnd()
    {
        var (entries, _) = Open(MakeZip(Sample(), CompressionLevel.Optimal), tailLength: 22);
        Assert.Equal(4, entries.Count);
    }

    [Fact]
    public void ACorruptedEntryIsRefused()
    {
        var files = Sample();
        var zip = MakeZip(files, CompressionLevel.NoCompression);
        var (entries, _) = Open(zip);
        var e = Zip.Find(entries, "ships/Nova/Nova.obj");
        var local = zip[(int)e.LocalOffset..];
        local[Zip.LocalHeaderSize + e.NameLength + 1000] ^= 0xff; // stored, and no local extra field from ZipArchive
        Assert.Throws<InvalidDataException>(() => Zip.Extract(e, local, out _));
    }

    [Fact]
    public void NoEndRecordIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Zip.FindDirectory(new byte[100], 0, 100, out _, out _));
    }

    // The real release zip, when this checkout has one (dist/release, written by tools/release.sh): every entry
    // unpacks to the file package.sh put in, and the biggest is timed (it unpacks on a thread in game).
    [Fact]
    public void TheReleaseZipUnpacks()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var path = Path.Combine(root, "dist/release/BridgeCrewRevival.zip");
        if (!File.Exists(path)) path = "/home/patrick/stbc/dist/release/BridgeCrewRevival.zip";
        if (!File.Exists(path)) { output.WriteLine("no release zip here; skipped"); return; }
        var zip = File.ReadAllBytes(path);
        var (entries, extract) = Open(zip);
        using var archive = new ZipArchive(new MemoryStream(zip));
        var sw = new Stopwatch();
        long biggest = 0; string biggestName = null; TimeSpan biggestTime = default;
        foreach (var e in entries)
        {
            if (e.Name.EndsWith("/")) continue;
            sw.Restart();
            var data = extract(e);
            sw.Stop();
            using var s = archive.GetEntry(e.Name).Open();
            var want = new MemoryStream(); s.CopyTo(want);
            Assert.Equal(SHA256.HashData(want.ToArray()), SHA256.HashData(data));
            if (data.Length > biggest) { biggest = data.Length; biggestName = e.Name; biggestTime = sw.Elapsed; }
        }
        output.WriteLine($"{entries.Count} entries; biggest {biggestName} ({biggest / 1e6:F1} MB) in {biggestTime.TotalSeconds:F2} s");
    }
}
