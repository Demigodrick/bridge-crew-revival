#nullable disable
// Revival voice wire format. Compiled by the mod (net35) and the voice tests (net8): keep it to plain arrays.
namespace StbcRevival.Voice
{
    public static class VoiceFormat
    {
        public const int SampleRate = 48000, FrameSamples = 960, FrameMs = 20, NoiseFrameSamples = 480;
        public const int Channel = 4;
        public static int PlaybackRate = 48000;
    }

    public sealed class VoicePacketIn
    {
        public uint Stream;
        public uint Seq;
        public bool BurstStart;
        public byte[] Opus;
        public int OpusLength;
        public byte[] Prev;
        public int PrevLength;
    }

    public static class VoiceWire
    {
        public const byte VoiceType = (byte)'R', ReportType = (byte)'Q', Version = 1;
        const int Header = 13;

        static void PutU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24);
        }

        static uint GetU32(byte[] s, int o)
        {
            return (uint)(s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24));
        }

        public static int WriteVoice(byte[] dest, uint stream, uint seq, bool burstStart,
                                     byte[] opus, int opusLength, byte[] prev, int prevLength)
        {
            if (prev == null) prevLength = 0;
            int total = Header + opusLength + 2 + prevLength;
            dest[0] = VoiceType;
            dest[1] = Version;
            dest[2] = (byte)(burstStart ? 1 : 0);
            PutU32(dest, 3, stream);
            PutU32(dest, 7, seq);
            dest[11] = (byte)opusLength; dest[12] = (byte)(opusLength >> 8);
            System.Buffer.BlockCopy(opus, 0, dest, Header, opusLength);
            int o = Header + opusLength;
            dest[o] = (byte)prevLength; dest[o + 1] = (byte)(prevLength >> 8);
            if (prevLength > 0) System.Buffer.BlockCopy(prev, 0, dest, o + 2, prevLength);
            return total;
        }

        public static bool TryReadVoice(byte[] src, int length, VoicePacketIn into)
        {
            if (src == null || into == null || length < Header + 2 || length > src.Length) return false;
            if (src[0] != VoiceType || src[1] != Version) return false;
            int opusLen = src[11] | (src[12] << 8);
            int o = Header + opusLen;
            if (o + 2 > length) return false;
            int prevLen = src[o] | (src[o + 1] << 8);
            if (o + 2 + prevLen > length) return false;
            into.BurstStart = (src[2] & 1) != 0;
            into.Stream = GetU32(src, 3);
            into.Seq = GetU32(src, 7);
            if (into.Opus == null || into.Opus.Length < opusLen) into.Opus = new byte[System.Math.Max(opusLen, 256)];
            System.Buffer.BlockCopy(src, Header, into.Opus, 0, opusLen);
            into.OpusLength = opusLen;
            if (into.Prev == null || into.Prev.Length < prevLen) into.Prev = new byte[System.Math.Max(prevLen, 256)];
            if (prevLen > 0) System.Buffer.BlockCopy(src, o + 2, into.Prev, 0, prevLen);
            into.PrevLength = prevLen;
            return true;
        }

        public static int WriteReport(byte[] dest, uint stream, byte lossPercent, ushort jitterMs)
        {
            dest[0] = ReportType;
            dest[1] = Version;
            PutU32(dest, 2, stream);
            dest[6] = lossPercent;
            dest[7] = (byte)jitterMs; dest[8] = (byte)(jitterMs >> 8);
            return 9;
        }

        public static bool TryReadReport(byte[] src, int length, out uint stream, out byte lossPercent, out ushort jitterMs)
        {
            stream = 0; lossPercent = 0; jitterMs = 0;
            if (src == null || length < 9 || length > src.Length) return false;
            if (src[0] != ReportType || src[1] != Version) return false;
            stream = GetU32(src, 2);
            lossPercent = src[6];
            jitterMs = (ushort)(src[7] | (src[8] << 8));
            return true;
        }
    }
}
