using System;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Rooms] JoinSound: when someone joins your crew (PlayerJoinedAlert notices), an LCARS-style notification sound.
    /// The mod brings no recording: its own sound is made here, a short computer chirp of two rising tones. Your own
    /// sound goes first: a 16-bit .wav at BepInEx/sounds/player-joined.wav (not part of the mod). Played as 2D sound.
    /// </summary>
    internal static class JoinSound
    {
        private static AudioClip clip;
        private static AudioSource source;

        internal const string OwnFile = "BepInEx/sounds/player-joined.wav";

        internal static void Play()
        {
            try
            {
                if (clip == null) clip = Own() ?? Make(44100);
                if (source == null)
                {
                    var go = new GameObject("Revival join sound");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    source = go.AddComponent<AudioSource>();
                    source.spatialBlend = 0f;
                    source.playOnAwake = false;
                }
                source.volume = 0.6f;
                source.PlayOneShot(clip);
            }
            catch (Exception e) { Plugin.Log.LogInfo("Join sound: " + e.Message); }
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
                var own = AudioClip.Create("Join sound (yours)", frames, channels, rate, false);
                own.SetData(samples, 0);
                Plugin.Log.LogInfo($"Join sound: yours, {OwnFile} ({frames / (float)rate:F2} s)");
                return own;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Join sound: {OwnFile}: {e.Message}; playing the mod's own");
                return null;
            }
        }

        // Two short tones, the second higher, each with a quick attack and a soft tail: a computer chirp.
        private static AudioClip Make(int rate)
        {
            float[] starts = { 0f, 0.11f }, lengths = { 0.09f, 0.16f }, freqs = { 1180f, 1570f };
            float total = 0.32f;
            int n = (int)(total * rate);
            var s = new float[n];
            for (int k = 0; k < starts.Length; k++)
            {
                int from = (int)(starts[k] * rate), count = (int)(lengths[k] * rate);
                for (int i = 0; i < count && from + i < n; i++)
                {
                    float t = i / (float)rate;
                    float env = Mathf.Min(1f, t / 0.005f) * Mathf.Exp(-t / (lengths[k] * 0.45f));
                    float wave = Mathf.Sin(2f * Mathf.PI * freqs[k] * t) + 0.25f * Mathf.Sin(2f * Mathf.PI * freqs[k] * 2f * t);
                    s[from + i] += env * wave * 0.4f;
                }
            }
            var made = AudioClip.Create("Join sound", n, 1, rate, false);
            made.SetData(s, 0);
            return made;
        }
    }
}
