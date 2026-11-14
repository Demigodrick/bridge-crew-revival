using System;
using System.IO;
using System.Text;

namespace StbcRevival
{
    /// <summary>A PCM WAV file (8 or 16 bit, mono or stereo) as samples, for the ships' own sounds (Defiant, Quantum).</summary>
    internal sealed class PcmWave
    {
        public int Channels, Rate;
        public float[] Samples;
        public static PcmWave Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                throw new InvalidDataException("Not a RIFF WAVE file");
            int channels = 0, rate = 0, bits = 0, format = 0, offset = -1, count = 0;
            for (int pos = 12; pos <= bytes.Length - 8;)
            {
                string tag = Encoding.ASCII.GetString(bytes, pos, 4);
                int length = BitConverter.ToInt32(bytes, pos + 4);
                pos += 8;
                if (length < 0 || length > bytes.Length - pos) throw new InvalidDataException("Truncated WAV chunk");
                if (tag == "fmt ")
                {
                    if (length < 16) throw new InvalidDataException("Invalid WAV format");
                    format = BitConverter.ToUInt16(bytes, pos);
                    channels = BitConverter.ToUInt16(bytes, pos + 2);
                    rate = BitConverter.ToInt32(bytes, pos + 4);
                    bits = BitConverter.ToUInt16(bytes, pos + 14);
                }
                if (tag == "data") { offset = pos; count = length; }
                pos += length;
                if ((length & 1) != 0 && pos < bytes.Length) pos++;
            }
            if (format != 1 || channels < 1 || channels > 2 || rate < 8000 || rate > 192000 || (bits != 8 && bits != 16) || offset < 0 || count == 0 || count % (channels * (bits / 8)) != 0)
                throw new InvalidDataException("Expected PCM8/PCM16 mono or stereo WAV");
            float[] samples = new float[count / (bits / 8)];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = bits == 8 ? (bytes[offset + i] - 128) / 128f : BitConverter.ToInt16(bytes, offset + i * 2) / 32768f;
            return new PcmWave { Channels = channels, Rate = rate, Samples = samples };
        }

        /// <summary>An audio clip of it (main thread).</summary>
        public UnityEngine.AudioClip Clip(string name)
        {
            var clip = UnityEngine.AudioClip.Create(name, Samples.Length / Channels, Channels, Rate, false);
            clip.SetData(Samples, 0);
            return clip;
        }
    }
}
