using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The sound PlayerJoinedAlert plays when someone joins your crew: an LCARS-style notification. The mod brings no
    /// recording: its own sound is made here, a short computer chirp of two rising tones. Your own sound goes first: a
    /// 16-bit PCM .wav at BepInEx/sounds/player-joined.wav (not part of the mod). Load() runs on entering a room, so
    /// the file is read then rather than when someone joins, and a file added or changed since is picked up. 2D sound.
    /// </summary>
    internal static class JoinSound
    {
        internal const string OwnFile = "BepInEx/sounds/player-joined.wav";
        private const int MaxBytes = 16 * 1024 * 1024; // about 90 s of 16-bit stereo at 48 kHz: plenty for a chirp

        private static AudioClip made, own;
        private static DateTime ownStamp;              // the loaded file's write time (MinValue: none loaded)
        private static AudioSource source;

        private static string OwnPath => Path.Combine(Path.GetDirectoryName(Application.dataPath), OwnFile);

        // Your own .wav, (re)read if it's new or changed since the last load; dropped if it's gone.
        internal static void Load()
        {
            try
            {
                var path = OwnPath;
                if (!File.Exists(path))
                {
                    Drop();
                    return;
                }
                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == ownStamp) return;
                Drop();
                ownStamp = stamp; // a file that won't read is only tried (and warned about) once per change
                own = Read(path);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Join sound: {OwnFile}: {e.Message}; playing the mod's own"); }
        }

        internal static void Play()
        {
            try
            {
                if (own == null && ownStamp == DateTime.MinValue) Load(); // joined before a room was seen
                var clip = own ?? (made != null ? made : made = Make(44100));
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

        private static void Drop()
        {
            if (own != null) UnityEngine.Object.Destroy(own);
            own = null;
            ownStamp = DateTime.MinValue;
        }

        private static AudioClip Read(string path)
        {
            if (new FileInfo(path).Length > MaxBytes) throw new Exception($"over {MaxBytes / (1024 * 1024)} MB");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                throw new Exception("not a .wav file");
            int format = 0, channels = 0, rate = 0, bits = 0, dataAt = -1, dataSize = 0;
            for (int o = 12; o + 8 <= bytes.Length;)
            {
                string id = Encoding.ASCII.GetString(bytes, o, 4);
                long size = BitConverter.ToUInt32(bytes, o + 4); // unsigned: a bad size can't step backwards
                if (id == "fmt ")
                {
                    if (size < 16 || o + 8 + 16 > bytes.Length) throw new Exception("bad format chunk");
                    format = BitConverter.ToUInt16(bytes, o + 8);
                    channels = BitConverter.ToUInt16(bytes, o + 10);
                    rate = BitConverter.ToInt32(bytes, o + 12);
                    bits = BitConverter.ToUInt16(bytes, o + 22);
                }
                else if (id == "data")
                {
                    dataAt = o + 8;
                    dataSize = (int)Math.Min(size, bytes.Length - dataAt);
                    break;
                }
                long next = o + 8 + size + (size & 1);
                if (next > bytes.Length) break;
                o = (int)next;
            }
            if (format != 1) throw new Exception("not PCM");
            if (bits != 16 || channels < 1 || channels > 8 || rate < 8000 || rate > 192000 || dataAt < 0)
                throw new Exception($"{bits}-bit, {channels} channel(s), {rate} Hz: only 16-bit PCM is read");
            int frames = dataSize / 2 / channels;
            if (frames < 1) throw new Exception("no sound in it");
            var samples = new float[frames * channels];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, dataAt + i * 2) / 32768f;
            var clip = AudioClip.Create("Join sound (yours)", frames, channels, rate, false);
            clip.SetData(samples, 0);
            Plugin.Log.LogInfo($"Join sound: yours, {OwnFile} ({frames / (float)rate:F2} s)");
            return clip;
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
            var clip = AudioClip.Create("Join sound", n, 1, rate, false);
            clip.SetData(s, 0);
            return clip;
        }
    }
}
