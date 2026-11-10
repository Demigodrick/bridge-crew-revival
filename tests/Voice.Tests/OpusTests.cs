using StbcRevival.Voice;
using Xunit;

namespace Voice.Tests;

// xunit 2.4 has no dynamic skip: a missing libopus turns every test into a reported skip.
public sealed class OpusFactAttribute : FactAttribute
{
    public OpusFactAttribute()
    {
        try { using var e = new OpusEncoder(48000, 48); }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
        { Skip = "libopus could not be loaded (" + ex.GetType().Name + "); Opus tests skipped."; }
    }
}

public class OpusTests
{
    const int N = VoiceFormat.FrameSamples;

    static float[] SpeechLike(int frames, int seed)
    {
        var r = new Random(seed);
        var x = new float[frames * N];
        float lp = 0;
        for (int i = 0; i < x.Length; i++)
        {
            lp += 0.3f * ((float)(r.NextDouble() * 2 - 1) - lp);
            double env = 0.6 + 0.4 * Math.Sin(i / 4800.0 * Math.PI);
            x[i] = (float)(lp * env * 0.8 + 0.2 * Math.Sin(i * 2 * Math.PI * 220 / 48000.0) * env);
        }
        return x;
    }

    [OpusFact]
    public void SineSurvivesRoundTrip()
    {
        const int frames = 50;
        var input = new float[frames * N];
        for (int i = 0; i < input.Length; i++) input[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / 48000.0);
        var output = new float[input.Length];
        using var enc = new OpusEncoder(48000, 48);
        using var dec = new OpusDecoder(VoiceFormat.PlaybackRate);
        var pkt = new byte[1500];
        var pcm = new float[N];
        for (int f = 0; f < frames; f++)
        {
            int len = enc.Encode(input, f * N, pkt);
            Assert.True(len > 0);
            Assert.Equal(N, dec.Decode(pkt, len, pcm, false));
            Array.Copy(pcm, 0, output, f * N, N);
        }
        double ri = 0, ro = 0; int zc = 0, n = 0;
        for (int i = 2 * N; i < input.Length; i++)
        {
            ri += input[i] * input[i]; ro += output[i] * output[i]; n++;
            if (i > 2 * N && (output[i - 1] < 0) != (output[i] < 0)) zc++;
        }
        double rmsIn = Math.Sqrt(ri / n), rmsOut = Math.Sqrt(ro / n);
        Assert.InRange(rmsOut / rmsIn, 0.9, 1.1);
        double freq = zc / 2.0 / (n / 48000.0);
        Assert.InRange(freq, 970, 1030);
    }

    [OpusFact]
    public void FecRecoversDroppedFrame()
    {
        var input = SpeechLike(50, 7);
        var packets = new byte[50][];
        using (var enc = new OpusEncoder(48000, 48))
        {
            enc.PacketLossPercent = 20;
            var buf = new byte[1500];
            for (int f = 0; f < 50; f++)
            {
                int len = enc.Encode(input, f * N, buf);
                packets[f] = new byte[len];
                Array.Copy(buf, packets[f], len);
            }
        }
        var clean = new float[N];
        using (var d = new OpusDecoder(VoiceFormat.PlaybackRate))
            for (int f = 0; f <= 25; f++) d.Decode(packets[f], packets[f].Length, clean, false);

        var rec = new float[N];
        using (var d = new OpusDecoder(VoiceFormat.PlaybackRate))
        {
            var tmp = new float[N];
            for (int f = 0; f < 25; f++) d.Decode(packets[f], packets[f].Length, tmp, false);
            Assert.Equal(N, d.Decode(packets[26], packets[26].Length, rec, true));
        }
        double e = 0, a = 0, b = 0, dot = 0;
        for (int i = 0; i < N; i++) { e += rec[i] * rec[i]; a += rec[i] * rec[i]; b += clean[i] * clean[i]; dot += rec[i] * clean[i]; }
        Assert.True(e > 1e-4, "recovered frame is silence");
        Assert.True(dot / Math.Sqrt(a * b) > 0.5, "correlation " + dot / Math.Sqrt(a * b));
    }

    [OpusFact]
    public void ConcealProducesFullFrame()
    {
        using var enc = new OpusEncoder(48000, 48);
        using var dec = new OpusDecoder(VoiceFormat.PlaybackRate);
        var input = SpeechLike(3, 1);
        var pkt = new byte[1500];
        var pcm = new float[N];
        for (int f = 0; f < 3; f++) { int len = enc.Encode(input, f * N, pkt); dec.Decode(pkt, len, pcm, false); }
        Assert.Equal(N, dec.Conceal(pcm));
    }

    [OpusFact]
    public void PacketLossPercentClamped()
    {
        using var enc = new OpusEncoder(48000, 48);
        Assert.Equal(10, enc.PacketLossPercentRead);
        enc.PacketLossPercent = 50;
        Assert.Equal(30, enc.PacketLossPercentRead);
        enc.PacketLossPercent = 1;
        Assert.Equal(5, enc.PacketLossPercentRead);
    }
}
