namespace Voice.Tests;

public class EchoReferenceTests
{
    static float[] Fill(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void Reads_each_channel_interleaved_where_it_was_written()
    {
        var r = new EchoReference(2, 1);
        r.Write(0, Fill(480, 0.5f), 480, 1000);
        r.Write(1, Fill(480, -0.25f), 480, 1240);
        var o = new short[480 * 2];
        r.Read(1000, 480, o);
        Assert.Equal((short)16383, o[0]);         // channel 0 at 1000
        Assert.Equal((short)0, o[1]);             // channel 1 not yet
        Assert.Equal((short)16383, o[2 * 240]);
        Assert.Equal((short)-8191, o[2 * 240 + 1]);
    }

    [Fact]
    public void Nothing_written_or_not_yet_written_reads_as_silence()
    {
        var r = new EchoReference(1, 1);
        r.Write(0, Fill(480, 0.5f), 480, 1000);
        var o = new short[480];
        r.Read(1480, 480, o);
        Assert.All(o, v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_lap_of_the_ring_ago_is_never_read_as_played()
    {
        var r = new EchoReference(1, 1);            // one second: 48000 samples
        r.Write(0, Fill(480, 0.5f), 480, 1000);
        r.Write(0, Fill(480, 0.1f), 480, 1000 + 48000 + 960);
        var o = new short[480];
        r.Read(1000 + 48000, 480, o);               // the same ring slots as the first write: the gap, silence
        Assert.All(o, v => Assert.Equal(0, v));
        r.Read(1000, 480, o);                       // too old now
        Assert.All(o, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Knows_when_anything_last_played()
    {
        var r = new EchoReference(1, 1);
        Assert.False(r.ActiveSince(0));
        r.Write(0, Fill(480, 0.5f), 480, 1000);
        Assert.True(r.ActiveSince(1479));
        Assert.False(r.ActiveSince(1481));
    }
}
