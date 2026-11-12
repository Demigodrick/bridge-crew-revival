namespace Voice.Tests;

public class VoiceWireTests
{
    static byte[] Make(out int len, bool withPrev = true)
    {
        var buf = new byte[64];
        len = VoiceWire.WriteVoice(buf, 7, 1000, true, new byte[] { 1, 2, 3 }, 3,
            withPrev ? new byte[] { 9, 8 } : new byte[0], withPrev ? 2 : 0);
        return buf;
    }

    [Fact]
    public void RoundTripsVoiceWithPrev()
    {
        var buf = Make(out var len);
        var p = new VoicePacketIn();
        Assert.True(VoiceWire.TryReadVoice(buf, len, p));
        Assert.Equal(7u, p.Stream);
        Assert.Equal(1000u, p.Seq);
        Assert.True(p.BurstStart);
        Assert.Equal(3, p.OpusLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, p.Opus.Take(3).ToArray());
        Assert.Equal(2, p.PrevLength);
        Assert.Equal(new byte[] { 9, 8 }, p.Prev.Take(2).ToArray());
    }

    [Fact]
    public void RoundTripsVoiceWithoutPrev()
    {
        var buf = Make(out var len, false);
        var p = new VoicePacketIn();
        Assert.True(VoiceWire.TryReadVoice(buf, len, p));
        Assert.Equal(0, p.PrevLength);
        Assert.Equal(3, p.OpusLength);
    }

    [Fact]
    public void RejectsTruncated()
    {
        var buf = Make(out var len);
        var p = new VoicePacketIn();
        for (int n = 0; n < len; n++)
            Assert.False(VoiceWire.TryReadVoice(buf, n, p), "prefix " + n);
        Assert.False(VoiceWire.TryReadVoice(buf, -1, p));
        Assert.False(VoiceWire.TryReadVoice(buf, buf.Length + 5, p));
    }

    [Fact]
    public void RejectsWrongTypeOrVersion()
    {
        var p = new VoicePacketIn();
        var buf = Make(out var len);
        buf[0] = (byte)'Q';
        Assert.False(VoiceWire.TryReadVoice(buf, len, p));
        buf = Make(out len);
        buf[1] = 2;
        Assert.False(VoiceWire.TryReadVoice(buf, len, p));
    }

    [Fact]
    public void RoundTripsReport()
    {
        var buf = new byte[16];
        int len = VoiceWire.WriteReport(buf, 9, 12, 85);
        Assert.True(VoiceWire.TryReadReport(buf, len, out var s, out var loss, out var jit));
        Assert.Equal(9u, s);
        Assert.Equal((byte)12, loss);
        Assert.Equal((ushort)85, jit);
        Assert.False(VoiceWire.TryReadReport(buf, len - 1, out _, out _, out _));
        buf[0] = (byte)'R';
        Assert.False(VoiceWire.TryReadReport(buf, len, out _, out _, out _));
    }

    [Fact]
    public void PingRoundTrips()
    {
        var b = new byte[16];
        int n = VoiceWire.WritePing(b, VoiceWire.PingType, 123456789u);
        Assert.True(VoiceWire.TryReadPing(b, n, VoiceWire.PingType, out uint t));
        Assert.Equal(123456789u, t);
        Assert.False(VoiceWire.TryReadPing(b, n, VoiceWire.PongType, out _));
        Assert.False(VoiceWire.TryReadPing(b, n - 1, VoiceWire.PingType, out _));
    }
}
