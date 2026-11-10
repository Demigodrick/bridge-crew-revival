using System;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Rooms] BoatswainWhistle: when someone joins your crew (PlayerJoinedAlert notices), the boatswain's whistle
    /// pipes them aboard. The sound is made here, not taken from anywhere: a whistle gliding up an octave, held with
    /// a vibrato and a short warble, and gliding back down, with a little breath in it. Made once, played as 2D sound.
    /// </summary>
    internal static class BoatswainWhistle
    {
        private static AudioClip clip;
        private static AudioSource source;

        internal static void Play()
        {
            try
            {
                if (clip == null) clip = Make(44100);
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
