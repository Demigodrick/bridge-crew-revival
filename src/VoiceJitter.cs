using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Controller = DaikonForge.VoIP.VoiceControllerBase;
using Samples = DaikonForge.VoIP.DHEDECPPLGP<float>;

namespace StbcRevival
{
    /// <summary>
    /// Smoother voice chat. A crewmate's voice is decoded a frame at a time and handed to Wwise as it comes
    /// (WwiseAudioPlayer.BufferAudio pushes it into the game's Wwise audio-input plugin, which plays whatever
    /// it has and silence when it runs dry). Between their microphone and your ears are several frame-to-frame
    /// hops (their game, Photon, the Steam relay, the host's in-game server, the relay again, yours, the
    /// decoder), so the voice arrives a little unevenly, and every late frame was a gap: choppy. Here the start
    /// of each burst of speech is held until [Voice] JitterBufferMs of it is waiting (100 ms; 0 = the game's
    /// behaviour) and then handed over at once, so Wwise has that much in hand for the rest of the burst.
    /// Every 30 s, for each crewmate heard, the log gives the bursts, the times the voice ran dry, and the voice
    /// frames that never arrived (filled in by the decoder), to tell delay from loss.
    /// </summary>
    internal static class VoiceJitter
    {
        private const float BurstGap = 0.25f; // seconds of no voice that end a burst of speech

        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, AudioSource> Source =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, AudioSource>("NIPDKJAKHCO"); // tracks Wwise's playback
        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, int> SampleRate =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, int>("BOAFPOJBLCN");
        private static readonly AccessTools.FieldRef<Controller, int> MissingFrames =
            AccessTools.FieldRefAccess<Controller, int>("PLHMCJMKMOC");

        private sealed class Speaker
        {
            public readonly List<float> Held = new List<float>();
            public Samples Buffer;          // the decoder's buffer, reused to hand over what's held
            public bool Streaming;          // within a burst, with its cushion handed over
            public float LastVoice;
            public int Bursts, DryOuts, LastMissing;
            public bool WasPlaying;
        }

        private static readonly Dictionary<WwiseAudioPlayer, Speaker> speakers = new Dictionary<WwiseAudioPlayer, Speaker>();
        private static bool handingOver;
        private static float nextReport;

        internal static void Patch(Harmony harmony)
        {
            var buffer = AccessTools.Method(typeof(WwiseAudioPlayer), "BufferAudio");
            var update = AccessTools.Method(typeof(WwiseAudioPlayer), "BatchUpdate");
            if (buffer == null || update == null)
            {
                Plugin.Log.LogWarning("Voice: the Wwise voice player wasn't found; voice playback left as it is");
                return;
            }
            harmony.Patch(buffer, prefix: new HarmonyMethod(typeof(VoiceJitter), nameof(Buffer_Prefix)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(VoiceJitter), nameof(Update_Postfix)));
        }

        private static Speaker Of(WwiseAudioPlayer player)
        {
            if (!speakers.TryGetValue(player, out var s)) speakers[player] = s = new Speaker();
            return s;
        }

        private static int Target(WwiseAudioPlayer player)
        {
            int rate = SampleRate(player) > 0 ? SampleRate(player) : 16000;
            // At most what the player's own 4096-sample clip takes in one go (it tracks playback for the game).
            return Mathf.Min(rate * Plugin.VoiceJitterBufferMs.Value / 1000, 4000);
        }

        // Decoded voice for Wwise: within a burst, straight through; at the start of one, held until there's a
        // cushion's worth, then all of it at once.
        private static bool Buffer_Prefix(WwiseAudioPlayer __instance, Samples __0)
        {
            if (handingOver || Plugin.VoiceJitterBufferMs.Value <= 0 || __0 == null) return true;
            var s = Of(__instance);
            float now = Time.unscaledTime;
            bool sameBurst = now - s.LastVoice < BurstGap;
            s.LastVoice = now;
            if (s.Streaming && sameBurst) return true;
            if (!sameBurst) s.Held.Clear();
            s.Streaming = false;
            s.Buffer = __0;
            int count = __0.PICFPDJILEK;
            if (count <= 0) return true;
            var chunk = new float[count];
            __0.CopyTo(0, chunk, 0, count * 4);
            s.Held.AddRange(chunk);
            if (s.Held.Count < Target(__instance)) return false; // keep holding
            HandOverInto(s, __0);
            return true;
        }

        private static void HandOverInto(Speaker s, Samples buffer)
        {
            var all = s.Held.ToArray();
            s.Held.Clear();
            buffer.Resize(all.Length);
            buffer.CopyFrom(all, 0, 0, all.Length * 4);
            s.Streaming = true;
            s.Bursts++;
        }

        private static void Update_Postfix(WwiseAudioPlayer __instance)
        {
            var s = Of(__instance);
            // A short burst that never reached the cushion: hand it over once the voice has stopped coming.
            if (!s.Streaming && s.Held.Count > 0 && s.Buffer != null && Time.unscaledTime - s.LastVoice >= BurstGap * 0.5f)
            {
                HandOverInto(s, s.Buffer);
                handingOver = true;
                try { __instance.BufferAudio(s.Buffer); }
                finally { handingOver = false; }
            }
            // The game pauses its tracking clip when playback catches up with what arrived: the voice ran dry.
            var source = Source(__instance);
            bool playing = source != null && source.isPlaying;
            if (s.WasPlaying && !playing) s.DryOuts++;
            s.WasPlaying = playing;
            if (Time.unscaledTime >= nextReport)
            {
                nextReport = Time.unscaledTime + 30f;
                Report();
            }
        }

        private static void Report()
        {
            foreach (var kv in new List<KeyValuePair<WwiseAudioPlayer, Speaker>>(speakers))
            {
                var player = kv.Key;
                var s = kv.Value;
                if (player == null)
                {
                    speakers.Remove(player);
                    continue;
                }
                var controller = player.GetComponent<Controller>() ?? player.GetComponentInParent<Controller>();
                int missing = controller != null ? MissingFrames(controller) : 0;
                int lost = missing - s.LastMissing;
                s.LastMissing = missing;
                if (s.Bursts == 0 && s.DryOuts == 0 && lost == 0) continue;
                Plugin.Log.LogInfo($"Voice chat from {player.gameObject.name}: last 30 s, {s.Bursts} bursts of speech, ran dry {s.DryOuts} times "
                                   + $"(a burst's end counts once), {lost} frames lost on the way; cushion {Plugin.VoiceJitterBufferMs.Value} ms");
                s.Bursts = s.DryOuts = 0;
            }
        }
    }
}
