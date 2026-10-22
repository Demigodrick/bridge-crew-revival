#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using ExitGames.Client.Photon;
using HarmonyLib;
using StbcServer.Net;

namespace StbcRevival.Net
{
    /// <summary>
    /// Lets the game's Photon client reach servers that aren't TCP endpoints: the server running inside
    /// the game ("local:1") and hosts reached over Steam P2P ("steam:&lt;steamid&gt;:1").
    ///
    /// Photon's TCP peer creates a SocketTcp per connection. For virtual addresses, Harmony prefixes take
    /// over its Connect / Send / Disconnect and route the bytes through an <see cref="IClientLink"/>;
    /// server frames go back through the socket's own HandleReceivedDatagram exactly as SocketTcp's receive
    /// loop would hand them over. Shared source: compiled into the mod and into the server tests.
    /// </summary>
    public static class VirtualSockets
    {
        /// <summary>
        /// Opens a link for a virtual address (host part, e.g. "local" or "steam:7656..."), or returns null
        /// to leave the address to the real TCP socket.
        /// </summary>
        public delegate IClientLink? Connector(string host, Action<byte[]> deliver, Action closed);

        public static Connector? Connect;

        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Dictionary<object, IClientLink> links = new();
        private static FieldInfo? peerBaseField;
        private static MethodInfo? setState;
        private static MethodInfo? handleException;

        public static void Install(Harmony harmony)
        {
            var socketTcp = typeof(PhotonPeer).Assembly.GetType("ExitGames.Client.Photon.SocketTcp")
                            ?? throw new InvalidOperationException("Photon SocketTcp not found");
            peerBaseField = typeof(IPhotonSocket).GetField("peerBase", All);
            setState = typeof(IPhotonSocket).GetProperty("State", All)?.GetSetMethod(true);
            handleException = typeof(IPhotonSocket).GetMethod("HandleException", All);
            harmony.Patch(socketTcp.GetMethod("Connect", All, null, Type.EmptyTypes, null),
                prefix: new HarmonyMethod(typeof(VirtualSockets), nameof(Connect_Prefix)));
            harmony.Patch(socketTcp.GetMethod("Send", All),
                prefix: new HarmonyMethod(typeof(VirtualSockets), nameof(Send_Prefix)));
            harmony.Patch(socketTcp.GetMethod("Disconnect", All),
                prefix: new HarmonyMethod(typeof(VirtualSockets), nameof(Disconnect_Prefix)));
        }

        /// <summary>Split "host:port" (the port is required by Photon but meaningless here).</summary>
        public static string HostOf(string address)
        {
            int colon = address.LastIndexOf(':');
            return colon > 0 ? address.Substring(0, colon) : address;
        }

        private static bool Connect_Prefix(IPhotonSocket __instance, ref bool __result)
        {
            var peer = peerBaseField!.GetValue(__instance);
            var address = peer?.GetType().GetProperty("ServerAddress", All)?.GetValue(peer, null) as string ?? "";
            var connect = Connect;
            if (connect == null) return true;
            var socket = __instance;
            var link = connect(HostOf(address), frame => Deliver(socket, frame), () => RemoteClosed(socket));
            if (link == null) return true;

            lock (links) links[socket] = link;
            setState!.Invoke(socket, new object[] { PhotonSocketState.Connected });
            __result = true;

            // SocketTcp reports OnConnect from its connect thread, after TPeer.Connect has set the peer to
            // Connecting; do the same, so the init message isn't dropped by a still-Disconnected peer.
            var stateField = peer!.GetType().GetField("peerConnectionState", All);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 200 && Convert.ToInt32(stateField!.GetValue(peer)) == 0; i++) Thread.Sleep(5);
                peer.GetType().GetMethod("OnConnect", All)!.Invoke(peer, null);
            });
            return false;
        }

        private static bool Send_Prefix(IPhotonSocket __instance, byte[] data, int length, ref PhotonSocketError __result)
        {
            IClientLink? link;
            lock (links) links.TryGetValue(__instance, out link);
            if (link == null) return true;
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            try
            {
                link.Send(copy);
                __result = PhotonSocketError.Success;
            }
            catch (Exception)
            {
                __result = PhotonSocketError.Exception;
            }
            return false;
        }

        private static bool Disconnect_Prefix(IPhotonSocket __instance, ref bool __result)
        {
            IClientLink? link;
            lock (links)
            {
                if (!links.TryGetValue(__instance, out link)) return true;
                links.Remove(__instance);
            }
            setState!.Invoke(__instance, new object[] { PhotonSocketState.Disconnecting });
            try { link.Close(); } catch { /* already gone */ }
            setState.Invoke(__instance, new object[] { PhotonSocketState.Disconnected });
            __result = true;
            return false;
        }

        // Server frame -> what SocketTcp's receive loop passes on: 9-byte ping replies as they are,
        // framed messages without their 7-byte TCP header (starting at 0xF3).
        private static void Deliver(IPhotonSocket socket, byte[] frame)
        {
            if (frame.Length == 0) return;
            if (frame[0] == 0xF0)
            {
                socket.HandleReceivedDatagram(frame, frame.Length, false);
                return;
            }
            if (frame.Length < 9) return;
            var payload = new byte[frame.Length - 7];
            Buffer.BlockCopy(frame, 7, payload, 0, payload.Length);
            socket.HandleReceivedDatagram(payload, payload.Length, false);
        }

        private static void RemoteClosed(IPhotonSocket socket)
        {
            lock (links)
            {
                if (!links.Remove(socket)) return;
            }
            handleException?.Invoke(socket, new object[] { StatusCode.DisconnectByServer });
        }
    }
}
