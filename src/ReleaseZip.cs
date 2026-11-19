using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace StbcRevival
{
    /// <summary>
    /// A release's BridgeCrewRevival.zip on GitHub, read a piece at a time with HTTP range requests (tested in game
    /// with Unity 5.4's UnityWebRequest, through GitHub's redirect): the end of the zip for its index, then just the
    /// bytes of each file wanted. So a release needs only the zip (and the few files older updaters fetch), and an
    /// update still downloads only what it needs. GitHub refuses "the last N bytes", so the size comes first, from a
    /// one-byte request's Content-Range. Each file is unpacked on a thread (a ship model is up to 60 MB).
    /// </summary>
    internal sealed class ReleaseZip
    {
        private const string Releases = "https://github.com/Demigodrick/bridge-crew-revival/releases/download/v";
        private const int TailBytes = 64 * 1024;   // the zip's end record and index (117 files: about 10 KB)
        private const int ExtraRoom = 256;         // for a local header's extra field (package.sh writes 28 bytes)
        private const float IndexSeconds = 60f;
        private const float FileSeconds = 900f;    // a large model on a slow line

        private readonly string url;
        private List<Zip.Entry> entries;
        private long total;

        internal ReleaseZip(string version) { url = Releases + version + "/BridgeCrewRevival.zip"; }

        /// <summary>Set when Open failed: why, for the log.</summary>
        internal string Problem { get; private set; }
        internal bool IsOpen => entries != null;

        /// <summary>Reads the zip's index. Afterwards IsOpen, or Problem says why not.</summary>
        internal IEnumerator Open()
        {
            Problem = null;
            byte[] first = null; long size = -1; string problem = null;
            yield return Range(0, 0, IndexSeconds, (b, t, p) => { first = b; size = t; problem = p; });
            if (problem != null) { Problem = "the zip's size: " + problem; yield break; }
            if (size < Zip.EndRecordSize) { Problem = "the zip's size wasn't given"; yield break; }
            total = size;

            long tailStart = Math.Max(0, total - TailBytes);
            byte[] tail = null;
            yield return Range(tailStart, total - 1, IndexSeconds, (b, t, p) => { tail = b; problem = p; });
            if (problem != null) { Problem = "the zip's end: " + problem; yield break; }
            long dirOffset, dirSize;
            byte[] dir;
            try
            {
                Zip.FindDirectory(tail, tailStart, total, out dirOffset, out dirSize);
                dir = dirOffset >= tailStart ? Slice(tail, dirOffset - tailStart, dirSize) : null;
            }
            catch (Exception e) { Problem = e.Message; yield break; }
            if (dir == null)
            {
                yield return Range(dirOffset, dirOffset + dirSize - 1, IndexSeconds, (b, t, p) => { dir = b; problem = p; });
                if (problem != null) { Problem = "the zip's index: " + problem; yield break; }
            }
            try { entries = Zip.ReadDirectory(dir); }
            catch (Exception e) { Problem = e.Message; }
        }

        /// <summary>
        /// One file of the release by its path in the release folder ("plugins/StbcRevival.dll"): fetched, unpacked
        /// and CRC-checked: done(bytes, null), or done(null, why) if it isn't there or something failed.
        /// </summary>
        internal IEnumerator Get(string path, Action<byte[], string> done)
        {
            if (entries == null) { done(null, "the zip isn't open"); yield break; }
            var e = Zip.Find(entries, path);
            if (e == null) { done(null, "it isn't in the zip"); yield break; }
            int room = ExtraRoom;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[] local = null; string problem = null;
                long end = Math.Min(total, e.LocalOffset + Zip.FetchLength(e, room)) - 1;
                yield return Range(e.LocalOffset, end, FileSeconds, (b, t, p) => { local = b; problem = p; });
                if (problem != null) { done(null, problem); yield break; }

                byte[] data = null; int needExtra = 0; string failed = null; bool finished = false;
                var thread = new System.Threading.Thread(() =>
                {
                    try { data = Zip.Extract(e, local, out needExtra); }
                    catch (Exception x) { failed = x.Message; }
                    finished = true;
                }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
                while (!finished) yield return null;
                local = null;
                if (failed != null) { done(null, failed); yield break; }
                if (data != null) { done(data, null); yield break; }
                room = needExtra;
            }
            done(null, "its header didn't fit");
        }

        // Bytes from..to (inclusive) of the zip: done(bytes, total size, null) or done(null, -1, problem). A server
        // that ignores the range and sends the whole zip is stopped, rather than downloading 150 MB.
        private IEnumerator Range(long from, long to, float seconds, Action<byte[], long, string> done)
        {
            long want = to - from + 1;
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Range", "bytes=" + from + "-" + to);
            req.Send();
            float until = Time.realtimeSinceStartup + seconds;
            bool tooMuch = false;
            while (!req.isDone && Time.realtimeSinceStartup < until)
            {
                if ((long)req.downloadedBytes > want + 4096) { tooMuch = true; break; }
                yield return null;
            }
            try
            {
                if (tooMuch) { req.Abort(); done(null, -1, "the server sent more than was asked for"); yield break; }
                if (!req.isDone) { req.Abort(); done(null, -1, "no answer"); yield break; }
                if (req.isError) { done(null, -1, req.error); yield break; }
                if (req.responseCode != 206) { done(null, -1, "HTTP " + req.responseCode); yield break; }
                var range = req.GetResponseHeader("Content-Range"); // "bytes 0-29/155924886"
                long size = -1, start = -1;
                if (range != null && range.StartsWith("bytes "))
                {
                    int dash = range.IndexOf('-'), slash = range.IndexOf('/');
                    if (dash > 6 && slash > dash)
                    {
                        long.TryParse(range.Substring(6, dash - 6), out start);
                        long.TryParse(range.Substring(slash + 1), out size);
                    }
                }
                var data = req.downloadHandler.data;
                if (start != from || data == null || data.Length != want) { done(null, -1, "a different range came back (" + range + ")"); yield break; }
                done(data, size, null);
            }
            finally { req.Dispose(); }
        }

        private static byte[] Slice(byte[] b, long at, long n)
        {
            if (at < 0 || at + n > b.Length) throw new System.IO.InvalidDataException("zip index outside what was fetched");
            var r = new byte[n];
            Buffer.BlockCopy(b, (int)at, r, 0, (int)n);
            return r;
        }
    }
}
