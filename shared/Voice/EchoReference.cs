#nullable disable
// Revival voice echo reference: what each crewmate's voice is about to play out of this computer's speakers, at
// 48 kHz, on one timeline with the microphone (sample index = milliseconds on the voice clock x 48). Playback
// writes each crewmate's audio where it's due to play; the capture worker reads, for each 10 ms of microphone,
// what played just then, one channel per crewmate (the echo canceller models each one's path from speaker to
// microphone on its own: their voices come from different places on the bridge).
// Written on the main thread, read on the capture worker: one lock.
// Compiled by the mod (net35) and the voice tests (net8): plain arrays only.
using System;

namespace StbcRevival.Voice
{
    public sealed class EchoReference
    {
        public const int SamplesPerMs = VoiceFormat.SampleRate / 1000;

        readonly object sync = new object();
        readonly float[][] rings;
        readonly long[] top;          // per channel: one past the newest sample written (0: nothing yet)
        readonly int size;
        long lastWrite = long.MinValue;

        public EchoReference(int channels, int seconds)
        {
            size = seconds * VoiceFormat.SampleRate;
            rings = new float[channels][];
            top = new long[channels];
            for (int c = 0; c < channels; c++) rings[c] = new float[size];
        }

        public int Channels { get { return rings.Length; } }

        /// <summary>Whether anything was written at or after this sample index.</summary>
        public bool ActiveSince(long index)
        {
            lock (sync) return lastWrite >= index;
        }

        /// <summary>48 kHz samples x[0 .. n) of a channel, playing from sample index at on.</summary>
        public void Write(int channel, float[] x, int n, long at)
        {
            if (channel < 0 || channel >= rings.Length || n <= 0 || at < 0) return;
            lock (sync)
            {
                var ring = rings[channel];
                // What lies between the newest sample and this write was silence: clear it, so nothing from a
                // lap of the ring ago is read as played.
                long from = Math.Max(top[channel], at + n - size);
                for (long i = from; i < at; i++) ring[(int)(i % size)] = 0f;
                for (int i = 0; i < n; i++) ring[(int)((at + i) % size)] = x[i];
                if (at + n > top[channel]) top[channel] = at + n;
                if (at + n > lastWrite) lastWrite = at + n;
            }
        }

        /// <summary>Samples [from, from + n) of every channel into output, interleaved (n x Channels) as 16-bit,
        /// as the echo canceller takes them. Never written, too old, or not yet written: silence.</summary>
        public void Read(long from, int n, short[] output)
        {
            int channels = rings.Length;
            lock (sync)
            {
                for (int c = 0; c < channels; c++)
                {
                    var ring = rings[c];
                    long t = top[c];
                    for (int i = 0; i < n; i++)
                    {
                        long at = from + i;
                        float v = at >= 0 && at < t && at >= t - size ? ring[(int)(at % size)] : 0f;
                        float s = v * 32767f;
                        output[i * channels + c] = (short)(s > 32767f ? 32767 : s < -32768f ? -32768 : (int)s);
                    }
                }
            }
        }
    }
}
