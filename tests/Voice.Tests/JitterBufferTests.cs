namespace Voice.Tests;

public class JitterBufferTests
{
    // Decoded seq N is filled with N, FEC from packet N with -(N-1), concealment with 0.5.
    // The packet bytes carry their seq so the fake can tell which frame it is decoding.
    sealed class FakeDecoder : IFrameDecoder
    {
        public readonly HashSet<int> FailNormal = new HashSet<int>();   // these seqs don't decode (fec=false)

        public int Decode(byte[] packet, int length, float[] pcm, bool fec)
        {
            int seq = BitConverter.ToInt32(packet, 0);
            if (!fec && FailNormal.Contains(seq)) return -4;   // OPUS_INVALID_PACKET
            float v = fec ? -(seq - 1) : seq;
            for (int i = 0; i < VoiceFormat.FrameSamples; i++) pcm[i] = v;
            return VoiceFormat.FrameSamples;
        }

        public int Conceal(float[] pcm)
        {
            for (int i = 0; i < VoiceFormat.FrameSamples; i++) pcm[i] = 0.5f;
            return VoiceFormat.FrameSamples;
        }
    }

    // One reused packet object, as the receive path does: the buffer must copy what it keeps.
    static readonly VoicePacketIn Shared = new VoicePacketIn { Opus = new byte[64], Prev = new byte[64] };

    static void Push(JitterBuffer jb, uint seq, double arrivalMs, bool burstStart = false, bool prev = true, uint stream = 1)
    {
        var p = Shared;
        p.Stream = stream;
        p.Seq = seq;
        p.BurstStart = burstStart;
        BitConverter.GetBytes((int)seq).CopyTo(p.Opus, 0);
        p.OpusLength = 4;
        if (prev && seq > 0)
        {
            BitConverter.GetBytes((int)seq - 1).CopyTo(p.Prev, 0);
            p.PrevLength = 4;
        }
        else p.PrevLength = 0;
        jb.Push(p, arrivalMs);
        // Scribble over the shared buffers so a buffer that kept a reference would decode garbage.
        Array.Clear(p.Opus, 0, p.Opus.Length);
        Array.Clear(p.Prev, 0, p.Prev.Length);
    }

    static readonly float[] Pcm = new float[VoiceFormat.FrameSamples];

    static bool Pop(JitterBuffer jb, out FrameSource src, out float value)
    {
        Array.Clear(Pcm, 0, Pcm.Length);
        bool ok = jb.Pop(Pcm, out src);
        value = Pcm[0];
        return ok;
    }

    static List<(FrameSource src, float value)> PopUntilStop(JitterBuffer jb, int max = 1000)
    {
        var list = new List<(FrameSource, float)>();
        for (int i = 0; i < max; i++)
        {
            if (!Pop(jb, out var s, out var v)) break;
            list.Add((s, v));
        }
        return list;
    }

    [Fact]
    public void PlaysInOrder()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        Assert.False(jb.Playing);
        for (uint s = 0; s < 10; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        Assert.True(jb.Playing);
        var frames = PopUntilStop(jb);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(FrameSource.Normal, frames[i].src);
            Assert.Equal((float)i, frames[i].value);
        }
        var st = jb.TakeStats();
        Assert.Equal(10, st.Received);
        Assert.Equal(0, st.Lost);
    }

    [Fact]
    public void WaitsForTargetBeforePlaying()
    {
        // Steady arrivals: the start target adapts to max(clamp(0 + 20, 40, 300), 60 - 20) = 40 ms, two frames.
        var jb = new JitterBuffer(new FakeDecoder());
        Push(jb, 0, 0, burstStart: true);
        Assert.False(jb.Playing);
        Assert.False(jb.Pop(Pcm, out _));
        Push(jb, 1, 20);
        Assert.True(jb.Playing);
        Assert.Equal(40, jb.TakeStats().TargetMs);
        Assert.True(Pop(jb, out var s, out var v));
        Assert.Equal(FrameSource.Normal, s);
        Assert.Equal(0f, v);
    }

    [Fact]
    public void ReordersSwappedPackets()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        uint[] order = { 0, 1, 3, 2, 4, 6, 5, 7, 8, 9 };
        for (int i = 0; i < order.Length; i++) Push(jb, order[i], i * 20.0, burstStart: order[i] == 0);
        var frames = PopUntilStop(jb);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(FrameSource.Normal, frames[i].src);
            Assert.Equal((float)i, frames[i].value);
        }
        Assert.Equal(0, jb.TakeStats().Lost);
    }

    [Fact]
    public void RecoversFromRedundancy()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) if (s != 5) Push(jb, s, s * 20.0, burstStart: s == 0);
        var frames = PopUntilStop(jb);
        Assert.Equal(FrameSource.Redundant, frames[5].src);
        Assert.Equal(5f, frames[5].value);
        Assert.Equal(FrameSource.Normal, frames[6].src);
        Assert.Equal(6f, frames[6].value);
        var st = jb.TakeStats();
        Assert.Equal(1, st.Lost);
        Assert.Equal(1, st.Redundant);
        Assert.Equal(10, st.LossPercent);      // 1 lost of 1 + 9 received
    }

    [Fact]
    public void RecoversFromFecWhenNoPrev()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) if (s != 5) Push(jb, s, s * 20.0, burstStart: s == 0, prev: s != 6);
        var frames = PopUntilStop(jb);
        Assert.Equal(FrameSource.Fec, frames[5].src);
        Assert.Equal(-5f, frames[5].value);    // FEC carried by packet 6
        Assert.Equal(FrameSource.Normal, frames[6].src);
        Assert.Equal(6f, frames[6].value);
        var st = jb.TakeStats();
        Assert.Equal(1, st.Lost);
        Assert.Equal(1, st.Fec);
    }

    [Fact]
    public void ConcealsAcrossLongerLossWhenLaterPacketHeld()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        // 4..8 lost (5 frames; 9 carries 8 whole, and 8's FEC is 7): 4..6 concealed, 7 FEC, 8 redundant, then 9 on.
        for (uint s = 0; s < 14; s++) if (s < 4 || s > 8) Push(jb, s, s * 20.0, burstStart: s == 0);
        var frames = PopUntilStop(jb);
        for (int i = 4; i < 7; i++) Assert.Equal(FrameSource.Concealed, frames[i].src);
        Assert.Equal(FrameSource.Fec, frames[7].src);
        Assert.Equal(-7f, frames[7].value);
        Assert.Equal(FrameSource.Redundant, frames[8].src);
        Assert.Equal(9f, frames[9].value);
        Assert.Equal(13f, frames[13].value);
        var st = jb.TakeStats();
        Assert.Equal(5, st.Lost);
        Assert.Equal(3, st.Concealed);
        Assert.Equal(5, st.TailConcealed);     // the fade-out after 13
        Assert.Equal(0, st.Underruns);         // the hole had later packets held, and the end of the burst isn't one
    }

    [Fact]
    public void ConcealsThenFadesAndStops()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        for (int i = 0; i < 10; i++) Assert.True(Pop(jb, out _, out _));
        float[] pcm = new float[VoiceFormat.FrameSamples];
        for (int i = 0; i < 5; i++)
        {
            Assert.True(jb.Pop(pcm, out var src));
            Assert.Equal(FrameSource.Concealed, src);
            if (i < 4)
            {
                Assert.True(jb.Playing);
                Assert.Equal(0.5f, pcm[VoiceFormat.FrameSamples - 1]);
            }
        }
        // The 5th fades linearly to zero by its end.
        Assert.InRange(pcm[0], 0.49f, 0.5f);
        Assert.InRange(pcm[VoiceFormat.FrameSamples / 2], 0.24f, 0.26f);
        Assert.Equal(0f, pcm[VoiceFormat.FrameSamples - 1]);
        Assert.False(jb.Playing);
        Assert.False(jb.Pop(pcm, out _));
        // The speaker stopped: a tail, not a fault.
        var st = jb.TakeStats();
        Assert.Equal(0, st.Underruns);
        Assert.Equal(0, st.Concealed);
        Assert.Equal(5, st.TailConcealed);
        Assert.Equal(0, st.Lost);
    }

    [Fact]
    public void DropsLateAndDuplicate()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 8; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        Push(jb, 6, 150);                       // held twice
        for (int i = 0; i < 4; i++) Assert.True(Pop(jb, out _, out _));   // 0..3 played
        Push(jb, 3, 160);                       // after 3 played
        var frames = PopUntilStop(jb);
        Assert.Equal(4f, frames[0].value);
        Assert.Equal(7f, frames[3].value);
        var st = jb.TakeStats();
        Assert.Equal(1, st.Late);
        Assert.Equal(1, st.Duplicates);
        Assert.Equal(8, st.Received);
    }

    [Fact]
    public void NewStreamResets()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 100; s < 110; s++) Push(jb, s, s * 20.0, burstStart: s == 100, stream: 1);
        for (int i = 0; i < 3; i++) Assert.True(Pop(jb, out _, out _));
        Assert.True(jb.Playing);
        double t = 2300;
        for (uint s = 0; s < 6; s++) Push(jb, s, t + s * 20, burstStart: s == 0, stream: 2);
        var frames = PopUntilStop(jb);
        Assert.Equal(6 + 5, frames.Count);      // then the 5 concealed frames of the fade-out
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(FrameSource.Normal, frames[i].src);
            Assert.Equal((float)i, frames[i].value);
        }
    }

    [Fact]
    public void NewBurstAfterSilenceIsNotLoss()
    {
        // Seq counts mic time, so a new burst arrives far ahead. If it lands while the old burst is still
        // being played out, the gap between them is silence, not lost frames to conceal.
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 5; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        for (int i = 0; i < 3; i++) Assert.True(Pop(jb, out _, out _));
        for (uint s = 50; s < 55; s++) Push(jb, s, s * 20.0, burstStart: s == 50);
        var played = new List<float>();
        for (int i = 0; i < 20; i++)
        {
            if (!Pop(jb, out var src, out var v)) continue;
            if (src != FrameSource.Normal) break;
            played.Add(v);
        }
        Assert.Equal(new float[] { 3, 4, 50, 51, 52, 53, 54 }, played);
        Assert.Equal(0, jb.TakeStats().Lost);
    }

    [Fact]
    public void StragglerBeforeLostBurstStartIsDropped()
    {
        // A burst ends by fade-out; a straggler of it stays held while idle; the next burst's BurstStart is lost.
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        Assert.Equal(15, PopUntilStop(jb).Count);   // 10 played + 5 concealed, next seq 15
        Push(jb, 16, 16 * 20.0);                    // straggler, too short to start on its own
        Assert.False(jb.Playing);
        Assert.False(jb.Pop(Pcm, out _));
        jb.TakeStats();
        for (uint s = 117; s < 120; s++) Push(jb, s, s * 20.0);   // 116 (BurstStart) lost
        Assert.True(Pop(jb, out var src, out var v));
        Assert.Equal(FrameSource.Normal, src);
        Assert.Equal(117f, v);
        Assert.Equal(0, jb.TakeStats().Lost);
    }

    [Fact]
    public void FarAheadPacketWithoutBurstStartEndsTheBurst()
    {
        // While the old burst is in its concealment tail, a new burst arrives with its BurstStart lost.
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        for (int i = 0; i < 10; i++) Assert.True(Pop(jb, out _, out _));
        for (int i = 0; i < 2; i++) { Assert.True(Pop(jb, out var c, out _)); Assert.Equal(FrameSource.Concealed, c); }
        jb.TakeStats();
        for (uint s = 61; s < 66; s++) Push(jb, s, s * 20.0);    // 60 (BurstStart) lost
        Assert.True(Pop(jb, out var src, out var v));
        Assert.Equal(FrameSource.Normal, src);
        Assert.Equal(61f, v);
        var st = jb.TakeStats();
        Assert.Equal(0, st.Lost);
        Assert.Equal(0, st.Concealed);
    }

    // Drives the buffer as the game would: packets at their arrival times, a Pop every 20 ms.
    static double Run(JitterBuffer jb, List<(uint seq, double at, bool start)> packets, double clock, double until)
    {
        packets.Sort((a, b) => a.at.CompareTo(b.at));
        int k = 0;
        for (; clock < until; clock += 20)
        {
            while (k < packets.Count && packets[k].at <= clock)
            {
                Push(jb, packets[k].seq, packets[k].at, packets[k].start);
                k++;
            }
            jb.Pop(Pcm, out _);
        }
        return clock;
    }

    static List<(uint, double, bool)> Burst(uint first, int count, double t0, Random? rng, double jitter)
    {
        var list = new List<(uint, double, bool)>();
        for (int i = 0; i < count; i++)
        {
            double j = rng == null ? 0 : (rng.NextDouble() * 2 - 1) * jitter;
            list.Add(((uint)(first + i), t0 + i * 20 + 5 + j, i == 0));
        }
        return list;
    }

    [Fact]
    public void TargetAdaptsToJitter()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        var rng = new Random(1234);
        double clock = Run(jb, Burst(0, 200, 60, rng, 60), 0, 4400);
        Assert.False(jb.Playing);
        // The very next burst already waits for the adapted cushion, not the one the jittery burst started with.
        int frames = 0;
        for (uint s = 300; !jb.Playing; s++)
        {
            Assert.True(s < 320);
            Push(jb, s, s * 20.0 + 5, burstStart: s == 300);
            frames++;
        }
        int target = jb.TakeStats().TargetMs;
        Assert.InRange(target, 80, 160);
        Assert.True(frames * 20 >= target && (frames - 1) * 20 < target, $"started at {frames} frames, target {target}");
        clock = Run(jb, Burst(300 + (uint)frames, 20 - frames, 6000 + frames * 20, null, 0), clock, 6400);
        var st = jb.TakeStats();
        Assert.InRange(st.TargetMs, 80, 160);
        Assert.InRange(st.JitterMs, 90, 125);  // p95 of a 120 ms uniform spread
    }

    [Fact]
    public void TargetShrinksSlowly()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        var rng = new Random(99);
        double clock = Run(jb, Burst(0, 200, 60, rng, 60), 0, 4400);
        clock = Run(jb, Burst(300, 200, 6000, null, 0), clock, 10500);
        int before = jb.TakeStats().TargetMs;
        Assert.True(before >= 80);
        uint seq = 1000;
        int last = before;
        for (int b = 0; b < 3; b++)
        {
            double t0 = seq * 20.0;
            clock = Math.Max(clock, t0 - 100);
            clock = Run(jb, Burst(seq, 20, t0, null, 0), clock, t0 + 1000);
            int now = jb.TakeStats().TargetMs;
            Assert.InRange(last - now, 0, 20);
            last = now;
            seq += 100;
        }
        Assert.True(last < before);
    }

    [Fact]
    public void UnderrunGrowsTarget()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 6; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        int start = jb.TakeStats().TargetMs;
        for (int i = 0; i < 6; i++) Assert.True(Pop(jb, out _, out _));
        // 6 and 7 are late: two frames starve with nothing later held.
        for (int i = 0; i < 2; i++) { Assert.True(Pop(jb, out var s, out _)); Assert.Equal(FrameSource.Concealed, s); }
        Assert.True(jb.Playing);
        for (uint s = 6; s < 12; s++) Push(jb, s, 200 + s * 20.0);
        Assert.True(Pop(jb, out var src, out var v));
        Assert.Equal(FrameSource.Normal, src);
        Assert.Equal(8f, v);
        var st = jb.TakeStats();
        Assert.Equal(start + 20, st.TargetMs);
        Assert.Equal(1, st.Underruns);
        Assert.Equal(2, st.Concealed);         // proven missing by 8
        Assert.Equal(0, st.TailConcealed);
        Assert.Equal(2, st.Late);
    }

    [Fact]
    public void TwoLostRecoverFecFromPrevThenRedundant()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) if (s != 4 && s != 5) Push(jb, s, s * 20.0, burstStart: s == 0);
        var frames = PopUntilStop(jb);
        Assert.Equal(FrameSource.Fec, frames[4].src);
        Assert.Equal(-4f, frames[4].value);        // FEC of frame 5, carried whole by packet 6
        Assert.Equal(FrameSource.Redundant, frames[5].src);
        Assert.Equal(5f, frames[5].value);
        Assert.Equal(6f, frames[6].value);
        var st = jb.TakeStats();
        Assert.Equal(2, st.Lost);
        Assert.Equal(1, st.Fec);
        Assert.Equal(1, st.Redundant);
        Assert.Equal(0, st.Concealed);
    }

    [Fact]
    public void FailedRedundantFallsBackToFec()
    {
        var dec = new FakeDecoder();
        dec.FailNormal.Add(5);                     // packet 6's copy of frame 5 doesn't decode
        var jb = new JitterBuffer(dec);
        for (uint s = 0; s < 10; s++) if (s != 5) Push(jb, s, s * 20.0, burstStart: s == 0);
        var frames = PopUntilStop(jb);
        Assert.Equal(FrameSource.Fec, frames[5].src);
        Assert.Equal(-5f, frames[5].value);        // FEC from packet 6
        Assert.Equal(6f, frames[6].value);
        var st = jb.TakeStats();
        Assert.Equal(1, st.Lost);
        Assert.Equal(1, st.Fec);
        Assert.Equal(0, st.Redundant);
        Assert.Equal(0, st.Concealed);
    }

    // The speaker's gate closes after its hang and reopens a frame later: frame 20 was never sent, and the next
    // burst (pre-roll 21..23 with BurstStart 21, plus 24) leaves at 24 * 20 ms. Perfect network.
    [Fact]
    public void ShortPauseIsSilenceNotUnderrun()
    {
        var jb = new JitterBuffer(new FakeDecoder());
        var packets = new List<(uint seq, double at, bool start)>();
        for (uint s = 0; s <= 3; s++) packets.Add((s, 60, s == 0));            // the first burst's pre-roll
        for (uint s = 4; s <= 19; s++) packets.Add((s, s * 20.0, false));
        for (uint s = 21; s <= 24; s++) packets.Add((s, 24 * 20.0, s == 21));
        for (uint s = 25; s <= 60; s++) packets.Add((s, s * 20.0, false));
        Run(jb, packets.GetRange(0, 4), 0, 80);
        int before = jb.TakeStats().TargetMs;
        Run(jb, packets.GetRange(4, packets.Count - 4), 80, 1400);
        var st = jb.TakeStats();
        Assert.Equal(before, st.TargetMs);
        Assert.Equal(0, st.Underruns);
        Assert.Equal(0, st.Concealed);
        Assert.Equal(0, st.Lost);
        Assert.Equal(0, st.Fec);
    }

    [Fact]
    public void SilenceBeforeHeldBurstStartIsNotLoss()
    {
        // Frame 10 was never sent; the next burst's BurstStart (11) is already held when 10's tick comes.
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 10; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        for (int i = 0; i < 6; i++) Assert.True(Pop(jb, out _, out _));
        int before = jb.TakeStats().TargetMs;
        for (uint s = 11; s < 15; s++) Push(jb, s, 14 * 20.0, burstStart: s == 11);   // pre-roll, sent together
        var played = new List<float>();
        for (int i = 0; i < 8; i++)
        {
            Assert.True(Pop(jb, out var src, out var v));
            Assert.Equal(FrameSource.Normal, src);
            played.Add(v);
        }
        Assert.Equal(new float[] { 6, 7, 8, 9, 11, 12, 13, 14 }, played);
        var st = jb.TakeStats();
        Assert.Equal(0, st.Lost);
        Assert.Equal(0, st.Fec);
        Assert.Equal(0, st.Concealed);
        Assert.Equal(0, st.Underruns);
        Assert.Equal(before, st.TargetMs);
    }

    [Fact]
    public void HitchDoesNotInflateTarget()
    {
        // A steady link, then a 300 ms hold-up delivers 15 packets at once, then steady again.
        var jb = new JitterBuffer(new FakeDecoder());
        double clock = Run(jb, Burst(0, 100, 0, null, 0), 0, 2200);
        clock = Run(jb, Burst(200, 20, 4000, null, 0), Math.Max(clock, 3900), 4600);
        int before = jb.TakeStats().TargetMs;
        var hitch = Burst(300, 60, 6000, null, 0);
        for (int i = 10; i < 25; i++) hitch[i] = (hitch[i].Item1, 6000 + 25 * 20 + 5, false);
        clock = Run(jb, hitch, Math.Max(clock, 5900), 7400);
        // The next burst starts with the cushion it had before the hold-up.
        for (uint s = 400; !jb.Playing; s++)
        {
            Assert.True(s < 420);
            Push(jb, s, s * 20.0 + 5, burstStart: s == 400);
        }
        var st = jb.TakeStats();
        Assert.InRange(st.TargetMs, before - 20, before + 20);
        Assert.InRange(st.JitterMs, 0, 20);
    }

    [Fact]
    public void PreRollBatchIsNotJitter()
    {
        // Perfect link; every burst opens with its 4-frame pre-roll sent together.
        var jb = new JitterBuffer(new FakeDecoder());
        double clock = 0;
        for (uint b = 0; b < 10; b++)
        {
            uint first = b * 100;
            var list = new List<(uint, double, bool)>();
            for (uint i = 0; i < 10; i++)
            {
                uint s = first + i;
                double at = i < 4 ? (first + 3) * 20.0 + 5 : s * 20.0 + 5;
                list.Add((s, at, i == 0));
            }
            clock = Run(jb, list, Math.Max(clock, first * 20.0), first * 20.0 + 600);
        }
        var st = jb.TakeStats();
        Assert.Equal(0, st.JitterMs);
        Assert.Equal(JitterBuffer.MinTargetMs, st.TargetMs);
    }

    [Fact]
    public void CopiesWhatItKeeps()
    {
        // Push scribbles over the shared packet buffers after every call (see Push above); frames still decode.
        var jb = new JitterBuffer(new FakeDecoder());
        for (uint s = 0; s < 5; s++) Push(jb, s, s * 20.0, burstStart: s == 0);
        var frames = PopUntilStop(jb);
        Assert.Equal(new float[] { 0, 1, 2, 3, 4 }, frames.Take(5).Select(f => f.value));
    }
}
