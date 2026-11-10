using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StbcRevival.Voice;
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
    ///
    /// Revival voice: the hello's third byte is the Revival voice wire version (VoiceWire.Version) when Revival voice is
    /// on, capturing and playable here, else 0 (older mods send two bytes, read as off). A crewmate counts as on
    /// Revival voice only when that byte equals our own VoiceWire.Version, so mismatched versions fall back to game
    /// voice instead of dropping each other's packets. Crewmates both sides of a confirmed link with it on get Revival voice instead (RevivalVoice), so
    /// the game's frames to them are left out here, and theirs aren't played while their Revival voice is live. The
    /// hellos run whenever Revival voice is on, even with DirectVoice off (then game voice still goes through the host).
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
            public bool Revival;                // their hellos say Revival voice is on there (old two-byte hellos: no)
        }

        /// <summary>A crewmate with the mod as the main thread sees them now (for Revival voice).</summary>
        internal sealed class PeerView
        {
            public ulong SteamId;
            public int Actor;
            public string Name;
            public bool Direct;
            public bool Revival;
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
            public ulong[] ViaHostSteam = new ulong[0]; // each ViaHost player's shared Steam ID (0: none)
        }

        private static volatile Routes routes = new Routes();
        private sealed class Outgoing
        {
            public byte[] Packet;
            public ulong[] To;
        }

        private static readonly Queue<Outgoing> outbox = new Queue<Outgoing>();
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

        // Hellos and the crewmate list run for direct game voice and for Revival voice (which only goes over a
        // link the hellos confirm); game voice itself only goes directly with [Voice] DirectVoice on.
        private static bool Linked => (Plugin.DirectVoice.Value || Plugin.RevivalVoiceActive) && SteamMode.Enabled && SteamMode.MySteamId != 0;
        private static bool Active => Plugin.DirectVoice.Value && Linked;

        // The hello's third byte: Revival voice is on here, capturing, and we can decode it, so crewmates may send it to us.
        private static bool RevivalHere => Plugin.RevivalVoiceActive && VoiceCapture.Capturing && RevivalPlayback.CanDecode;

        // NetworkCore.SendEvent(code, content, receivers, protocol, channel): a voice frame goes directly to those
        // with a direct link; the rest (if any) are sent it through the host, addressed to them alone.
        // (NetworkEventCode, EventReceivers and SequenceChannel are BHMFCLKJOAD, KPLNDEOBNNJ and GDFONJHIBGH.)
        // Crewmates on Revival voice get neither: they hear us through it instead.
        private static bool SendEvent_Prefix(SVR.Network.BHMFCLKJOAD __0, object __1, SVR.Network.KPLNDEOBNNJ __2, SVR.Network.GDFONJHIBGH __4, ref bool __result)
        {
            if (Convert.ToInt32(__0) != VoiceEvent || !(__1 is SVR.Network.PLPKPDFOLDL frame) || !Linked) return true;
            if (Convert.ToInt32(__2) != 0) return true; // not "Others" (the game's debug echo)
            var now = routes;
            bool revival = RevivalVoice.SendsToAnyone;
            if (now.Direct.Length == 0 && !revival) return true; // nobody direct: the game's own send
            var direct = now.Direct;
            var viaHost = now.ViaHost;
            if (revival)
            {
                direct = Array.FindAll(now.Direct, id => !RevivalVoice.SendsTo(id));
                var kept = new List<int>(viaHost.Length);
                for (int i = 0; i < viaHost.Length; i++)
                    if (now.ViaHostSteam[i] == 0 || !RevivalVoice.SendsTo(now.ViaHostSteam[i])) kept.Add(viaHost[i]);
                if (direct.Length == 0 && kept.Count == viaHost.Length) return true; // nobody on Revival voice after all
                viaHost = kept.ToArray();
            }
            if (direct.Length > 0)
            {
                int length = (int)frame.PICFPDJILEK; // Length
                if (length <= 0 || length > receiveBuffer.Length - 1) return true;
                var packet = new byte[length + 1];
                packet[0] = Voice;
                Buffer.BlockCopy(frame.CDBPFHDELDP, 0, packet, 1, length); // Buffer
                lock (outbox) outbox.Enqueue(new Outgoing { Packet = packet, To = direct });
                System.Threading.Interlocked.Add(ref sentDirect, direct.Length);
            }
            if (viaHost.Length == 0)
            {
                SVR.Network.PLPKPDFOLDL.Release(frame); // what Photon's serializer would have done after sending it
                PerfLog.VoiceFrameSent();
                __result = true;
                return false;
            }
            // The game's send, to the rest only (the frame is released by Photon's serializer as usual).
            System.Threading.Interlocked.Add(ref sentViaHost, viaHost.Length);
            __result = NDIOGIGMPBH.RaiseEvent(VoiceEvent, frame, false, new CGJCFMCOBMJ { TargetActors = viaHost, SequenceChannel = Convert.ToByte(__4) });
            return false;
        }

        /// <summary>Main thread, from SteamMode.Update once Steam is ready.</summary>
        internal static void Update()
        {
            if (!Linked)
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
                byte revival = RevivalHere ? VoiceWire.Version : (byte)0;
                foreach (var peer in peers.Values) Send(peer.SteamId, new[] { Hello, (byte)(time - peer.LastHeard < DirectFor ? 1 : 0), revival });
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
            var viaHostSteam = new List<ulong>();
            var unmodded = new List<string>();
            foreach (var player in OtherPlayers())
            {
                int actor = player.KLNDGCGHADM;                                  // ID
                string name = player.GPICMMEIAPM;                                // name
                if (!(Props(player)?[HostHandoff.SteamIdKey] is string sid) || !ulong.TryParse(sid, out ulong steamId) || steamId == SteamMode.MySteamId)
                {
                    viaHost.Add(actor);
                    viaHostSteam.Add(0);
                    unmodded.Add(name);
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
                    Plugin.Log.LogInfo(Active ? $"Direct voice: {name} {(direct ? "now hears you and is heard directly" : "back through the host")}"
                                              : $"Direct voice: the link with {name} {(direct ? "works both ways" : "is lost")}");
                }
                if (!direct || !Active)
                {
                    viaHost.Add(actor);
                    viaHostSteam.Add(steamId);
                }
            }
            foreach (var gone in peers.Keys.Where(k => !seen.Contains(k)).ToList()) peers.Remove(gone);
            withoutMod = unmodded.ToArray();
            routes = new Routes
            {
                Direct = Active ? peers.Values.Where(p => p.Direct).Select(p => p.SteamId).ToArray() : new ulong[0],
                ViaHost = viaHost.ToArray(),
                ViaHostSteam = viaHostSteam.ToArray(),
            };
        }

        private static void Receive(float time)
        {
            for (int i = 0; i < 200 && SteamNetworking.IsP2PPacketAvailable(out uint size, Channel); i++)
            {
                if (!SteamNetworking.ReadP2PPacket(receiveBuffer, (uint)receiveBuffer.Length, out uint read, out CSteamID from, Channel)) break;
                if (read == 0 || !peers.TryGetValue(from.m_SteamID, out var peer)) continue; // only from a player in this room
                peer.LastHeard = time;
                if (receiveBuffer[0] == Hello && read >= 2)
                {
                    if (receiveBuffer[1] == 1) peer.LastConfirmed = time;
                    peer.Revival = read >= 3 && receiveBuffer[2] == VoiceWire.Version;
                }
                if (receiveBuffer[0] != Voice || read < 2) continue;
                Deliver(peer, (int)read - 1);
            }
        }

        // To VoIpManager's own handler, as Photon would have delivered it: a PooledReader of the frame, from its
        // speaker's actor. The handler releases the reader once it has read it.
        // Not while their Revival voice is playing: that's them already (the handover drops the game's copy).
        private static void Deliver(Peer peer, int length)
        {
            if (RevivalPlayback.Live(peer.Actor)) return;
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
            while (true)
            {
                Outgoing item;
                lock (outbox)
                {
                    if (outbox.Count == 0) break;
                    item = outbox.Dequeue();
                }
                foreach (var steamId in item.To) Send(steamId, item.Packet);
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
            if (direct == 0 && viaHost == 0 && got == 0 && (peers.Count == 0 || !Active)) return; // without DirectVoice, Revival voice reports the links
            Plugin.Log.LogInfo($"Direct voice: last {ReportEvery:0} s, {direct} frames sent directly and {viaHost} through the host (per crewmate), {got} received directly; "
                               + (peers.Count == 0 ? "no crewmates with the mod" : string.Join(", ", peers.Values.Select(p => $"{p.Name} {(p.Direct ? "direct" : "through the host")}").ToArray())));
        }

        private static void Forget()
        {
            peers.Clear();
            withoutMod = new string[0];
            routes = new Routes();
            lock (outbox) outbox.Clear();
        }

        private static string[] withoutMod = new string[0];

        /// <summary>Main thread: the room's other players with the mod (a shared Steam ID), as of the last refresh.</summary>
        internal static List<PeerView> Peers()
        {
            var list = new List<PeerView>(peers.Count);
            foreach (var p in peers.Values)
                list.Add(new PeerView { SteamId = p.SteamId, Actor = p.Actor, Name = p.Name, Direct = p.Direct, Revival = p.Revival });
            return list;
        }

        /// <summary>Main thread: the names of the room's other players without the mod (no shared Steam ID).</summary>
        internal static string[] WithoutMod() => withoutMod;

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
