namespace Voice.Tests;

public class StretchTests
{
    const int Rate = 24000, Frame = Rate * VoiceFormat.FrameMs / 1000; // 480: the game's voice rate

    static float[] Sine(int n, double hz, double amp = 0.3, int start = 0)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * (start + i) / Rate));
        return x;
    }

    // Largest jump between neighbouring samples: a cut in the wrong place shows up as a click.
    static float MaxStep(float[] x, int n)
    {
        float m = 0;
        for (int i = 1; i < n; i++) m = Math.Max(m, Math.Abs(x[i] - x[i - 1]));
        return m;
    }

    [Fact]
    public void Compress_cuts_whole_periods_of_voiced_speech_without_a_click()
    {
        var x = Sine(2 * Frame, 200);           // a 200 Hz voice: a 120-sample period
        var y = new float[3 * Frame];
        int cut = TimeStretch.Compress(x, x.Length, Rate, y);
        Assert.True(cut > 0);
        Assert.Equal(0, cut % 120 < 3 || cut % 120 > 117 ? 0 : cut % 120);
        Assert.True(MaxStep(y, x.Length - cut) <= MaxStep(x, x.Length) * 1.1f);
    }

    [Fact]
    public void Expand_repeats_a_period_without_a_click()
    {
        var x = Sine(Frame, 150);               // a deep voice: 160 samples
        var y = new float[3 * Frame];
        int added = TimeStretch.Expand(x, x.Length, Rate, y);
        Assert.True(added > 0);
        Assert.True(MaxStep(y, x.Length + added) <= MaxStep(x, x.Length) * 1.1f);
    }

    [Fact]
    public void Loud_noise_is_left_alone()
    {
        var rng = new Random(3);
        var x = new float[2 * Frame];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() - 0.5);
        var y = new float[3 * Frame];
        Assert.Equal(0, TimeStretch.Compress(x, x.Length, Rate, y));
        Assert.Equal(x, y.Take(x.Length).ToArray());
    }

    [Fact]
    public void Quiet_audio_is_cut_as_much_as_fits()
    {
        var x = Sine(2 * Frame, 200, 0.001);
        var y = new float[3 * Frame];
        Assert.Equal(Rate * 15 / 1000, TimeStretch.Compress(x, x.Length, Rate, y));
    }

    // One continuous 200 Hz tone, frame by frame: what the speaker said, whatever order it decodes in.
    sealed class ToneDecoder : IFrameDecoder
    {
        public int Decode(byte[] packet, int length, float[] pcm, bool fec)
        {
            int seq = BitConverter.ToInt32(packet, 0);
            var x = Sine(Frame, 200, 0.3, seq * Frame);
            Array.Copy(x, pcm, Frame);
            return Frame;
        }

        public int Conceal(float[] pcm)
        {
            Array.Clear(pcm, 0, Frame);
            return Frame;
        }
    }

    static readonly VoicePacketIn Packet = new VoicePacketIn { Opus = new byte[8], Prev = new byte[8] };

    static void Push(JitterBuffer jb, uint seq, double at)
    {
        Packet.Stream = 1;
        Packet.Seq = seq;
        Packet.BurstStart = seq == 0;
        BitConverter.GetBytes((int)seq).CopyTo(Packet.Opus, 0);
        Packet.OpusLength = 4;
        Packet.PrevLength = 0;
        jb.Push(Packet, at);
    }

    // Plays `ticks` ticks of a speaker talking on; `arrives(seq)` is when each packet gets here (ms).
    static (int sped, int slowed, int finalLevel) Run(int ticks, Func<uint, double> arrives) => Run(ticks, arrives, out _);

    static (int sped, int slowed, int finalLevel) Run(int ticks, Func<uint, double> arrives, out JitterStats stats)
    {
        var jb = new JitterBuffer(new ToneDecoder());
        var playout = new Playout(jb, Frame, Rate);
        var pcm = new float[Frame];
        uint next = 0;
        int sped = 0, slowed = 0;
        for (int t = 0; t < ticks; t++)
        {
            double now = t * VoiceFormat.FrameMs;
            while (arrives(next) <= now) Push(jb, next, arrives(next++));
            playout.Next(pcm, 0);
            playout.TakeStretched(out int s, out int w);
            sped += s;
            slowed += w;
        }
        stats = jb.TakeStats();
        return (sped, slowed, jb.DepthMs + playout.HeldMs);
    }

    [Fact]
    public void A_steady_connection_is_never_stretched()
    {
        var (sped, slowed, _) = Run(500, seq => 30 + seq * 20.0);
        Assert.Equal(0, sped);
        Assert.Equal(0, slowed);
    }

    [Fact]
    public void A_backlog_mid_sentence_is_played_off_by_speeding_up()
    {
        // The first 15 frames arrive together after a hold-up, then on time: 300 ms buffered against a 60 ms target.
        var (sped, slowed, level) = Run(500, seq => seq < 15 ? 300 : 300 + (seq - 14) * 20.0);
        Assert.True(sped >= 150, $"sped up {sped} ms");
        Assert.Equal(0, slowed);
        Assert.True(level <= 60 + 2 * VoiceFormat.FrameMs, $"still {level} ms buffered");
    }

    [Fact]
    public void Speeding_up_is_at_most_about_a_quarter()
    {
        var (sped, _, _) = Run(100, seq => seq < 100 ? 0 : 1e9); // two seconds' worth at once
        Assert.True(sped <= 100 * VoiceFormat.FrameMs / StretchEveryFraction, $"sped up {sped} ms in 2 s");
    }

    [Fact]
    public void After_an_underrun_mid_sentence_the_cushion_is_rebuilt_by_slowing_down()
    {
        // On time, then a 60 ms stall: the buffer runs dry, its target grows, and the speaker goes on talking.
        var (sped, slowed, level) = Run(400, seq => 30 + seq * 20.0 + (seq >= 100 ? 60 : 0));
        Assert.True(slowed > 0, "never slowed down");
        Assert.True(level >= 30, $"only {level} ms buffered"); // the new target (60) less a frame, within half a frame
    }

    [Fact]
    public void A_short_stall_mid_sentence_loses_no_speech()
    {
        // Every packet after a 60 ms stall is played, not dropped as late until the sentence breaks off.
        Run(400, seq => 30 + seq * 20.0 + (seq >= 100 ? 60 : 0), out var st);
        Assert.Equal(0, st.Late);
        Assert.Equal(0, st.Lost);
        Assert.Equal(1, st.Underruns);
        Assert.Equal(0, st.TailConcealed); // the burst didn't break off
    }

    const int StretchEveryFraction = 3; // one cut of at most 15 ms every 3 ticks (60 ms)
}
