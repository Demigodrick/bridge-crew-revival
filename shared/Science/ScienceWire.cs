using System;
using System.Text;

namespace StbcRevival.Science
{
    public enum ScienceMsgKind { Claim = 1, Release = 2, Heartbeat = 3, Report = 4, Complete = 5 }

    internal struct ScienceMsg
    {
        public ScienceMsgKind Kind;
        public string Id;
        public float Progress;
    }

    // Client to host event payloads. Layout, exact lengths only:
    //   Claim / Release / Heartbeat: [kind]
    //   Report:   [4][idLen 1..64][id UTF-8][progress float32 little-endian]
    //   Complete: [5][idLen 1..64][id UTF-8]
    // The encoders return null for an unusable id; the reader rejects anything malformed.
    internal static class ScienceWire
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Claim() { return new[] { (byte)ScienceMsgKind.Claim }; }
        public static byte[] Release() { return new[] { (byte)ScienceMsgKind.Release }; }
        public static byte[] Heartbeat() { return new[] { (byte)ScienceMsgKind.Heartbeat }; }

        public static byte[] Report(string id, float p)
        {
            byte[] idb = IdBytes(id);
            if (idb == null) return null;
            byte[] b = new byte[2 + idb.Length + 4];
            b[0] = (byte)ScienceMsgKind.Report;
            b[1] = (byte)idb.Length;
            Buffer.BlockCopy(idb, 0, b, 2, idb.Length);
            byte[] f = BitConverter.GetBytes(p);
            if (!BitConverter.IsLittleEndian) Array.Reverse(f);
            Buffer.BlockCopy(f, 0, b, 2 + idb.Length, 4);
            return b;
        }

        public static byte[] Complete(string id)
        {
            byte[] idb = IdBytes(id);
            if (idb == null) return null;
            byte[] b = new byte[2 + idb.Length];
            b[0] = (byte)ScienceMsgKind.Complete;
            b[1] = (byte)idb.Length;
            Buffer.BlockCopy(idb, 0, b, 2, idb.Length);
            return b;
        }

        static byte[] IdBytes(string id)
        {
            if (!ScanBoard.ValidId(id)) return null;
            byte[] idb;
            try { idb = Utf8.GetBytes(id); } catch (Exception) { return null; } // a lone surrogate
            return idb.Length <= ScanBoard.MaxIdLength ? idb : null;
        }

        public static bool TryRead(byte[] b, out ScienceMsg m)
        {
            m = new ScienceMsg();
            if (b == null || b.Length == 0) return false;
            ScienceMsgKind k = (ScienceMsgKind)b[0];
            switch (k)
            {
                case ScienceMsgKind.Claim:
                case ScienceMsgKind.Release:
                case ScienceMsgKind.Heartbeat:
                    if (b.Length != 1) return false;
                    m.Kind = k;
                    return true;
                case ScienceMsgKind.Report:
                case ScienceMsgKind.Complete:
                    if (b.Length < 2) return false;
                    int n = b[1];
                    if (n < 1 || n > ScanBoard.MaxIdLength) return false;
                    int tail = k == ScienceMsgKind.Report ? 4 : 0;
                    if (b.Length != 2 + n + tail) return false;
                    string id;
                    try { id = Utf8.GetString(b, 2, n); } catch (Exception) { return false; }
                    if (!ScanBoard.ValidId(id)) return false;
                    float p = 0f;
                    if (tail == 4)
                    {
                        byte[] f = new byte[4];
                        Buffer.BlockCopy(b, 2 + n, f, 0, 4);
                        if (!BitConverter.IsLittleEndian) Array.Reverse(f);
                        p = BitConverter.ToSingle(f, 0);
                        if (float.IsNaN(p) || float.IsInfinity(p)) return false;
                    }
                    m.Kind = k; m.Id = id; m.Progress = p;
                    return true;
                default:
                    return false;
            }
        }
    }
}
