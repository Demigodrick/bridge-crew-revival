using System;
using System.Collections.Generic;
using System.Linq;

namespace StbcServer.Net;

/// <summary>Client end of a virtual connection to a server: send client bytes, close.</summary>
public interface IClientLink
{
    void Send(byte[] data);
    void Close();
}

/// <summary>
/// In-process connection: the game talking to the server running inside the same game process.
/// </summary>
public static class LocalLink
{
    /// <param name="deliver">Receives each server frame (framed message or ping reply).</param>
    /// <param name="closed">Called when the server closes the connection (not when the client does).</param>
    public static IClientLink Connect(IPeerHandler server, Func<ICryptoSession> crypto, string gameServerAddress,
        Action<byte[]> deliver, Action closed)
    {
        var link = new Link(deliver, closed);
        var connection = new Connection(server, link, crypto, "local", gameServerAddress);
        link.Connection = connection;
        connection.Open();
        return link;
    }

    private sealed class Link : IClientLink, ITransport
    {
        private readonly Action<byte[]> deliver;
        private readonly Action closed;
        private bool clientClosed;

        public Link(Action<byte[]> deliver, Action closed)
        {
            this.deliver = deliver;
            this.closed = closed;
        }

        public Connection? Connection { get; set; }

        // Client side
        public void Send(byte[] data) => Connection!.Receive(data, 0, data.Length);

        void IClientLink.Close()
        {
            clientClosed = true;
            Connection!.Close();
        }

        // Server side
        void ITransport.Send(byte[] frame) => deliver(frame);

        void ITransport.Close()
        {
            if (!clientClosed) closed();
        }
    }
}

/// <summary>
/// Photon connections multiplexed over a peer-to-peer message channel (Steam P2P in the game).
/// Packets: kind (1 open, 2 data, 3 close), int32 connection id (big-endian), payload.
/// Data packets carry client bytes towards the host and server frames back; the channel must be
/// reliable and ordered.
/// </summary>
public static class Relay
{
    public const byte Open = 1, Data = 2, CloseKind = 3;

    public static byte[] Packet(byte kind, int id, byte[]? payload = null)
    {
        var p = new byte[5 + (payload?.Length ?? 0)];
        p[0] = kind;
        p[1] = (byte)(id >> 24); p[2] = (byte)(id >> 16); p[3] = (byte)(id >> 8); p[4] = (byte)id;
        if (payload != null) Buffer.BlockCopy(payload, 0, p, 5, payload.Length);
        return p;
    }

    public static bool TryParse(byte[] packet, int length, out byte kind, out int id, out byte[] payload)
    {
        kind = 0; id = 0; payload = new byte[0];
        if (length < 5) return false;
        kind = packet[0];
        id = (packet[1] << 24) | (packet[2] << 16) | (packet[3] << 8) | packet[4];
        payload = new byte[length - 5];
        Buffer.BlockCopy(packet, 5, payload, 0, payload.Length);
        return true;
    }
}

/// <summary>Host side of the relay: remote players' connections into the local server.</summary>
public sealed class RelayHost
{
    private readonly IPeerHandler server;
    private readonly Func<ICryptoSession> crypto;
    private readonly Action<ulong, byte[]> send;
    private readonly Dictionary<string, Connection> connections = new();

    public RelayHost(IPeerHandler server, Func<ICryptoSession> crypto, Action<ulong, byte[]> send)
    {
        this.server = server;
        this.crypto = crypto;
        this.send = send;
    }

    public int ConnectionCount { get { lock (connections) return connections.Count; } }

    public void OnPacket(ulong from, byte[] packet, int length)
    {
        if (!Relay.TryParse(packet, length, out var kind, out var id, out var payload)) return;
        var key = from + "/" + id;
        Connection? connection;
        switch (kind)
        {
            case Relay.Open:
                connection = new Connection(server, new Transport(this, from, id, key), crypto, "p2p " + key, "steam-host");
                lock (connections)
                {
                    if (connections.TryGetValue(key, out var old)) old.Close();
                    connections[key] = connection;
                }
                connection.Open();
                break;
            case Relay.Data:
                lock (connections) connections.TryGetValue(key, out connection);
                if (connection != null) connection.Receive(payload, 0, payload.Length);
                else send(from, Relay.Packet(Relay.CloseKind, id)); // unknown: tell the client it's gone
                break;
            case Relay.CloseKind:
                lock (connections)
                {
                    if (!connections.TryGetValue(key, out connection)) return;
                    connections.Remove(key);
                }
                connection.Close();
                break;
        }
    }

    /// <summary>The remote peer is unreachable (P2P session failed): drop all its connections.</summary>
    public void PeerLost(ulong peer)
    {
        List<Connection> lost;
        lock (connections)
        {
            var keys = connections.Keys.Where(k => k.StartsWith(peer + "/")).ToList();
            lost = keys.Select(k => connections[k]).ToList();
            foreach (var k in keys) connections.Remove(k);
        }
        foreach (var c in lost) c.Close();
    }

    private sealed class Transport : ITransport
    {
        private readonly RelayHost host;
        private readonly ulong peer;
        private readonly int id;
        private readonly string key;

        public Transport(RelayHost host, ulong peer, int id, string key)
        {
            this.host = host;
            this.peer = peer;
            this.id = id;
            this.key = key;
        }

        public void Send(byte[] frame) => host.send(peer, Relay.Packet(Relay.Data, id, frame));

        public void Close()
        {
            bool known;
            lock (host.connections)
            {
                known = host.connections.Remove(key);
            }
            if (known) host.send(peer, Relay.Packet(Relay.CloseKind, id));
        }
    }
}

/// <summary>Client side of the relay: this player's connections to remote hosts.</summary>
public sealed class RelayClient
{
    private readonly Action<ulong, byte[]> send;
    private readonly Dictionary<int, Link> links = new();
    private int nextId = new Random().Next(1, int.MaxValue / 2);

    public RelayClient(Action<ulong, byte[]> send) => this.send = send;

    public IClientLink Connect(ulong host, Action<byte[]> deliver, Action closed)
    {
        Link link;
        lock (links)
        {
            link = new Link(this, host, nextId++, deliver, closed);
            links[link.Id] = link;
        }
        send(host, Relay.Packet(Relay.Open, link.Id));
        return link;
    }

    public void OnPacket(ulong from, byte[] packet, int length)
    {
        if (!Relay.TryParse(packet, length, out var kind, out var id, out var payload)) return;
        Link? link;
        lock (links) links.TryGetValue(id, out link);
        if (link == null || link.Host != from) return;
        if (kind == Relay.Data) link.Deliver(payload);
        else if (kind == Relay.CloseKind) link.RemoteClosed();
    }

    /// <summary>The host is unreachable (P2P session failed): close every link to it.</summary>
    public void PeerLost(ulong host)
    {
        List<Link> lost;
        lock (links) lost = links.Values.Where(l => l.Host == host).ToList();
        foreach (var l in lost) l.RemoteClosed();
    }

    private sealed class Link : IClientLink
    {
        private readonly RelayClient owner;
        private readonly Action<byte[]> deliver;
        private readonly Action closed;

        public Link(RelayClient owner, ulong host, int id, Action<byte[]> deliver, Action closed)
        {
            this.owner = owner;
            Host = host;
            Id = id;
            this.deliver = deliver;
            this.closed = closed;
        }

        public ulong Host { get; }
        public int Id { get; }

        public void Send(byte[] data) => owner.send(Host, Relay.Packet(Relay.Data, Id, data));

        public void Close()
        {
            if (!Forget()) return;
            owner.send(Host, Relay.Packet(Relay.CloseKind, Id));
        }

        public void Deliver(byte[] frame) => deliver(frame);

        public void RemoteClosed()
        {
            if (Forget()) closed();
        }

        private bool Forget()
        {
            lock (owner.links) return owner.links.Remove(Id);
        }
    }
}
