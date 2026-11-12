using System;
using System.IO;
using SovereignQuantum;

internal static class Tests
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
    }
    private static void Invalid(byte[] bytes, string name)
    {
        try { PcmWave.Read(bytes); }
        catch (InvalidDataException) { passed++; return; }
        throw new Exception("FAIL: " + name);
    }
    public static void Main(string[] args)
    {
        Check(QuantumPolicy.IsSovereignBody(true, "Body - Sovereign (Enterprise-D)(Clone)"), "TNG Sovereign");
        Check(QuantumPolicy.IsSovereignBody(true, "Body - Sovereign (Aegis)(Clone)"), "Aegis Sovereign");
        Check(!QuantumPolicy.IsSovereignBody(false, "Body - Sovereign (Aegis)"), "NPC exclusion");
        Check(!QuantumPolicy.IsSovereignBody(true, "Body - Nova (Enterprise-D)(Clone)"), "Nova exclusion");
        Check(!QuantumPolicy.IsSovereignBody(true, "Body - Enterprise 1701D(Clone)"), "stock Enterprise exclusion");
        Check(!QuantumPolicy.IsSovereignBody(true, "USS Sovereign"), "renamed ships excluded");
        Check(!QuantumPolicy.IsSovereignBody(true, null), "missing body");
        Check(QuantumPolicy.Multiplier(1.5f) == 1.5f, "balance value");
        Check(QuantumPolicy.Multiplier(-10f) == 1f, "negative config");
        Check(QuantumPolicy.Multiplier(999f) == 3f, "upper bound");
        Check(QuantumPolicy.Multiplier(float.NaN) == 1.5f, "NaN config");
        Check(QuantumPolicy.Multiplier(float.PositiveInfinity) == 1.5f, "infinite config");
        byte[] bytes = args.Length > 0 ? File.ReadAllBytes(args[0]) : Fixture();
        PcmWave wave = PcmWave.Read(bytes);
        Check(wave.Channels == 1 && wave.Rate == 44100, "audio format");
        Check(wave.Samples.Length > wave.Rate / 10 && wave.Samples.Length < wave.Rate * 15, "clip duration");
        float peak = 0;
        foreach (float sample in wave.Samples) { Check(sample >= -1 && sample <= 1, "PCM range"); peak = Math.Max(peak, Math.Abs(sample)); }
        Check(peak > 0.05f, "non-silent sound");
        Invalid(new byte[0], "empty audio");
        Invalid(new byte[20], "non-wave audio");
        byte[] truncated = new byte[bytes.Length - 100];
        Array.Copy(bytes, truncated, truncated.Length);
        Invalid(truncated, "truncated audio");
        Console.WriteLine("PASS: ship isolation, balance bounds, PCM parsing, invalid-file handling; " + passed + " assertions.");
        Console.WriteLine("Audio: " + ((double)wave.Samples.Length / wave.Rate / wave.Channels).ToString("0.00") + " seconds; peak " + peak.ToString("0.000"));
    }

    private static byte[] Fixture()
    {
        const int samples = 11025;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 0.1) * 16384));
            return stream.ToArray();
        }
    }
}
