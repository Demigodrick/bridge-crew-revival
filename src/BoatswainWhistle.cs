using System;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Rooms] BoatswainWhistle: when someone joins your crew (PlayerJoinedAlert notices), the boatswain's whistle
    /// pipes them aboard. The sound is made here, not taken from anywhere: a whistle gliding up an octave, held with
    /// a vibrato and a short warble, and gliding back down, with a little breath in it. Made once, played as 2D sound.
    /// Your own sound goes first: a 16-bit .wav at BepInEx/sounds/bootsmannspfeife.wav (not part of the mod).
    /// </summary>
    internal static class BoatswainWhistle
    {
        private static AudioClip clip;
        private static AudioSource source;

        // Your own whistle, if you've put one here (a .wav, 16-bit PCM): it's played instead of the made one. It stays
        // on your PC; the mod brings none.
        internal const string OwnFile = "BepInEx/sounds/bootsmannspfeife.wav";

        internal static void Play()
        {
            try
            {
                if (clip == null) clip = Own() ?? Make(44100);
                if (source == null)
                {
                    var go = new GameObject("Revival boatswain whistle");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    source = go.AddComponent<AudioSource>();
                    source.spatialBlend = 0f;
                    source.playOnAwake = false;
                }
                source.volume = 0.6f;
                source.PlayOneShot(clip);
            }
            catch (Exception e) { Plugin.Log.LogInfo("Boatswain whistle: " + e.Message); }
        }

        private static AudioClip Own()
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), OwnFile);
                if (!System.IO.File.Exists(path)) return null;
                var bytes = System.IO.File.ReadAllBytes(path);
                if (bytes.Length < 44 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                    throw new Exception("not a .wav file");
                int channels = 0, rate = 0, bits = 0, dataAt = -1, dataSize = 0;
                for (int o = 12; o + 8 <= bytes.Length;)
                {
                    string id = System.Text.Encoding.ASCII.GetString(bytes, o, 4);
                    int size = BitConverter.ToInt32(bytes, o + 4);
                    if (id == "fmt ")
                    {
                        if (BitConverter.ToInt16(bytes, o + 8) != 1) throw new Exception("not PCM");
                        channels = BitConverter.ToInt16(bytes, o + 10);
                        rate = BitConverter.ToInt32(bytes, o + 12);
                        bits = BitConverter.ToInt16(bytes, o + 22);
                    }
                    else if (id == "data") { dataAt = o + 8; dataSize = Math.Min(size, bytes.Length - dataAt); break; }
                    o += 8 + size + (size & 1);
                }
                if (bits != 16 || channels < 1 || rate <= 0 || dataAt < 0) throw new Exception($"{bits}-bit, {channels} channel(s): only 16-bit PCM is read");
                int frames = dataSize / 2 / channels;
                var samples = new float[frames * channels];
                for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, dataAt + i * 2) / 32768f;
                var own = AudioClip.Create("Boatswain whistle (yours)", frames, channels, rate, false);
                own.SetData(samples, 0);
                Plugin.Log.LogInfo($"Boatswain whistle: yours, {OwnFile} ({frames / (float)rate:F2} s)");
                return own;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Boatswain whistle: {OwnFile}: {e.Message}; playing the mod's own");
                return null;
            }
        }

        private static AudioClip Make(int rate)
        {
            const float low = 1400f, high = 2800f, up = 0.32f, hold = 1.15f, down = 0.38f;
            float total = up + hold + down;
            int n = (int)(total * rate);
            var s = new float[n];
            double phase = 0;
            var rng = new System.Random(7);
            float noise = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate, f;
                if (t < up) f = low * Mathf.Pow(high / low, t / up);
                else if (t < up + hold)
                {
                    float h = t - up;
                    f = high + 14f * Mathf.Sin(2f * Mathf.PI * 6.5f * h);                     // vibrato
                    if (h > 0.45f && h < 0.75f) f += 90f * Mathf.Sin(2f * Mathf.PI * 18f * h); // warble
                }
                else f = high * Mathf.Pow(low / high, (t - up - hold) / down);
                phase += 2 * Math.PI * f / rate;
                float env = Mathf.Min(1f, t / 0.03f) * Mathf.Min(1f, (total - t) / 0.15f);
                noise = noise * 0.92f + (float)(rng.NextDouble() * 2 - 1) * 0.08f;       // breath
                s[i] = env * (0.82f * (float)Math.Sin(phase) + 0.08f * (float)Math.Sin(2 * phase) + 0.10f * noise) * 0.5f;
            }
            var made = AudioClip.Create("Boatswain whistle", n, 1, rate, false);
            made.SetData(s, 0);
            return made;
        }
    }
}
