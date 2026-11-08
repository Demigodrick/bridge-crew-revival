namespace Voice.Tests;

public class ProcessingTests
{
    static float Rms(float[] a, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++) s += a[i] * a[i];
        return (float)Math.Sqrt(s / (to - from));
    }

    static float Db(float lin) => 20f * (float)Math.Log10(lin);

    [Fact]
    public void ResamplerUp24kTo48kKeepsTone()
    {
        const int n = 24000;
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 3000 * i / 24000.0);
        var r = new Resampler(24000, 48000);
        var output = new float[n * 2 + 64];
        int written = 0;
        // Feed in odd-sized chunks to exercise the streaming state.
        for (int pos = 0; pos < n;)
        {
            int c = Math.Min(480 + (pos % 7), n - pos);
            var chunk = new float[c];
            Array.Copy(input, pos, chunk, 0, c);
            var o = new float[c * 2 + 64];
            int w = r.Process(chunk, c, o);
            Array.Copy(o, 0, output, written, w);
            written += w;
            pos += c;
        }
        Assert.InRange(written, n * 2 - 64, n * 2 + 64);
        float expected = 0.5f / (float)Math.Sqrt(2);
        float rms = Rms(output, 1000, written - 1000);
        Assert.InRange(rms, expected * 0.95f, expected * 1.05f);
        // Continuity: no sample-to-sample jump beyond what a 3 kHz tone at 48 kHz can do.
        float maxStep = 0;
        for (int i = 1000; i < written - 1000; i++) maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 1]));
        Assert.True(maxStep < 0.5f * 2 * Math.PI * 3000 / 48000 * 1.1, "click: " + maxStep);
    }

    [Fact]
    public void ResamplerSameRateIsCopy()
    {
        var input = new float[100];
        for (int i = 0; i < input.Length; i++) input[i] = i * 0.01f;
        var output = new float[200];
        int w = new Resampler(48000, 48000).Process(input, 100, output);
        Assert.Equal(100, w);
        for (int i = 0; i < 100; i++) Assert.Equal(input[i], output[i]);
    }

    static float[] Tone(int count, float rmsDb)
    {
        var f = new float[count];
        float amp = (float)Math.Pow(10, rmsDb / 20) * (float)Math.Sqrt(2);
        for (int i = 0; i < count; i++) f[i] = amp * (float)Math.Sin(2 * Math.PI * 440 * i / 48000.0);
        return f;
    }

    [Fact]
    public void AgcBringsQuietSpeechUp()
    {
        var agc = new Agc();
        var tone = Tone(960, -40f);
        float[] frame = new float[960];
        for (int i = 0; i < 250; i++) // 5 s
        {
            frame = (float[])tone.Clone();
            agc.Process(frame, 0, 960, true);
        }
        float db = Db(Rms(frame, 0, 960));
        Assert.InRange(db, -25f, -19f);
    }

    [Fact]
    public void AgcDoesNotPumpNoise()
    {
        var agc = new Agc();
        var rng = new Random(1);
        for (int i = 0; i < 250; i++)
        {
            var f = new float[960];
            for (int j = 0; j < f.Length; j++) f[j] = (float)(rng.NextDouble() * 2 - 1) * 0.00316f * 1.7f;
            agc.Process(f, 0, 960, false);
        }
        Assert.Equal(0f, agc.GainDb);
    }

    [Fact]
    public void AgcNeverClips()
    {
        var agc = new Agc();
        var tone = Tone(960, -40f);
        for (int i = 0; i < 250; i++) agc.Process((float[])tone.Clone(), 0, 960, true);
        Assert.True(agc.GainDb > 17f);
        for (int k = 0; k < 5; k++)
        {
            var sq = new float[960];
            for (int i = 0; i < sq.Length; i++) sq[i] = ((i / 24) % 2 == 0) ? 1f : -1f;
            agc.Process(sq, 0, 960, true);
            foreach (var x in sq) Assert.True(Math.Abs(x) <= 0.98f + 1e-6f);
        }
    }

    [Fact]
    public void AgcLimiterIsSmoothAcrossFrames()
    {
        var agc = new Agc();
        var quiet = Tone(960, -40f);
        for (int i = 0; i < 250; i++) agc.Process((float[])quiet.Clone(), 0, 960, true);
        // A smooth 50 Hz tone whose level decays from 0.5 to about 0.03: the limiter has to let go.
        var all = new float[960 * 12];
        for (int i = 0; i < all.Length; i++)
            all[i] = 0.5f * Math.Min(1f, i / 960f) * (float)Math.Exp(-i / 4000.0) * (float)Math.Sin(2 * Math.PI * 50 * i / 48000.0 + Math.PI / 2);
        for (int f = 0; f < 12; f++) agc.Process(all, f * 960, 960, true);
        float maxStep = 0;
        for (int i = 1; i < all.Length; i++)
        {
            Assert.True(Math.Abs(all[i]) <= 0.98f + 1e-6f);
            maxStep = Math.Max(maxStep, Math.Abs(all[i] - all[i - 1]));
        }
        Assert.True(maxStep < 0.05f, "step " + maxStep);
    }

    [Fact]
    public void GateOpensWithPreRoll()
    {
        var g = new SpeechGate();
        bool b;
        Assert.Equal(0, g.Step(0.1f, out b)); Assert.False(b);
        Assert.Equal(0, g.Step(0.1f, out b));
        Assert.Equal(0, g.Step(0.1f, out b));
        Assert.Equal(4, g.Step(0.9f, out b));
        Assert.True(b);
        Assert.True(g.Open);
    }

    static SpeechGate Opened()
    {
        var g = new SpeechGate();
        g.Step(0.9f, out _);
        return g;
    }

    [Fact]
    public void GateHangsThenCloses()
    {
        var g = Opened();
        for (int i = 0; i < 15; i++) Assert.Equal(1, g.Step(0.1f, out var b)); 
        Assert.Equal(0, g.Step(0.1f, out _));
        Assert.False(g.Open);
    }

    [Fact]
    public void GateHoldResetsHang()
    {
        var g = Opened();
        for (int i = 0; i < 10; i++) Assert.Equal(1, g.Step(0.1f, out _));
        Assert.Equal(1, g.Step(0.4f, out var b));
        Assert.False(b);
        for (int i = 0; i < 15; i++) Assert.Equal(1, g.Step(0.1f, out _));
        Assert.Equal(0, g.Step(0.1f, out _));
    }

    [Fact]
    public void EnergyVadThreshold()
    {
        Assert.Equal(1f, EnergyVad.Probability(Tone(960, -20f), 0, 960));
        Assert.Equal(0f, EnergyVad.Probability(Tone(960, -50f), 0, 960));
    }
}
