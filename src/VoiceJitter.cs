using System;
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
    /// of each burst of speech is held until a cushion of it is waiting and then handed over at once (in pieces
    /// the game's player takes), so Wwise has that much in hand for the rest of the burst. The cushion adapts,
    /// per crewmate: it starts at [Voice] JitterBufferMs (0 = the game's behaviour); when their voice runs dry in
    /// the middle of a burst (it came late), playback holds again until there's a cushion, and the cushion grows
    /// (up to 400 ms); each burst that plays through shrinks it back a little. A good connection stays quick,
    /// a bad one gets smoother. Every 30 s, for each crewmate heard, the log gives the bursts, the times the
    /// voice ran dry, the voice frames that never arrived (filled in by the decoder), and the cushion.
    /// </summary>
    internal static class VoiceJitter
    {
        private const float BurstGap = 0.25f; // seconds of no voice that end a burst of speech
        private const int MaxCushionMs = 400, GrowMs = 40, ShrinkMs = 10;
        private const int Piece = 4000;       // samples per hand-over: what the player's own 4096-sample clip takes

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
            public int Bursts, DryOuts, LastMissing, Rebuffers;
            public bool WasPlaying;
            public int CushionMs = -1;      // this crewmate's cushion; -1 until first heard
            public bool PlayedThisStream;   // playback started since the last hand-over (only then can it run dry)
            public bool BurstRanDry;        // this burst had to hold again
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

        private static int Target(WwiseAudioPlayer player, Speaker s)
        {
            int rate = SampleRate(player) > 0 ? SampleRate(player) : 16000;
            return rate * s.CushionMs / 1000;
        }

        // Decoded voice for Wwise: within a burst, straight through; at the start of one, held until there's a
        // cushion's worth, then all of it at once.
        private static bool Buffer_Prefix(WwiseAudioPlayer __instance, Samples __0)
        {
            if (handingOver || Plugin.VoiceJitterBufferMs.Value <= 0 || __0 == null) return true;
            var s = Of(__instance);
            int start = Plugin.VoiceJitterBufferMs.Value;
            if (s.CushionMs < start) s.CushionMs = start;
            float now = Time.unscaledTime;
            bool sameBurst = now - s.LastVoice < BurstGap;
            s.LastVoice = now;
            if (s.Streaming && sameBurst)
            {
                var source = Source(__instance);
                if (!s.PlayedThisStream || source == null || source.isPlaying) return true;
                // Ran dry mid-burst: this voice comes late. Hold again until there's a (bigger) cushion.
                s.CushionMs = Math.Min(MaxCushionMs, s.CushionMs + GrowMs);
                s.Rebuffers++;
                s.BurstRanDry = true;
            }
            else if (!sameBurst)
            {
                // A new burst: if the last one played through, ease the cushion back towards where it started.
                if (s.Streaming && !s.BurstRanDry) s.CushionMs = Math.Max(start, s.CushionMs - ShrinkMs);
                s.BurstRanDry = false;
                s.Held.Clear();
                s.Bursts++;
            }
            s.Streaming = false;
            s.Buffer = __0;
            int count = __0.PICFPDJILEK;
            if (count <= 0) return true;
            var chunk = new float[count];
            __0.CopyTo(0, chunk, 0, count * 4);
            s.Held.AddRange(chunk);
            if (s.Held.Count < Target(__instance, s)) return false; // keep holding
            HandOver(__instance, s);
            return false;
        }

        // What's held, to the player in pieces it takes (each also goes to Wwise as it comes).
        private static void HandOver(WwiseAudioPlayer player, Speaker s)
        {
            var all = s.Held.ToArray();
            s.Held.Clear();
            s.Streaming = true;
            s.PlayedThisStream = false;
            handingOver = true;
            try
            {
                for (int at = 0; at < all.Length; at += Piece)
                {
                    int n = Math.Min(Piece, all.Length - at);
                    s.Buffer.Resize(n);
                    s.Buffer.CopyFrom(all, at * 4, 0, n * 4);
                    player.BufferAudio(s.Buffer);
                }
            }
            finally { handingOver = false; }
        }

        private static void Update_Postfix(WwiseAudioPlayer __instance)
        {
            var s = Of(__instance);
            // A short burst that never reached the cushion: hand it over once the voice has stopped coming.
            if (!s.Streaming && s.Held.Count > 0 && s.Buffer != null && Time.unscaledTime - s.LastVoice >= BurstGap * 0.5f)
                HandOver(__instance, s);
            // The game pauses its tracking clip when playback catches up with what arrived: the voice ran dry.
            var source = Source(__instance);
            bool playing = source != null && source.isPlaying;
            if (playing) s.PlayedThisStream = true;
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
                                   + $"(a burst's end counts once), held again mid-burst {s.Rebuffers} times, {lost} frames lost on the way; "
                                   + $"cushion {Math.Max(s.CushionMs, Plugin.VoiceJitterBufferMs.Value)} ms");
                s.Bursts = s.DryOuts = s.Rebuffers = 0;
            }
        }
    }
}
