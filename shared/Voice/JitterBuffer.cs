#nullable disable
// Revival voice receive buffer, one per speaker: reorders by seq, adapts its delay to measured arrival jitter, and
// fills holes from the redundant copy, then Opus FEC, then Opus PLC (fading out after 100 ms with nothing coming).
// Seqs below a held BurstStart were never sent (the speaker's gate was closed): silence, not loss.
// Push runs on the main thread, Pop on the playout thread; one lock guards everything, decoder calls included.
// Compiled by the mod (net35) and the voice tests (net8): plain arrays and collections only.
using System;
using System.Collections.Generic;

namespace StbcRevival.Voice
{
    public enum FrameSource { Normal, Redundant, Fec, Concealed }

    public sealed class JitterStats
    {
        // Counters, reset by TakeStats.
        // Concealed: frames concealed because a later packet proved them missing (a hole, or a mid-burst underrun).
        // TailConcealed: the fade-out frames after the speaker stopped (or ahead of a new burst): not a fault.
        public int Received, Late, Duplicates, Lost, Redundant, Fec, Concealed, TailConcealed, Underruns;
        // Gauges.
        public int TargetMs, DepthMs, JitterMs, LossPercent;
    }

    public sealed class JitterBuffer
    {
        public const int InitialTargetMs = 60, MinTargetMs = 40, MaxTargetMs = 300, TargetStepMs = 20;
        public const int MaxConcealed = 5;     // frames of PLC in a row with nothing later held before fading out
        const int TransitWindow = 100;
        const int MaxHeld = 250;               // 5 s of audio: a flood guard a real speaker never reaches

        sealed class Frame
        {
            public uint Seq;
            public bool BurstStart;
            public byte[] Opus = new byte[256];
            public int OpusLength;
            public byte[] Prev = new byte[256];
            public int PrevLength;
        }

        readonly object sync = new object();
        readonly IFrameDecoder decoder;
        readonly Dictionary<uint, Frame> held = new Dictionary<uint, Frame>();
        readonly Stack<Frame> pool = new Stack<Frame>();
        readonly List<uint> keys = new List<uint>();

        bool hasStream;
        uint stream;
        bool playing;
        bool hasPlayed;        // nextSeq is meaningful: every seq below it has had its playout tick
        uint nextSeq;
        uint highest;          // highest held seq, valid while held is non-empty
        int concealRun;        // Conceal calls in a row
        int tailPending;       // frames concealed in a row with nothing later held, not yet known as underrun or silence
        bool dry;              // the latest ticks were concealed with nothing held, from dryFrom on, and nothing has played since
        uint dryFrom;

        readonly double[] transits = new double[TransitWindow];
        readonly double[] sortScratch = new double[TransitWindow];
        int transitCount, transitNext;
        bool hasBatch;         // the newest transit came from a Push at batchMs
        double batchMs;

        int targetMs = InitialTargetMs, depthMs, jitterMs;
        int received, late, duplicates, lost, redundant, fec, concealed, tailConcealed, underruns;

        public JitterBuffer(IFrameDecoder decoder)
        {
            if (decoder == null) throw new ArgumentNullException("decoder");
            this.decoder = decoder;
        }

        public bool Playing { get { lock (sync) return playing; } }

        // Gauge: held audio ahead of the play position after the last Pop.
        public int DepthMs { get { lock (sync) return depthMs; } }

        // The delay it aims for: picked at each burst start, raised by an underrun.
        public int TargetMs { get { lock (sync) return targetMs; } }

        // Thread-safe. The packet's buffers are copied: the caller reuses them.
        public void Push(VoicePacketIn packet, double arrivalMs)
        {
            if (packet == null) return;
            lock (sync)
            {
                if (!hasStream || packet.Stream != stream)
                {
                    ResetStream();
                    hasStream = true;
                    stream = packet.Stream;
                }

                uint seq = packet.Seq;

                // A new burst whose start we already ticked past while dry (its pre-roll batch landed just after its
                // tick): those ticks were the speaker's silence. Start the new burst here instead of dropping its
                // BurstStart as late and calling the dry run an underrun.
                if (packet.BurstStart && dry && hasPlayed && seq < nextSeq && seq >= dryFrom)
                {
                    ResolveTail(false);
                    playing = false;
                    concealRun = 0;
                    dry = false;
                    nextSeq = seq;
                }

                if (hasPlayed && seq < nextSeq)
                {
                    late++;
                    AddTransit(arrivalMs, seq);   // late arrivals are exactly what the jitter estimate must see
                    return;
                }
                if (held.ContainsKey(seq)) { duplicates++; return; }
                if (held.Count >= MaxHeld) return;

                AddTransit(arrivalMs, seq);
                received++;

                // A new burst while not playing: stragglers from before it would only stretch the start into a gap.
                if (!playing && packet.BurstStart) DropBelow(seq);

                Frame f = pool.Count > 0 ? pool.Pop() : new Frame();
                f.Seq = seq;
                f.BurstStart = packet.BurstStart;
                f.OpusLength = Copy(packet.Opus, packet.OpusLength, ref f.Opus);
                f.PrevLength = Copy(packet.Prev, packet.PrevLength, ref f.Prev);
                if (held.Count == 0 || seq > highest) highest = seq;
                held[seq] = f;

                if (!playing) TryStart(true);
            }
        }

        // One 20 ms playout tick. False means nothing to play (pcm is cleared).
        public bool Pop(float[] pcm, out FrameSource source)
        {
            source = FrameSource.Concealed;
            lock (sync)
            {
                if (!playing) TryStart(false);
                if (!playing)
                {
                    Array.Clear(pcm, 0, pcm.Length);
                    return false;
                }

                bool ok = PlayNext(pcm, out source);
                depthMs = held.Count == 0 ? 0 : (int)(highest - nextSeq + 1) * VoiceFormat.FrameMs;
                if (!ok) Array.Clear(pcm, 0, pcm.Length);
                return ok;
            }
        }

        // Counters reset, gauges kept.
        public JitterStats TakeStats()
        {
            lock (sync)
            {
                jitterMs = ComputeJitterMs(false);
                int seen = lost + received;
                var s = new JitterStats
                {
                    Received = received, Late = late, Duplicates = duplicates, Lost = lost,
                    Redundant = redundant, Fec = fec, Concealed = concealed, TailConcealed = tailConcealed, Underruns = underruns,
                    TargetMs = targetMs, DepthMs = depthMs, JitterMs = jitterMs,
                    LossPercent = seen == 0 ? 0 : (lost * 100 + seen / 2) / seen,
                };
                received = late = duplicates = lost = redundant = fec = concealed = tailConcealed = underruns = 0;
                return s;
            }
        }

        // ---- everything below runs under the lock ----

        bool PlayNext(float[] pcm, out FrameSource source)
        {
            uint seq = nextSeq;
            Frame f, after;
            int n;

            // Something arrived after running dry: it says whether the dry run was an underrun or silence.
            // (Held seqs are never below seq.) The dry frames sit below the lowest held one.
            if (tailPending > 0 && held.Count > 0)
            {
                Frame next = Lowest();
                ResolveTail(!next.BurstStart && next.Seq - seq <= BoundaryFrames(targetMs));
            }

            if (held.TryGetValue(seq, out f))
            {
                // 1. The frame itself.
                held.Remove(seq);
                n = decoder.Decode(f.Opus, f.OpusLength, pcm, false);
                pool.Push(f);
                if (n > 0) return Played(FrameSource.Normal, out source);
            }
            else if (held.Count > 0)
            {
                // Seq counts mic time, so a new burst lands far ahead: the gap before it is silence, not loss.
                // Every seq below a held BurstStart was never sent; so is a gap too wide to be a hole (the
                // BurstStart packet may be lost). End this burst and start the new one by the usual rule
                // instead of concealing across the gap or counting it lost.
                Frame low = Lowest();
                if (IsSilenceBefore(low, seq))
                {
                    playing = false;
                    TryStart(false);
                    if (!playing) { source = FrameSource.Concealed; return false; }
                    return PlayNext(pcm, out source);
                }

                lost++;   // a later packet of this burst proves this one was sent
                if (held.TryGetValue(seq + 1, out after))
                {
                    if (after.PrevLength > 0)
                    {
                        // 2. Its redundant copy, carried by the next packet.
                        n = decoder.Decode(after.Prev, after.PrevLength, pcm, false);
                        if (n > 0) { redundant++; return Played(FrameSource.Redundant, out source); }
                    }
                    // 3. Opus FEC from the next packet (no redundant copy, or it didn't decode).
                    n = decoder.Decode(after.Opus, after.OpusLength, pcm, true);
                    if (n > 0) { fec++; return Played(FrameSource.Fec, out source); }
                }
                else if (held.TryGetValue(seq + 2, out after) && after.PrevLength > 0)
                {
                    // 3. This one and the next both missing: the packet after carries the next one whole, and
                    // that frame's Opus FEC is this one.
                    n = decoder.Decode(after.Prev, after.PrevLength, pcm, true);
                    if (n > 0) { fec++; return Played(FrameSource.Fec, out source); }
                }
            }

            // 4/5. Conceal. Nothing later held means we ran dry: the speaker stopped, or the network stalled.
            // Which one is only known when something arrives (ResolveTail); until then it is a tail.
            // A hole (something later is here) is skipped; a frame we ran dry waiting for is still waited for, as
            // NetEq does: concealing inserts time instead of using the frame's up, so when the late frame comes it
            // plays, a little later than planned, and the playout speeds back up to the target (Playout). Skipping
            // it instead made every packet behind a stall late too, dropped until the burst broke off and started
            // over: a 60 ms stall cost 200 ms and more of speech.
            source = FrameSource.Concealed;
            bool anyLater = held.Count > 0;
            n = decoder.Conceal(pcm);
            if (n < 0) n = 0;
            if (anyLater)
            {
                concealed++;
                nextSeq = seq + 1;
            }
            else
            {
                if (!dry) { dry = true; dryFrom = seq; }
                tailPending++;
            }
            concealRun++;

            if (!anyLater && concealRun >= MaxConcealed)
            {
                // Fade this last concealed frame linearly to zero by its end and stop, rather than buzz.
                for (int i = 0; i < n; i++) pcm[i] *= 1f - (float)(i + 1) / n;
                playing = false;
                concealRun = 0;
                ResolveTail(false);   // the burst ended; not a mid-burst underrun, so the target doesn't grow
            }
            return true;
        }

        bool Played(FrameSource src, out FrameSource source)
        {
            source = src;
            dry = false;
            nextSeq++;
            concealRun = 0;
            return true;
        }

        // The frames below this held one, from seq up, were never sent: it starts a new burst, or is too far
        // ahead to be the same one.
        bool IsSilenceBefore(Frame low, uint seq)
        {
            return low.Seq > seq && (low.BurstStart || low.Seq - seq > BoundaryFrames(targetMs));
        }

        // The frames concealed while dry were missing from a burst still going (an underrun: the cushion was too
        // thin, so the target grows), or the speaker's silence (a tail: nothing wrong).
        void ResolveTail(bool underrun)
        {
            if (tailPending == 0) return;
            if (underrun)
            {
                dry = false;
                underruns++;
                concealed += tailPending;
                targetMs = Math.Min(MaxTargetMs, targetMs + TargetStepMs);
            }
            else tailConcealed += tailPending;
            tailPending = 0;
        }

        // From Push, the batch this packet belongs to may still be arriving: its transit is the newest so far, not the
        // smallest (the newest seq) it will end with, so that start leaves it out.
        void TryStart(bool batchOpen)
        {
            if (held.Count == 0) return;

            // Adapt at each start, where a depth change can't warp speech; shrink at most one step per start.
            // The candidate is tested against the span now, so a link that got jittery is covered at the very
            // next burst; it is only committed when the start happens, so idle Pushes don't ratchet the shrink.
            int j = ComputeJitterMs(batchOpen);
            int t = Math.Max(MinTargetMs, Math.Min(MaxTargetMs, j + VoiceFormat.FrameMs));
            int cand = Math.Max(t, targetMs - TargetStepMs);

            DropBeforeBoundary(cand);
            Frame first = Lowest();
            if ((int)(highest - first.Seq + 1) * VoiceFormat.FrameMs < cand) return;

            jitterMs = j;
            targetMs = cand;
            nextSeq = first.Seq;
            hasPlayed = true;
            playing = true;
            concealRun = 0;
        }

        // A seq gap wider than the cushion plus the concealment limit can't be a hole to conceal: seq counts mic
        // time, so it is the silence before a new burst (whose BurstStart packet may have been lost).
        static uint BoundaryFrames(int target)
        {
            return (uint)(target / VoiceFormat.FrameMs + MaxConcealed);
        }

        // Drops held stragglers that sit before a burst boundary, so the start begins at the newest burst.
        void DropBeforeBoundary(int target)
        {
            if (held.Count < 2) return;
            keys.Clear();
            foreach (var kv in held) keys.Add(kv.Key);
            keys.Sort();
            uint limit = BoundaryFrames(target);
            int cut = 0;
            for (int i = 1; i < keys.Count; i++)
                if (keys[i] - keys[i - 1] > limit) cut = i;
            for (int i = 0; i < cut; i++)
            {
                pool.Push(held[keys[i]]);
                held.Remove(keys[i]);
            }
        }

        Frame Lowest()
        {
            Frame low = null;
            foreach (var kv in held)
                if (low == null || kv.Key < low.Seq) low = kv.Value;
            return low;
        }

        void DropBelow(uint seq)
        {
            List<uint> drop = null;
            foreach (var kv in held)
                if (kv.Key < seq) { if (drop == null) drop = new List<uint>(); drop.Add(kv.Key); }
            if (drop == null) return;
            for (int i = 0; i < drop.Count; i++)
            {
                pool.Push(held[drop[i]]);
                held.Remove(drop[i]);
            }
        }

        // A new stream: its seqs start afresh, so the held packets, play position and transit history go.
        // The adapted target and the report counters describe the peer's network and stay.
        void ResetStream()
        {
            foreach (var kv in held) pool.Push(kv.Value);
            held.Clear();
            playing = false;
            hasPlayed = false;
            nextSeq = 0;
            concealRun = 0;
            ResolveTail(false);
            dry = false;
            transitCount = transitNext = 0;
            hasBatch = false;
            depthMs = 0;
        }

        // One transit per arrival batch: packets pushed with the same arrival time came together (a hitch on either
        // side, or the speaker's pre-roll), and only the newest, least delayed of them says how late the network is.
        void AddTransit(double arrivalMs, uint seq)
        {
            double transit = arrivalMs - (double)seq * VoiceFormat.FrameMs;
            if (hasBatch && arrivalMs == batchMs && transitCount > 0)
            {
                int last = (transitNext + TransitWindow - 1) % TransitWindow;
                if (transit < transits[last]) transits[last] = transit;
                return;
            }
            hasBatch = true;
            batchMs = arrivalMs;
            transits[transitNext] = transit;
            transitNext = (transitNext + 1) % TransitWindow;
            if (transitCount < TransitWindow) transitCount++;
        }

        // p95 of (transit - min transit) over the last 100 arrival batches; skipNewest leaves out the newest batch.
        int ComputeJitterMs(bool skipNewest)
        {
            int skip = skipNewest && transitCount > 1 ? (transitNext + TransitWindow - 1) % TransitWindow : -1;
            int c = 0;
            for (int i = 0; i < transitCount; i++) if (i != skip) sortScratch[c++] = transits[i];
            if (c == 0) return 0;
            double min = double.MaxValue;
            for (int i = 0; i < c; i++) if (sortScratch[i] < min) min = sortScratch[i];
            for (int i = 0; i < c; i++) sortScratch[i] -= min;
            Array.Sort(sortScratch, 0, c);
            int k = (int)Math.Ceiling(0.95 * c) - 1;
            if (k < 0) k = 0;
            return (int)Math.Round(sortScratch[k]);
        }

        static int Copy(byte[] src, int length, ref byte[] dest)
        {
            if (src == null || length <= 0) return 0;
            if (length > src.Length) length = src.Length;
            if (dest.Length < length) dest = new byte[length];
            Buffer.BlockCopy(src, 0, dest, 0, length);
            return length;
        }
    }
}
