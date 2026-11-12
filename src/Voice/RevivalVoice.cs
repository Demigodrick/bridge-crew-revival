// Revival voice transport: our captured packets straight to each crewmate on Revival voice (Steam channel 4,
// unreliable, no delay), theirs in to RevivalPlayback, and loss reports both ways so each speaker's encoder can
// match its forward error correction to what its listeners lose.
//
// A crewmate is on Revival voice when DirectVoice's hellos confirm the link both ways and say Revival voice is on
// there, and our own capture is running (our hello says so too). For them the game's voice frames are left out of
// the game's send (DirectVoice.SendEvent_Prefix), and their game voice is dropped here while their Revival voice is
// live (RevivalPlayback.Live), so the handover in either direction has no gap and no double voice.
// Steam is only called on the main thread.
using System;
using System.Collections.Generic;
using HarmonyLib;
using StbcRevival.Voice;
using UnityEngine;
using CSteamID = Steamworks.IMHJDCPKKHM;
using EP2PSend = Steamworks.KKHEAIABKGB;
using SteamNetworking = Steamworks.IJILPGFHMJG; // client SteamNetworking

namespace StbcRevival
{
    internal static class RevivalVoice
    {
        private const int Channel = VoiceFormat.Channel;
        private const int MaxReceivePerFrame = 400;
        private const float RefreshEvery = 0.25f, ReportEvery = 1f, LogEvery = 30f, ReportsFresh = 10f;
        private const byte VoiceEvent = 125; // NetworkEventCode.VoIP

        private sealed class Peer
        {
            public ulong SteamId;
            public int Actor;
            public string Name;
            public bool On;                   // we send them Revival voice (and not game voice)
            public string Why;                // why not, when not
            public int Sent, Received;        // packets, since the last report
            public uint Stream;               // their latest stream, for our reports to them
            public int ReportedLoss = -1, ReportedJitter;
            public float ReportedAt = -100f;
            public NetSim Sim;
            public string SimSpec;
            public int Pings, Pongs, RttSum, RttMax;   // since the last 30 s report
        }

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly List<ulong> gone = new List<ulong>();
        private static volatile HashSet<ulong> sendsTo = new HashSet<ulong>(); // replaced whole; read by the send hook
        private static readonly byte[] receiveBuffer = new byte[64 * 1024];
        private static readonly byte[] reportBuffer = new byte[16];
        private static readonly byte[] pingBuffer = new byte[VoiceWire.PingLength];
        private static readonly VoicePacketIn packetIn = new VoicePacketIn();
        private static float nextRefresh, nextReport, nextLog;
        private static bool running;

        /// <summary>Revival voice runs here: Steam mode, and [Voice] Engine = Revival.</summary>
        internal static bool Active => Plugin.RevivalVoiceActive;

        /// <summary>We send this crewmate Revival voice, so not game voice. Any thread.</summary>
        internal static bool SendsTo(ulong steamId) => sendsTo.Contains(steamId);

        /// <summary>Anyone at all gets Revival voice from us. Any thread.</summary>
        internal static bool SendsToAnyone => sendsTo.Count > 0;

        // ---- Game voice off while Revival voice is live -------------------------------------------------------

        internal static void Patch(Harmony harmony)
        {
            var received = AccessTools.Method(typeof(VoIpManager), "GMFCCIFGKDN"); // OnPhotonEvent
            if (received == null) Plugin.Log.LogWarning("Revival voice: VoIpManager's voice event wasn't found; a crewmate on Revival voice may be heard twice");
            else harmony.Patch(received, prefix: new HarmonyMethod(typeof(RevivalVoice), nameof(VoiceEvent_Prefix)));
        }

        // A crewmate's game voice through the host while their Revival voice plays: let go, as PvpRelay does.
        private static bool VoiceEvent_Prefix(byte __0, object __1, int __2, bool __runOriginal)
        {
            if (!__runOriginal) return false; // another prefix already let it go
            if (__0 != VoiceEvent || !RevivalPlayback.Live(__2)) return true;
            if (__1 is SVR.Network.PLPKPDFOLDL packet) SVR.Network.PLPKPDFOLDL.Release(packet); // back to its pool, as the game does
            return false;
        }

        // ---- Per frame ----------------------------------------------------------------------------------------

        /// <summary>Main thread, from SteamMode.Update (after DirectVoice.Update) once Steam is ready.</summary>
        internal static void Update()
        {
            if (!Active)
            {
                if (running) Stop();
                DrainUnused();
                return;
            }
            running = true;
            float time = Time.realtimeSinceStartup;
            double nowMs = RevivalPlayback.NowMs;
            if (time >= nextRefresh)
            {
                nextRefresh = time + RefreshEvery;
                Refresh();
            }
            SendCaptured();
            Receive(nowMs);
            if (time >= nextReport)
            {
                nextReport = time + ReportEvery;
                SendReports();
                SendPings(nowMs);
                TuneEncoder(time);
            }
            if (time >= nextLog)
            {
                nextLog = time + LogEvery;
                Report(time);
            }
        }

        // Who's on Revival voice now, from DirectVoice's crewmates.
        private static void Refresh()
        {
            bool capturing = VoiceCapture.Capturing;
            var on = new HashSet<ulong>();
            var seen = new HashSet<ulong>();
            foreach (var view in DirectVoice.Peers())
            {
                seen.Add(view.SteamId);
                if (!peers.TryGetValue(view.SteamId, out var peer)) peers[view.SteamId] = peer = new Peer { SteamId = view.SteamId };
                peer.Actor = view.Actor;
                peer.Name = view.Name;
                string why = !view.Direct ? "no direct link: an older mod, or Steam can't connect you"
                           : !view.Revival ? "their mod is older or uses a different Revival voice version, or their Revival voice is off, not capturing, or has no Opus decoder"
                           : !capturing ? "your microphone isn't being captured" : null;
                bool now = why == null;
                if (now != peer.On)
                {
                    if (now) Plugin.Log.LogInfo($"Revival voice: {peer.Name} now on Revival voice");
                    else Plugin.Log.LogInfo($"Revival voice: {peer.Name} back on game voice ({why})");
                }
                peer.On = now;
                peer.Why = why;
                if (now) on.Add(view.SteamId);
            }
            gone.Clear();
            foreach (var id in peers.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (var id in gone) peers.Remove(id);
            if (!on.SetEquals(sendsTo)) sendsTo = on;
        }

        // Our captured packets, to everyone on Revival voice (with nobody to send to they're dropped, not kept).
        private static void SendCaptured()
        {
            while (VoiceCapture.TryTakePacket(out var wire, out int length))
                foreach (var peer in peers.Values)
                {
                    if (!peer.On) continue;
                    Send(peer.SteamId, wire, length);
                    peer.Sent++;
                }
        }

        private static void Receive(double nowMs)
        {
            string spec = Plugin.VoiceDebugNetSim.Value;
            bool simulate = !string.IsNullOrEmpty(spec);
            for (int i = 0; i < MaxReceivePerFrame && SteamNetworking.IsP2PPacketAvailable(out uint size, Channel); i++)
            {
                if (!SteamNetworking.ReadP2PPacket(receiveBuffer, (uint)receiveBuffer.Length, out uint read, out CSteamID from, Channel)) break;
                if (read == 0 || !peers.TryGetValue(from.m_SteamID, out var peer)) continue; // only from a crewmate in this room
                var sim = simulate ? Sim(peer, spec) : null;
                if (sim != null) sim.Offer(receiveBuffer, (int)read, nowMs);
                else Handle(peer, receiveBuffer, (int)read, nowMs);
            }
            if (!simulate) return;
            foreach (var peer in peers.Values)
            {
                if (peer.Sim == null) continue;
                while (peer.Sim.TryTake(nowMs, out var packet, out int length)) Handle(peer, packet, length, nowMs);
            }
        }

        // [Voice] DebugNetSim: one simulated bad connection per crewmate, seeded from their Steam ID.
        private static NetSim Sim(Peer peer, string spec)
        {
            if (peer.SimSpec != spec)
            {
                peer.SimSpec = spec;
                peer.Sim = NetSim.Parse(spec, (int)(peer.SteamId ^ (peer.SteamId >> 32)));
                if (peer.Sim != null) Plugin.Log.LogInfo($"Revival voice: {peer.Name}'s voice arrives as over a bad connection ({spec}), for testing");
            }
            return peer.Sim;
        }

        private static void Handle(Peer peer, byte[] data, int length, double nowMs)
        {
            if (length < 2) return;
            if (data[0] == VoiceWire.VoiceType)
            {
                if (!VoiceWire.TryReadVoice(data, length, packetIn)) return;
                peer.Received++;
                peer.Stream = packetIn.Stream;
                RevivalPlayback.Receive(peer.Actor, packetIn, nowMs);
            }
            else if (data[0] == VoiceWire.ReportType && VoiceWire.TryReadReport(data, length, out _, out byte loss, out ushort jitter))
            {
                peer.ReportedLoss = loss;
                peer.ReportedJitter = jitter;
                peer.ReportedAt = Time.realtimeSinceStartup;
            }
            else if (data[0] == VoiceWire.PingType && VoiceWire.TryReadPing(data, length, VoiceWire.PingType, out uint pinged))
            {
                // Answered at once, on the channel voice uses, so the round trip is what voice sees.
                Send(peer.SteamId, pingBuffer, VoiceWire.WritePing(pingBuffer, VoiceWire.PongType, pinged));
            }
            else if (data[0] == VoiceWire.PongType && VoiceWire.TryReadPing(data, length, VoiceWire.PongType, out uint sent))
            {
                // (A simulated bad connection delays these too: they're on the same channel.)
                int rtt = (int)((uint)nowMs - sent);
                if (rtt < 0 || rtt > 10000) return; // not one of ours this run
                peer.Pongs++;
                peer.RttSum += rtt;
                peer.RttMax = Math.Max(peer.RttMax, rtt);
            }
        }

        // Each crewmate we hear: what we lose of them and their jitter, for their encoder.
        private static void SendReports()
        {
            foreach (var peer in peers.Values)
            {
                var stats = RevivalPlayback.Stats(peer.Actor);
                if (stats == null || stats.Received + stats.Lost == 0) continue; // nothing heard this second
                int length = VoiceWire.WriteReport(reportBuffer, peer.Stream, (byte)Mathf.Clamp(stats.LossPercent, 0, 100),
                                                   (ushort)Mathf.Clamp(stats.JitterMs, 0, ushort.MaxValue));
                Send(peer.SteamId, reportBuffer, length);
            }
        }

        // A ping a second to each crewmate on Revival voice, for the round trip in the 30 s report.
        private static void SendPings(double nowMs)
        {
            foreach (var peer in peers.Values)
            {
                if (!peer.On) continue;
                Send(peer.SteamId, pingBuffer, VoiceWire.WritePing(pingBuffer, VoiceWire.PingType, (uint)nowMs));
                peer.Pings++;
            }
        }

        // Whether Steam carries our connection to them through its relay servers (a longer way round) or directly.
        private static string Route(ulong steamId)
        {
            try
            {
                if (!SteamNetworking.GetP2PSessionState(new CSteamID(steamId), out var state)) return "Steam has no session with them";
                return state.m_bUsingRelay != 0 ? "through Steam's relay" : "direct";
            }
            catch (Exception e)
            {
                return "route unknown (" + e.Message + ")";
            }
        }

        // Our encoder's expected loss: twice the worst a listener reports, plus 5 (5-30 %). Kept as it is while
        // no listener has reported lately (nobody heard us speak).
        private static void TuneEncoder(float time)
        {
            int worst = -1;
            foreach (var peer in peers.Values)
                if (peer.On && peer.ReportedLoss >= 0 && time - peer.ReportedAt < ReportsFresh) worst = Math.Max(worst, peer.ReportedLoss);
            if (worst >= 0) VoiceCapture.PacketLossPercent = Mathf.Clamp(2 * worst + 5, 5, 30);
        }

        private static void Send(ulong steamId, byte[] packet, int length) =>
            SteamNetworking.SendP2PPacket(new CSteamID(steamId), packet, (uint)length, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel);

        // ---- Reports and stopping -----------------------------------------------------------------------------

        private static void Report(float time)
        {
            var without = DirectVoice.WithoutMod();
            if (peers.Count == 0 && without.Length == 0) return;
            if (VoiceCapture.Capturing) Plugin.Log.LogInfo("Revival voice: your microphone: " + VoiceCapture.Describe());
            foreach (var peer in peers.Values)
            {
                string mode = peer.On ? "Revival voice" : "game voice (" + peer.Why + ")";
                string reported = peer.ReportedLoss >= 0 && time - peer.ReportedAt < LogEvery
                    ? $"they lose {peer.ReportedLoss}% of yours, jitter {peer.ReportedJitter} ms"
                    : "no loss report from them";
                string trip = peer.Pongs > 0 ? $"round trip {peer.RttSum / peer.Pongs} ms (worst {peer.RttMax} ms, {peer.Pongs} of {peer.Pings} pings answered)"
                            : peer.Pings > 0 ? $"none of {peer.Pings} pings answered (an older mod, or nothing gets through)" : "not pinged";
                Plugin.Log.LogInfo($"Revival voice: {peer.Name}: {mode}; last 30 s, {peer.Sent} packets sent, {peer.Received} received; {reported}; "
                                   + $"{trip}, {Route(peer.SteamId)}");
                var heard = RevivalPlayback.Describe(peer.Actor, out int delayMs);
                if (heard != null)
                {
                    // Their mouth to your ear: a frame recorded, half the round trip, then everything buffered here.
                    // The game's own audio output adds its own on top, not known here.
                    if (delayMs > 0 && peer.Pongs > 0)
                        heard += $"; about {VoiceFormat.FrameMs + peer.RttSum / peer.Pongs / 2 + delayMs} ms from their mouth to the game's audio output";
                    Plugin.Log.LogInfo($"Revival voice from {peer.Name}: {heard}");
                }
                peer.Sent = peer.Received = 0;
                peer.Pings = peer.Pongs = peer.RttSum = peer.RttMax = 0;
            }
            foreach (var name in without) Plugin.Log.LogInfo($"Revival voice: {name}: game voice (no mod)");
        }

        private static void Stop()
        {
            running = false;
            peers.Clear();
            sendsTo = new HashSet<ulong>();
            Plugin.Log.LogInfo("Revival voice: off; crewmates hear you the game's way");
        }

        // Revival voice is off here: crewmates stop sending it within a hello or two; what still comes is let go.
        private static void DrainUnused()
        {
            for (int i = 0; i < MaxReceivePerFrame && SteamNetworking.IsP2PPacketAvailable(out uint size, Channel); i++)
                if (!SteamNetworking.ReadP2PPacket(receiveBuffer, (uint)receiveBuffer.Length, out _, out CSteamID _, Channel)) break;
        }
    }
}
