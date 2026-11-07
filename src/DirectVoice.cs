using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CSteamID = Steamworks.IMHJDCPKKHM;
using EP2PSend = Steamworks.KKHEAIABKGB;
using SteamNetworking = Steamworks.IJILPGFHMJG; // client SteamNetworking

namespace StbcRevival
{
    /// <summary>
    /// [Voice] DirectVoice (Steam mode): crewmates' voices go straight between players over Steam instead of
    /// through the host. In Steam mode everything, voice included, travels your game → the host's game → each
    /// crewmate, inside one ordered Photon stream: a hitch on the host's game holds up everyone's voice, and a
    /// lost packet holds up what's behind it until it's resent. Here each game sends its voice frames (what
    /// UDPNetworkVoiceController hands NetworkCore.SendEvent as the VoIP event: a PooledReader holding a 9-byte
    /// header and the encoded audio) to every crewmate it has a direct link with, on Steam channel 3, unreliable
    /// and unbuffered: a lost frame is simply missing, which the decoder already fills. Crewmates without a
    /// direct link (no mod, an older one, or Steam couldn't connect) still get it the game's way, through the
    /// host, addressed to them alone.
    ///
    /// Every game says hello on channel 3 every <see cref="HelloEvery"/> seconds to each crewmate, saying whether it
    /// has heard from them lately. A link counts as direct while the crewmate's hellos say they hear you (within
    /// <see cref="DirectFor"/> seconds): both directions work, so a link Steam only gets through one way isn't
    /// used, and a crewmate without this never answers. Frames are
    /// only taken from the Steam ID a player in this room shares ("SID", HostHandoff), and are played as that
    /// player's, through VoIpManager's own handler (so mutes, volumes and who-can-hear-whom still apply).
    /// Steam is only called on the main thread; the send hook (possibly on the voice encoder's thread) queues.
    /// </summary>
    internal static class DirectVoice
    {
        private const int Channel = 3;
        private const byte Hello = (byte)'H', Voice = (byte)'V';
        private const float HelloEvery = 2f, DirectFor = 6f, ReportEvery = 30f;
        private const byte VoiceEvent = 125; // NetworkEventCode.VoIP

        private sealed class Peer
        {
            public ulong SteamId;
            public int Actor;
            public string Name;
            public float LastHeard = -100f;     // anything from them on channel 3
            public float LastConfirmed = -100f; // a hello from them saying they hear us
            public bool Direct;
        }

        // Main thread: the room's other players with a shared Steam ID, by Steam ID.
        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static float nextHello, nextRefresh, nextReport;

        // Shared with the send hook: who gets voice directly, and who still through the host (a snapshot,
        // replaced whole from the main thread).
        private sealed class Routes
        {
            public ulong[] Direct = new ulong[0];
            public int[] ViaHost = new int[0];
        }

        private static volatile Routes routes = new Routes();
        private static readonly Queue<byte[]> outbox = new Queue<byte[]>();
        private static int sentDirect, sentViaHost, received; // for the report (Interlocked from the hook)

        private static readonly byte[] receiveBuffer = new byte[64 * 1024];

        // VoIpManager's players by actor number (PFKNMPPKOFP), and its Photon event handler (GMFCCIFGKDN).
        private static readonly AccessTools.FieldRef<VoIpManager, IDictionary> VoipPlayers =
            AccessTools.FieldRefAccess<VoIpManager, IDictionary>("PFKNMPPKOFP");
        private static readonly MethodInfo OnPhotonEvent = AccessTools.Method(typeof(VoIpManager), "GMFCCIFGKDN");

        internal static void Patch(Harmony harmony)
        {
            var send = AccessTools.Method(typeof(SVR.Network.NetworkCore), "SendEvent");
            if (send == null)
            {
                Plugin.Log.LogWarning("Direct voice: NetworkCore.SendEvent wasn't found; voice stays routed through the host");
                return;
            }
            harmony.Patch(send, prefix: new HarmonyMethod(typeof(DirectVoice), nameof(SendEvent_Prefix)));
        }

        private static bool Active => Plugin.DirectVoice.Value && SteamMode.Enabled && SteamMode.MySteamId != 0;

        // NetworkCore.SendEvent(code, content, receivers, protocol, channel): a voice frame goes directly to those
        // with a direct link; the rest (if any) are sent it through the host, addressed to them alone.
        // (NetworkEventCode, EventReceivers and SequenceChannel are BHMFCLKJOAD, KPLNDEOBNNJ and GDFONJHIBGH.)
        private static bool SendEvent_Prefix(SVR.Network.BHMFCLKJOAD __0, object __1, SVR.Network.KPLNDEOBNNJ __2, SVR.Network.GDFONJHIBGH __4, ref bool __result)
        {
            if (Convert.ToInt32(__0) != VoiceEvent || !(__1 is SVR.Network.PLPKPDFOLDL frame) || !Active) return true;
            var now = routes;
            if (now.Direct.Length == 0 || Convert.ToInt32(__2) != 0) return true; // nobody direct, or not "Others" (the game's debug echo)
            int length = (int)frame.PICFPDJILEK; // Length
            if (length <= 0 || length > receiveBuffer.Length - 1) return true;
            var packet = new byte[length + 1];
            packet[0] = Voice;
            Buffer.BlockCopy(frame.CDBPFHDELDP, 0, packet, 1, length); // Buffer
            lock (outbox) outbox.Enqueue(packet);
            System.Threading.Interlocked.Add(ref sentDirect, now.Direct.Length);
            if (now.ViaHost.Length == 0)
            {
                SVR.Network.PLPKPDFOLDL.Release(frame); // what Photon's serializer would have done after sending it
                PerfLog.VoiceFrameSent();
                __result = true;
                return false;
            }
            // The game's send, to the rest only (the frame is released by Photon's serializer as usual).
            System.Threading.Interlocked.Add(ref sentViaHost, now.ViaHost.Length);
            __result = NDIOGIGMPBH.RaiseEvent(VoiceEvent, frame, false, new CGJCFMCOBMJ { TargetActors = now.ViaHost, SequenceChannel = Convert.ToByte(__4) });
            return false;
        }

        /// <summary>Main thread, from SteamMode.Update once Steam is ready.</summary>
        internal static void Update()
        {
            if (!Active)
            {
                if (peers.Count > 0) Forget();
                return;
            }
            float time = Time.realtimeSinceStartup;
            Receive(time);
            if (time >= nextRefresh)
            {
                nextRefresh = time + 0.5f;
                Refresh(time);
            }
            Flush();
            if (time >= nextHello)
            {
                nextHello = time + HelloEvery;
                foreach (var peer in peers.Values) Send(peer.SteamId, new[] { Hello, (byte)(time - peer.LastHeard < DirectFor ? 1 : 0) });
            }
            if (time >= nextReport)
            {
                nextReport = time + ReportEvery;
                Report();
            }
        }

        // The room's other players with a shared Steam ID, and who of them is direct now.
        private static void Refresh(float time)
        {
            var seen = new HashSet<ulong>();
            var viaHost = new List<int>();
            foreach (var player in OtherPlayers())
            {
                int actor = player.KLNDGCGHADM;                                  // ID
                string name = player.GPICMMEIAPM;                                // name
                if (!(Props(player)?[HostHandoff.SteamIdKey] is string sid) || !ulong.TryParse(sid, out ulong steamId) || steamId == SteamMode.MySteamId)
                {
                    viaHost.Add(actor);
                    continue;
                }
                seen.Add(steamId);
                if (!peers.TryGetValue(steamId, out var peer)) peers[steamId] = peer = new Peer { SteamId = steamId };
                peer.Actor = actor;
                peer.Name = name;
                bool direct = time - peer.LastConfirmed < DirectFor;
                if (direct != peer.Direct)
                {
                    peer.Direct = direct;
                    Plugin.Log.LogInfo($"Direct voice: {name} {(direct ? "now hears you and is heard directly" : "back through the host")}");
                }
                if (!direct) viaHost.Add(actor);
            }
            foreach (var gone in peers.Keys.Where(k => !seen.Contains(k)).ToList()) peers.Remove(gone);
            routes = new Routes
            {
                Direct = peers.Values.Where(p => p.Direct).Select(p => p.SteamId).ToArray(),
                ViaHost = viaHost.ToArray(),
            };
        }

        private static void Receive(float time)
        {
            for (int i = 0; i < 200 && SteamNetworking.IsP2PPacketAvailable(out uint size, Channel); i++)
            {
                if (!SteamNetworking.ReadP2PPacket(receiveBuffer, (uint)receiveBuffer.Length, out uint read, out CSteamID from, Channel)) break;
                if (read == 0 || !peers.TryGetValue(from.m_SteamID, out var peer)) continue; // only from a player in this room
                peer.LastHeard = time;
                if (receiveBuffer[0] == Hello && read >= 2 && receiveBuffer[1] == 1) peer.LastConfirmed = time;
                if (receiveBuffer[0] != Voice || read < 2) continue;
                Deliver(peer, (int)read - 1);
            }
        }

        // To VoIpManager's own handler, as Photon would have delivered it: a PooledReader of the frame, from its
        // speaker's actor. The handler releases the reader once it has read it.
        private static void Deliver(Peer peer, int length)
        {
            var voip = VoIpManager.IEFBKOMBFGO; // Instance
            if (voip == null || OnPhotonEvent == null || !(VoipPlayers(voip) is IDictionary players) || !players.Contains(peer.Actor)) return; // no voice for them yet
            var frame = SVR.Network.PLPKPDFOLDL.Acquire();
            frame.Write(receiveBuffer, 1u, (uint)length);
            frame.Seek(0u);
            received++;
            OnPhotonEvent.Invoke(voip, new object[] { VoiceEvent, frame, peer.Actor });
        }

        private static void Flush()
        {
            var direct = routes.Direct;
            while (true)
            {
                byte[] packet;
                lock (outbox)
                {
                    if (outbox.Count == 0) break;
                    packet = outbox.Dequeue();
                }
                foreach (var steamId in direct) Send(steamId, packet);
            }
        }

        private static void Send(ulong steamId, byte[] packet) =>
            SteamNetworking.SendP2PPacket(new CSteamID(steamId), packet, (uint)packet.Length, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel);

        private static void Report()
        {
            int direct = System.Threading.Interlocked.Exchange(ref sentDirect, 0);
            int viaHost = System.Threading.Interlocked.Exchange(ref sentViaHost, 0);
            int got = received;
            received = 0;
            if (direct == 0 && viaHost == 0 && got == 0 && peers.Count == 0) return;
            Plugin.Log.LogInfo($"Direct voice: last {ReportEvery:0} s, {direct} frames sent directly and {viaHost} through the host (per crewmate), {got} received directly; "
                               + (peers.Count == 0 ? "no crewmates with the mod" : string.Join(", ", peers.Values.Select(p => $"{p.Name} {(p.Direct ? "direct" : "through the host")}").ToArray())));
        }

        private static void Forget()
        {
            peers.Clear();
            routes = new Routes();
            lock (outbox) outbox.Clear();
        }

        private static IEnumerable<CBBCCGMALMP> OtherPlayers()
        {
            try
            {
                if (!NDIOGIGMPBH.PGJDPPDBGBL) return Enumerable.Empty<CBBCCGMALMP>(); // inRoom
                return (typeof(NDIOGIGMPBH).GetMethod("get_otherPlayers", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as CBBCCGMALMP[])
                       ?.Where(p => p != null) ?? Enumerable.Empty<CBBCCGMALMP>();
            }
            catch
            {
                return Enumerable.Empty<CBBCCGMALMP>();
            }
        }

        private static IDictionary Props(CBBCCGMALMP player) => player.PCDMCKFFBAC; // customProperties
    }
}
