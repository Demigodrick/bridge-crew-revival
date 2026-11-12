#nullable disable
// Revival voice playout, one per speaker, on the playout thread: exactly one frame out per 20 ms tick, popped from
// the speaker's JitterBuffer and time-stretched to hold the delay at the buffer's target while they talk, as NetEq
// does. The jitter buffer only picks its delay at the start of each burst of speech; mid-burst, a network that
// calms down (or a batch of late packets) leaves more buffered than needed, and an underrun leaves less. Over the
// last half second: if even the least buffered moment had a frame more than the target, speech is sped up a little
// (one pitch period cut); if even the most buffered moment was half a frame short of it, slowed down a little (one
// period repeated). At most one cut or repeat every 60 ms, so at most about a quarter faster or slower, and only
// while it's going on.
// Compiled by the mod (net35) and the voice tests (net8): plain arrays only.
using System;
using System.Threading;

namespace StbcRevival.Voice
{
    public sealed class Playout
    {
        public const int WindowTicks = 25;   // half a second of buffer levels
        public const int StretchEvery = 3;   // ticks between two cuts or repeats, at least

        readonly JitterBuffer buffer;
        readonly int frame, rate;
        readonly float[] fifo, one, two, stretched;
        int fifoCount;
        volatile int heldSamples;

        readonly int[] levels = new int[WindowTicks];
        int levelCount, levelNext;
        int sinceStretch = StretchEvery;
        bool wasPlaying;
        int spedUp, slowedDown; // samples, since TakeStretched (Interlocked: taken from another thread)

        public Playout(JitterBuffer buffer, int frameSamples, int rate)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            this.buffer = buffer;
            frame = frameSamples;
            this.rate = rate;
            fifo = new float[frame * 4];
            one = new float[frame];
            two = new float[frame * 2];
            stretched = new float[frame * 3];
        }

        public JitterBuffer Buffer { get { return buffer; } }

        /// <summary>Decoded audio held here, not yet handed out (any thread).</summary>
        public int HeldMs { get { return heldSamples * 1000 / rate; } }

        /// <summary>Milliseconds of speech sped up (cut) and slowed down (added) since the last call (any thread).</summary>
        public void TakeStretched(out int spedUpMs, out int slowedDownMs)
        {
            spedUpMs = Interlocked.Exchange(ref spedUp, 0) * 1000 / rate;
            slowedDownMs = Interlocked.Exchange(ref slowedDown, 0) * 1000 / rate;
        }

        /// <summary>One tick: exactly one frame into pcm. downstreamMs is decoded audio already handed out and
        /// still waiting to be played. False: nothing to play (pcm cleared).</summary>
        public bool Next(float[] pcm, int downstreamMs)
        {
            while (fifoCount < frame)
            {
                FrameSource source;
                if (!buffer.Pop(one, out source)) break;
                if (!wasPlaying) { wasPlaying = true; levelCount = levelNext = 0; sinceStretch = StretchEvery; }
                int mode = source == FrameSource.Concealed ? 0 : Decide();
                if (mode > 0 && buffer.DepthMs >= VoiceFormat.FrameMs)
                {
                    // Too much buffered: this frame and the next as one, a period shorter.
                    Array.Copy(one, two, frame);
                    if (buffer.Pop(one, out source) && source != FrameSource.Concealed)
                    {
                        Array.Copy(one, 0, two, frame, frame);
                        int cut = TimeStretch.Compress(two, 2 * frame, rate, stretched);
                        Append(stretched, 2 * frame - cut);
                        if (cut > 0) { Interlocked.Add(ref spedUp, cut); sinceStretch = 0; }
                    }
                    else
                    {
                        Append(two, frame);
                        Append(one, frame);
                    }
                }
                else if (mode < 0)
                {
                    // Too little: this frame a period longer.
                    int added = TimeStretch.Expand(one, frame, rate, stretched);
                    Append(stretched, frame + added);
                    if (added > 0) { Interlocked.Add(ref slowedDown, added); sinceStretch = 0; }
                }
                else Append(one, frame);
            }

            if (fifoCount == 0)
            {
                Array.Clear(pcm, 0, frame);
                wasPlaying = false;
                heldSamples = 0;
                return false;
            }
            int n = Math.Min(frame, fifoCount);
            Array.Copy(fifo, 0, pcm, 0, n);
            if (n < frame) Array.Clear(pcm, n, frame - n); // the burst's last few samples, then silence
            fifoCount -= n;
            if (fifoCount > 0) Array.Copy(fifo, n, fifo, 0, fifoCount);
            heldSamples = fifoCount;
            sinceStretch++;
            if (buffer.Playing) Record(buffer.DepthMs + HeldMs + Math.Max(0, downstreamMs));
            else wasPlaying = fifoCount > 0;
            return true;
        }

        // +1 speed up, -1 slow down, 0 as it is.
        int Decide()
        {
            if (levelCount < WindowTicks || sinceStretch < StretchEvery) return 0;
            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < levelCount; i++)
            {
                if (levels[i] < min) min = levels[i];
                if (levels[i] > max) max = levels[i];
            }
            // Right after a pop, a buffer on target holds its target less the frame just played.
            int onTarget = buffer.TargetMs - VoiceFormat.FrameMs;
            if (min >= onTarget + VoiceFormat.FrameMs) return 1;
            if (max <= onTarget - VoiceFormat.FrameMs / 2) return -1;
            return 0;
        }

        void Record(int levelMs)
        {
            levels[levelNext] = levelMs;
            levelNext = (levelNext + 1) % WindowTicks;
            if (levelCount < WindowTicks) levelCount++;
        }

        void Append(float[] x, int n)
        {
            if (fifoCount + n > fifo.Length) n = fifo.Length - fifoCount; // can't happen: at most 3 frames go in
            Array.Copy(x, 0, fifo, fifoCount, n);
            fifoCount += n;
        }
    }
}
