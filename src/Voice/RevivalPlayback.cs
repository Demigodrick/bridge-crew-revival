// Revival voice playback: each crewmate's Revival voice through their own voice player, so lip sync, the 3D position,
// their volume, mute and who-can-hear-whom all apply as for game voice. Packets go into a JitterBuffer per crewmate
// (main thread); one playout thread pops every crewmate's buffer every 20 ms (decoding happens there, under the
// buffer's lock) into an output queue; the main thread hands what's queued to the crewmate's WwiseAudioPlayer,
// keeping about 60 ms ahead of what it has played.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using StbcRevival.Voice;
using UnityEngine;
using Controller = DaikonForge.VoIP.VoiceControllerBase;
using PlayerBase = DaikonForge.VoIP.AudioPlayerBase;
using Samples = DaikonForge.VoIP.DHEDECPPLGP<float>;

namespace StbcRevival
{
    internal static class RevivalPlayback
    {
        private const double LiveMs = 1000, StaleMs = 10000;
        private const int MaxCatchUp = 5;           // playout ticks made up at once after the thread was held up
        private const int PlayerQueueMs = 60;       // how far ahead of the player's playback we hand audio over
        private const int DropAboveMs = 160;        // at a burst's start, more queued than this is cut to PlayerQueueMs
        private const int MaxOutputFrames = 50;     // 1 s: a main thread stuck that long loses the oldest

        private static readonly Stopwatch clock = Stopwatch.StartNew();

        /// <summary>The clock packets' arrival times are on (milliseconds, any thread).</summary>
        internal static double NowMs => clock.Elapsed.TotalMilliseconds;

        private struct OutFrame
        {
            public float[] Pcm;
            public bool BurstStart;
        }

        private sealed class Speaker
        {
            public int Actor;
            public JitterBuffer Buffer;
            public OpusDecoder Decoder;                                   // popped (decoded) on the playout thread only
            public readonly Queue<OutFrame> Output = new Queue<OutFrame>(); // lock Output
            public readonly Stack<float[]> Spare = new Stack<float[]>();    // lock Output
            public volatile bool Removed;
            public double LastPacketMs;                                   // lock sync

            // Playout thread.
            public bool WasPlaying;
            public int Overflow;                                          // lock Output

            // Main thread.
            public WwiseAudioPlayer Player;                               // the player set up for Revival voice
            public bool Configured, EqualizeWas;
            public int RateWas;
            public Samples Samples;
            public JitterStats Window = new JitterStats();               // counters since the last Describe
            public int Played, Dropped, NotPlayed;
            public long DelaySumMs; public int DelaySamples;              // actual delay: buffer depth + player backlog
        }

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Speaker> speakers = new Dictionary<int, Speaker>(); // lock sync
        private static readonly List<Speaker> pendingDispose = new List<Speaker>();                // lock sync
        private static readonly List<Speaker> mainList = new List<Speaker>();
        private static Thread thread;
        private static Run run;                     // the running thread's stop flag (each thread has its own)

        private sealed class Run
        {
            public volatile bool Stop;
        }
        private static volatile string threadError;
        private static bool decoderFailureLogged;

        // VoIpManager's players by actor number (PFKNMPPKOFP); a voice controller's output device (PHGNCBOCLEH,
        // _audioOutputDevice); a player's tracking AudioSource, rate, clip and playback position fields.
        private static readonly AccessTools.FieldRef<VoIpManager, IDictionary> VoipPlayers =
            AccessTools.FieldRefAccess<VoIpManager, IDictionary>("PFKNMPPKOFP");
        private static readonly AccessTools.FieldRef<Controller, object> OutputDevice =
            AccessTools.FieldRefAccess<Controller, object>("PHGNCBOCLEH");
        private static readonly AccessTools.FieldRef<PlayerBase, AudioSource> Source =
            AccessTools.FieldRefAccess<PlayerBase, AudioSource>("NIPDKJAKHCO");     // _trackingSource
        private static readonly AccessTools.FieldRef<PlayerBase, int> SampleRate =
            AccessTools.FieldRefAccess<PlayerBase, int>("BOAFPOJBLCN");             // _sampleRate
        private static readonly AccessTools.FieldRef<PlayerBase, AudioClip> Clip =
            AccessTools.FieldRefAccess<PlayerBase, AudioClip>("KGHBBCEOLGA");       // _audioClip (the tracking clip)
        private static readonly AccessTools.FieldRef<PlayerBase, int> WriteAt =
            AccessTools.FieldRefAccess<PlayerBase, int>("MPEKIJHNFKP");             // where the next samples go in the clip
        private static readonly AccessTools.FieldRef<PlayerBase, int> Pushed =
            AccessTools.FieldRefAccess<PlayerBase, int>("LECNEIIHLJM");             // samples handed to the player in all
        private static readonly AccessTools.FieldRef<PlayerBase, int> LastTime =
            AccessTools.FieldRefAccess<PlayerBase, int>("OFGIMEGMJPL");             // the clip position last frame
        private static readonly AccessTools.FieldRef<PlayerBase, int> PlayedSamples =
            AccessTools.FieldRefAccess<PlayerBase, int>("OKBOHDOHKOI");             // _played: whole clip loops played
        private static readonly AccessTools.FieldRef<WwiseAudioPlayer, int> SinceStart =
            AccessTools.FieldRefAccess<WwiseAudioPlayer, int>("FMIJNNOLMAP");       // samples since the clip last stopped

        private static int FrameSamples => VoiceFormat.PlaybackRate * VoiceFormat.FrameMs / 1000;

        // ---- In: packets (main thread) -----------------------------------------------------------------------

        /// <summary>Main thread: a Revival voice packet from this player.</summary>
        internal static void Receive(int actor, VoicePacketIn packet, double nowMs)
        {
            if (!Plugin.RevivalVoiceActive || packet == null) return;
            Speaker s;
            lock (sync) speakers.TryGetValue(actor, out s);
            if (s == null)
            {
                s = Create(actor);
                if (s == null) return;
            }
            lock (sync) s.LastPacketMs = nowMs;
            s.Buffer.Push(packet, nowMs);
            if (thread == null) StartThread();
        }

        private static Speaker Create(int actor)
        {
            OpusDecoder decoder;
            try { decoder = new OpusDecoder(VoiceFormat.PlaybackRate); }
            catch (Exception e)
            {
                if (!decoderFailureLogged) Plugin.Log.LogError("Revival voice: no Opus decoder, crewmates' Revival voice isn't played: " + e.Message);
                decoderFailureLogged = true;
                return null;
            }
            var s = new Speaker
            {
                Actor = actor,
                Decoder = decoder,
                Buffer = new JitterBuffer(decoder),
                Samples = new Samples(FrameSamples, FrameSamples),
            };
            lock (sync) speakers[actor] = s;
            return s;
        }

        /// <summary>Whether a Revival packet came from this player within the last second (game voice from them is
        /// dropped meanwhile). Any thread.</summary>
        internal static bool Live(int actor)
        {
            lock (sync) return speakers.TryGetValue(actor, out var s) && NowMs - s.LastPacketMs < LiveMs;
        }

        /// <summary>Main thread: this player's buffer statistics since the last call (null if none), for the loss
        /// report to them. Counters also add up for <see cref="Describe"/>.</summary>
        internal static JitterStats Stats(int actor)
        {
            Speaker s;
            lock (sync) speakers.TryGetValue(actor, out s);
            return s == null ? null : Take(s);
        }

        private static JitterStats Take(Speaker s)
        {
            var now = s.Buffer.TakeStats();
            var w = s.Window;
            w.Received += now.Received;
            w.Late += now.Late;
            w.Duplicates += now.Duplicates;
            w.Lost += now.Lost;
            w.Redundant += now.Redundant;
            w.Fec += now.Fec;
            w.Concealed += now.Concealed;
            w.TailConcealed += now.TailConcealed;
            w.Underruns += now.Underruns;
            w.TargetMs = now.TargetMs;
            w.DepthMs = now.DepthMs;
            w.JitterMs = now.JitterMs;
            w.LossPercent = now.LossPercent;
            return now;
        }

        /// <summary>Main thread: the 30 s report for this player (null if nothing heard from them); starts a new window.</summary>
        internal static string Describe(int actor)
        {
            Speaker s;
            lock (sync) speakers.TryGetValue(actor, out s);
            if (s == null) return null;
            Take(s);
            var w = s.Window;
            int overflow;
            lock (s.Output) { overflow = s.Overflow; s.Overflow = 0; }
            if (w.Received == 0 && w.Late == 0 && w.Lost == 0 && s.Played == 0) return null;
            string line = $"last 30 s, received {w.Received}, lost {w.Lost} (redundancy {w.Redundant}, FEC {w.Fec}), concealed {w.Concealed}, "
                          + $"late {w.Late}, duplicates {w.Duplicates}, underruns {w.Underruns}, jitter {w.JitterMs} ms, buffer {w.TargetMs} ms"
                          + (s.DelaySamples > 0 ? $", actual delay {s.DelaySumMs / s.DelaySamples} ms" : "")
                          + $", {w.TailConcealed} frames faded out at speech ends"
                          + (s.Dropped + overflow > 0 ? $", {s.Dropped + overflow} frames dropped after a hold-up" : "")
                          + (s.NotPlayed > 0 ? $", {s.NotPlayed} frames not played (no voice player, muted or out of earshot)" : "");
            s.Window = new JitterStats();
            s.Played = s.Dropped = s.NotPlayed = 0;
            s.DelaySumMs = 0;
            s.DelaySamples = 0;
            return line;
        }

        // ---- Out: to the voice players (main thread) ------------------------------------------------------------

        /// <summary>Main thread, every frame: hand decoded audio to the players.</summary>
        internal static void Update()
        {
            if (!Plugin.RevivalVoiceActive)
            {
                if (thread != null || speakers.Count > 0) Shutdown("Revival voice is off");
                return;
            }
            var error = threadError;
            if (error != null)
            {
                threadError = null;
                Plugin.Log.LogError("Revival voice: playout failed: " + error);
            }
            double now = NowMs;
            mainList.Clear();
            lock (sync) mainList.AddRange(speakers.Values);
            foreach (var s in mainList)
            {
                double last;
                lock (sync) last = s.LastPacketMs;
                int queued;
                lock (s.Output) queued = s.Output.Count;
                if (queued > 0) Play(s);
                if (now - last >= LiveMs && queued == 0 && s.Configured) Restore(s);
                if (now - last > StaleMs && queued == 0 && !s.Buffer.Playing) Remove(s);
            }
        }

        private static void Play(Speaker s)
        {
            var player = FindPlayer(s.Actor, out var voip, out var info, out var controller);
            if (s.Configured && !ReferenceEquals(s.Player, player)) Restore(s); // a new voice player (a scene change)
            if (player == null || !Hears(voip, info, controller, s.Actor))
            {
                s.NotPlayed += Discard(s);
                return;
            }
            int viewId = info.View.LJIBDPHGLMM; // viewID
            float volume = Mathf.Clamp01(voip.GetChannelVolume(viewId) * voip.GetVolumeForPlayer(info.Player));
            bool burstStart = false;
            while (true)
            {
                OutFrame frame;
                lock (s.Output)
                {
                    if (s.Output.Count == 0) break;
                    if (s.Output.Peek().BurstStart && s.Output.Count * VoiceFormat.FrameMs > DropAboveMs)
                    {
                        // The main thread was held up: start this burst no later than usual.
                        while (s.Output.Count * VoiceFormat.FrameMs > PlayerQueueMs)
                        {
                            s.Spare.Push(s.Output.Dequeue().Pcm);
                            s.Dropped++;
                        }
                        burstStart = true; // what's left starts the burst
                    }
                    if (QueuedMs(player) >= PlayerQueueMs) break;
                    frame = s.Output.Dequeue();
                }
                if (frame.BurstStart || burstStart || !s.Configured) Configure(s, player);
                burstStart = false;
                int n = frame.Pcm.Length;
                if (volume != 1f)
                    for (int i = 0; i < n; i++) frame.Pcm[i] *= volume;
                s.Samples.Resize(n);
                s.Samples.CopyFrom(frame.Pcm, 0, 0, n * 4);
                player.SetReceived(n);
                VoiceJitter.Bypass(player, s.Samples);
                s.Played++;
                lock (s.Output) s.Spare.Push(frame.Pcm);
            }
            if (s.Buffer.Playing)
            {
                // What a word heard now waits: the jitter buffer's depth, plus decoded audio not yet played.
                int output;
                lock (s.Output) output = s.Output.Count;
                s.DelaySumMs += s.Buffer.DepthMs + output * VoiceFormat.FrameMs + QueuedMs(player);
                s.DelaySamples++;
            }
        }

        // As UDPNetworkVoiceController.VoIPEventHandler and VoiceControllerBase's decoder decide, plus PvP's filter.
        private static bool Hears(VoIpManager voip, VoIpManager.PIHAFAACGLJ info, Controller controller, int actor)
        {
            if (info.View == null || info.Player == null || controller.Mute) return false;
            if (!voip.CanHear(info.View.LJIBDPHGLMM)) return false;
            var nova = NovaPlayerManager.IEFBKOMBFGO;
            if (nova == null || nova.GetInfoForPlayer(info.Player) == null) return false;
            return PvpRelay.Hears(actor);
        }

        // The player's voice player: VoIpManager._voipPlayers[actor].VoiceController._audioOutputDevice.
        private static WwiseAudioPlayer FindPlayer(int actor, out VoIpManager voip, out VoIpManager.PIHAFAACGLJ info, out Controller controller)
        {
            info = null;
            controller = null;
            voip = VoIpManager.IEFBKOMBFGO; // Instance
            if (voip == null || !(VoipPlayers(voip) is IDictionary players) || !players.Contains(actor)) return null;
            info = players[actor] as VoIpManager.PIHAFAACGLJ;
            controller = info?.VoiceController;
            if (controller == null) return null;
            var player = OutputDevice(controller) as WwiseAudioPlayer;
            return player != null && Source(player) != null ? player : null;
        }

        // What the player has been handed and not yet played (the game's own bookkeeping, as its BatchUpdate keeps it).
        private static int QueuedMs(WwiseAudioPlayer player)
        {
            var source = Source(player);
            int position = PlayedSamples(player) + (source != null ? source.timeSamples : 0);
            int ahead = Pushed(player) - position;
            int rate = SampleRate(player) > 0 ? SampleRate(player) : VoiceFormat.PlaybackRate;
            return ahead <= 0 ? 0 : (int)(ahead * 1000L / rate);
        }

        // At each burst: our rate on the player and its own volume equalising off (the speaker's auto gain does that).
        private static void Configure(Speaker s, WwiseAudioPlayer player)
        {
            if (!s.Configured)
            {
                s.Player = player;
                s.EqualizeWas = player.Equalize;
                s.RateWas = SampleRate(player);
                s.Configured = true;
            }
            player.Equalize = false;
            SetRate(player, VoiceFormat.PlaybackRate);
        }

        // Revival voice from them stopped: the player as game voice left it.
        private static void Restore(Speaker s)
        {
            var player = s.Player;
            s.Player = null;
            s.Configured = false;
            if (player == null) return; // destroyed with its scene
            player.Equalize = s.EqualizeWas;
            if (s.RateWas > 0) SetRate(player, s.RateWas);
        }

        /// <summary>While Revival voice is on, WwiseAudioPlayer.SetSampleRate is <see cref="SetRate"/>: the game's
        /// version destroys the tracking AudioSource along with the old clip (decompiled/_/WwiseAudioPlayer.cs,
        /// SetSampleRate), after which the player plays nothing. Game voice alone never changes the rate, but switching
        /// a crewmate between game voice and Revival voice does, in either direction and in any order with the game's
        /// own receive path. With Revival voice off the game's method runs untouched.</summary>
        internal static void Patch(Harmony harmony)
        {
            var setRate = AccessTools.Method(typeof(WwiseAudioPlayer), "SetSampleRate", new[] { typeof(int) });
            if (setRate == null) Plugin.Log.LogWarning("Revival voice: WwiseAudioPlayer.SetSampleRate wasn't found; switching a crewmate back to game voice may silence them");
            else harmony.Patch(setRate, prefix: new HarmonyMethod(typeof(RevivalPlayback), nameof(SetSampleRate_Prefix)));
        }

        private static bool SetSampleRate_Prefix(WwiseAudioPlayer __instance, int __0)
        {
            if (!Plugin.RevivalVoiceActive) return true;
            try { SetRate(__instance, __0); }
            catch (Exception e) { Plugin.Log.LogWarning("Revival voice: changing a voice player's rate: " + e.Message); }
            return false;
        }

        // SetSampleRate's work, keeping the tracking AudioSource (and doing nothing without one, as the game's does).
        private static void SetRate(WwiseAudioPlayer player, int rate)
        {
            if (player == null || rate <= 0) return;
            var source = Source(player);
            if (source == null) return;
            var old = source.clip;
            if (old != null && old.frequency == rate) return;
            var clip = AudioClip.Create("vc", 4096, 1, rate, false);
            source.Stop();
            source.clip = clip;
            source.loop = true;
            if (old != null) UnityEngine.Object.Destroy(old);
            SampleRate(player) = rate;
            Clip(player) = clip;
            WriteAt(player) = 0;
            Pushed(player) = 0;
            LastTime(player) = 0;
            PlayedSamples(player) = 0;
            SinceStart(player) = 0;
        }

        private static int Discard(Speaker s)
        {
            lock (s.Output)
            {
                int n = s.Output.Count;
                while (s.Output.Count > 0) s.Spare.Push(s.Output.Dequeue().Pcm);
                return n;
            }
        }

        private static void Remove(Speaker s)
        {
            if (s.Configured) Restore(s);
            bool now;
            lock (sync)
            {
                speakers.Remove(s.Actor);
                s.Removed = true;
                now = thread == null;
                if (!now) pendingDispose.Add(s); // the playout thread disposes it once it can't be popping it
            }
            if (now) s.Decoder.Dispose();
        }

        // ---- The playout thread ---------------------------------------------------------------------------------

        private static void StartThread()
        {
            var mine = run = new Run();
            thread = new Thread(() => Playout(mine)) { IsBackground = true, Name = "Revival voice playout" };
            thread.Start();
        }

        private static void Playout(Run mine)
        {
            var list = new List<Speaker>();
            var done = new List<Speaker>();
            var watch = Stopwatch.StartNew();
            double next = 0;
            try
            {
                while (!mine.Stop)
                {
                    double now = watch.Elapsed.TotalMilliseconds;
                    if (now < next)
                    {
                        Thread.Sleep(Math.Max(1, (int)(next - now)));
                        continue;
                    }
                    int due = (int)((now - next) / VoiceFormat.FrameMs) + 1;
                    if (due > MaxCatchUp)
                    {
                        next += (due - MaxCatchUp) * (double)VoiceFormat.FrameMs; // what's further behind is skipped
                        due = MaxCatchUp;
                    }
                    lock (sync)
                    {
                        list.Clear();
                        list.AddRange(speakers.Values);
                        done.AddRange(pendingDispose);
                        pendingDispose.Clear();
                    }
                    foreach (var s in done) s.Decoder.Dispose(); // removed before this snapshot: nothing pops them now
                    done.Clear();
                    for (int t = 0; t < due; t++)
                    {
                        foreach (var s in list) Tick(s);
                        next += VoiceFormat.FrameMs;
                    }
                }
            }
            catch (Exception e)
            {
                threadError = e.ToString();
            }
        }

        private static void Tick(Speaker s)
        {
            if (s.Removed) return;
            float[] pcm;
            lock (s.Output) pcm = s.Spare.Count > 0 ? s.Spare.Pop() : null;
            if (pcm == null || pcm.Length != FrameSamples) pcm = new float[FrameSamples];
            if (!s.Buffer.Pop(pcm, out _))
            {
                s.WasPlaying = false;
                lock (s.Output) s.Spare.Push(pcm);
                return;
            }
            lock (s.Output)
            {
                if (s.Output.Count >= MaxOutputFrames)
                {
                    s.Spare.Push(s.Output.Dequeue().Pcm);
                    s.Overflow++;
                }
                s.Output.Enqueue(new OutFrame { Pcm = pcm, BurstStart = !s.WasPlaying });
            }
            s.WasPlaying = true;
        }

        // Stops and joins the thread, gives the players back, and disposes the decoders.
        private static void Shutdown(string why)
        {
            var t = thread;
            if (run != null) run.Stop = true;
            run = null;
            bool joined = t == null || t.Join(1000);
            List<Speaker> all;
            lock (sync)
            {
                all = new List<Speaker>(speakers.Values);
                all.AddRange(pendingDispose);
                speakers.Clear();
                pendingDispose.Clear();
                thread = null;
            }
            foreach (var s in all)
            {
                s.Removed = true;
                if (s.Configured) Restore(s);
                if (joined) s.Decoder.Dispose(); // else left to its finalizer: the thread may still be popping it
            }
            if (!joined) Plugin.Log.LogWarning("Revival voice: the playout thread didn't stop in time; it finishes on its own");
            Plugin.Log.LogInfo($"Revival voice: playback stopped ({why})");
        }
    }
}
