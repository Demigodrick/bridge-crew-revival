using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Player = DaikonForge.VoIP.UnityAudioPlayer;
using Controller = DaikonForge.VoIP.VoiceControllerBase;

namespace StbcRevival
{
    /// <summary>
    /// Smoother voice chat. A crewmate's voice is written, as it's decoded, into a looping 10 s clip
    /// (UnityAudioPlayer.BufferAudio), and the clip plays from there. When playback catches up with what has
    /// arrived the game pauses it (BatchUpdate) and plays again after just two more writes, so a few tens of
    /// milliseconds of delay on the way (here: sender, Photon, the Steam relay, the host's server, the relay again,
    /// the receiver and its decoder, each a frame apart) stops and starts the voice: choppy. Here playback
    /// (re)starts only with [Voice] JitterBufferMs of voice waiting (120 ms; 0 = the game's behaviour).
    /// Every 30 s, for each crewmate heard, the log gives the stops (buffer ran dry) and the voice frames that
    /// never arrived (filled in by the decoder), to tell delay from loss.
    /// </summary>
    internal static class VoiceJitter
    {
        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, AudioSource> Source =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, AudioSource>("NIPDKJAKHCO");
        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, AudioClip> Clip =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, AudioClip>("KGHBBCEOLGA");
        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, int> Written =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, int>("LECNEIIHLJM");     // samples written in all
        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, int> Wrapped =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, int>("OKBOHDOHKOI");     // samples of finished loops
        private static readonly AccessTools.FieldRef<DaikonForge.VoIP.AudioPlayerBase, int> WritesToPlay =
            AccessTools.FieldRefAccess<DaikonForge.VoIP.AudioPlayerBase, int>("BJKDPMLNKKG");     // writes left before Play
        private static readonly AccessTools.FieldRef<Controller, int> MissingFrames =
            AccessTools.FieldRefAccess<Controller, int>("PLHMCJMKMOC");

        private sealed class Stats
        {
            public int Stops, Holds, LastMissing;
            public bool WasPlaying;
        }

        private static readonly Dictionary<Player, Stats> stats = new Dictionary<Player, Stats>();
        private static float nextReport;

        internal static void Patch(Harmony harmony)
        {
            var buffer = AccessTools.Method(typeof(Player), "BufferAudio");
            var update = AccessTools.Method(typeof(Player), "BatchUpdate");
            if (buffer == null || update == null)
            {
                Plugin.Log.LogWarning("Voice: the voice player wasn't found; voice playback left as it is");
                return;
            }
            harmony.Patch(buffer, prefix: new HarmonyMethod(typeof(VoiceJitter), nameof(Buffer_Prefix)),
                postfix: new HarmonyMethod(typeof(VoiceJitter), nameof(Buffer_Postfix)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(VoiceJitter), nameof(Update_Postfix)));
        }

        private static Stats Of(Player player)
        {
            if (!stats.TryGetValue(player, out var s)) stats[player] = s = new Stats();
            return s;
        }

        private static int Ahead(Player player)
        {
            var source = Source(player);
            return Written(player) - (Wrapped(player) + (source != null ? source.timeSamples : 0));
        }

        private static void Buffer_Prefix(Player __instance, out bool __state)
        {
            var source = Source(__instance);
            __state = source != null && source.isPlaying;
        }

        // The game just started playback (two writes after a stop): hold it until enough is waiting.
        private static void Buffer_Postfix(Player __instance, bool __state)
        {
            int ms = Plugin.VoiceJitterBufferMs.Value;
            var source = Source(__instance);
            var clip = Clip(__instance);
            if (ms <= 0 || __state || source == null || clip == null || !source.isPlaying) return;
            int target = Mathf.Min(clip.frequency * ms / 1000, clip.samples / 2);
            if (Ahead(__instance) >= target) return;
            source.Pause();
            WritesToPlay(__instance) = 1; // the next write tries again (and comes back here)
            Of(__instance).Holds++;
        }

        // Count the stops (the game pauses when playback catches up), and report now and then.
        private static void Update_Postfix(Player __instance)
        {
            var source = Source(__instance);
            if (source == null) return;
            var s = Of(__instance);
            bool playing = source.isPlaying;
            if (s.WasPlaying && !playing) s.Stops++;
            s.WasPlaying = playing;
            if (Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + 30f;
            Report();
        }

        private static void Report()
        {
            foreach (var kv in new List<KeyValuePair<Player, Stats>>(stats))
            {
                var player = kv.Key;
                var s = kv.Value;
                if (player == null)
                {
                    stats.Remove(player);
                    continue;
                }
                var controller = player.GetComponent<Controller>();
                int missing = controller != null ? MissingFrames(controller) : 0;
                int lost = missing - s.LastMissing;
                if (s.Stops == 0 && lost == 0 && s.Holds == 0) continue;
                Plugin.Log.LogInfo($"Voice from {player.gameObject.name}: last 30 s, {s.Stops} stops (ran dry), {lost} frames lost on the way, "
                                   + $"{s.Holds} restarts held for {Plugin.VoiceJitterBufferMs.Value} ms");
                s.Stops = s.Holds = 0;
                s.LastMissing = missing;
            }
        }
    }
}
