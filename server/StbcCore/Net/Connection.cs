using System;
using System.IO;
using System.Text;
using System.Threading;
using StbcServer.Protocol;

namespace StbcServer.Net;

public interface IPeerHandler
{
    void OnConnected(Connection connection);
    void OnOperation(Connection connection, OperationRequest request);
    void OnDisconnected(Connection connection);
}

/// <summary>How a connection's bytes leave the server (TCP socket, in-process pipe, Steam P2P, ...).</summary>
public interface ITransport
{
    /// <summary>Deliver one complete server frame (framed message or 9-byte ping reply) to the client.</summary>
    void Send(byte[] frame);
    void Close();
}

/// <summary>Server side of Photon's key exchange (Diffie-Hellman, then AES on encrypted messages).</summary>
public interface ICryptoSession
{
    /// <summary>Derive the shared key from the client's public key; returns the server's public key.</summary>
    byte[] Establish(byte[] clientPublicKey);
    byte[] Decrypt(byte[] data, int offset, int count);
}

/// <summary>
/// One client connection speaking Photon's framed TCP protocol (TPeer / SocketTcp), independent of how
/// the bytes travel:
///   client to server: 0xFB, int32 total length, channel, reliable flag, then 0xF3, message type, body
///                     0xF0, int32 client time  (ping, unframed)
///   server to client: 0xFB, int32 total length, channel, flag, 0xF3, message type, body
///                     0xF0, int32 server time, int32 client time (ping reply, 9 bytes)
/// Message types: 0 init, 1 init response, 2 operation, 3 operation response, 4 event,
/// 6 internal operation (key exchange), 7 internal response. Bit 0x80 marks an encrypted body.
/// </summary>
public sealed class Connection
{
    private const int MaxFrame = 4 * 1024 * 1024;
    private static int nextId;

    private readonly IPeerHandler handler;
    private readonly ITransport transport;
    private readonly Func<ICryptoSession> cryptoFactory;
    private readonly object receiveLock = new();
    private byte[] buffer = new byte[4096];
    private int buffered;
    private ICryptoSession? crypto;
    private int closed;

    public Connection(IPeerHandler handler, ITransport transport, Func<ICryptoSession> cryptoFactory, string description, string gameServerAddress)
    {
        this.handler = handler;
        this.transport = transport;
        this.cryptoFactory = cryptoFactory;
        Description = description;
        GameServerAddress = gameServerAddress;
        LastReceive = Environment.TickCount;
    }

    public int Id { get; } = Interlocked.Increment(ref nextId);
    public string Description { get; }

    /// <summary>Address this server hands out for its own game-server role, as this client reaches it.</summary>
    public string GameServerAddress { get; }

    public string AppId { get; private set; } = "";

    /// <summary>Server-side state attached by the handler (master or game session).</summary>
    public object? Session { get; set; }

    /// <summary>Environment.TickCount of the last received data (clients ping every second).</summary>
    public int LastReceive { get; private set; }

    public bool IsConnected => Thread.VolatileRead(ref closed) == 0;

    public override string ToString() => $"conn#{Id}({Description})";

    public void Open() => handler.OnConnected(this);

    /// <summary>Close from the server side or because the transport went away. Idempotent.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { transport.Close(); } catch { /* already gone */ }
        try { handler.OnDisconnected(this); }
        catch (Exception e) { Log.Error($"{this} disconnect handler failed: {e}"); }
    }

    // ---- Receiving -----------------------------------------------------------------------

    /// <summary>Feed bytes from the client, in any chunking. Complete messages are processed immediately.</summary>
    public void Receive(byte[] data, int offset, int count)
    {
        if (!IsConnected) return;
        lock (receiveLock)
        {
            LastReceive = Environment.TickCount;
            if (buffered + count > buffer.Length)
            {
                var bigger = new byte[Math.Max(buffer.Length * 2, buffered + count)];
                Buffer.BlockCopy(buffer, 0, bigger, 0, buffered);
                buffer = bigger;
            }
            Buffer.BlockCopy(data, offset, buffer, buffered, count);
            buffered += count;

            int pos = 0;
            try
            {
                while (pos < buffered)
                {
                    int used = TryHandle(pos);
                    if (used == 0) break;
                    pos += used;
                }
            }
            catch (Exception e)
            {
                // (No exception filters: they're avoided for Unity 5.4's old Mono runtime.)
                if (e is not (InvalidDataException or ArgumentException or IndexOutOfRangeException)) throw;
                Log.Warn($"{this} protocol error: {e.Message}");
                Close();
                return;
            }
            if (pos > 0)
            {
                Buffer.BlockCopy(buffer, pos, buffer, 0, buffered - pos);
                buffered -= pos;
            }
        }
    }

    /// <summary>Handle one message at pos if complete; returns bytes consumed or 0 if incomplete.</summary>
    private int TryHandle(int pos)
    {
        int available = buffered - pos;
        switch (buffer[pos])
        {
            case 0xF0:
            {
                if (available < 5) return 0;
                var reply = new byte[9];
                reply[0] = 0xF0;
                WriteInt32(reply, 1, ServerTime.Now);
                Buffer.BlockCopy(buffer, pos + 1, reply, 5, 4);
                transport.Send(reply);
                return 5;
            }
            case 0xFB:
            {
                if (available < 5) return 0;
                int total = ReadInt32(buffer, pos + 1);
                if (total < 9 || total > MaxFrame)
                    throw new InvalidDataException($"bad frame length {total}");
                if (available < total) return 0;
                var rest = new byte[total - 5];
                Buffer.BlockCopy(buffer, pos + 5, rest, 0, rest.Length);
                HandleMessage(rest); // rest: channel, reliable, 0xF3, message type, body
                return total;
            }
            default:
                throw new InvalidDataException($"unexpected magic byte 0x{buffer[pos]:X2}");
        }
    }

    private void HandleMessage(byte[] rest)
    {
        if (rest[2] != 0xF3)
            throw new InvalidDataException($"unexpected message magic 0x{rest[2]:X2}");
        byte type = (byte)(rest[3] & 0x7F);
        bool encrypted = (rest[3] & 0x80) != 0;

        if (type == 0)
        {
            // Init: INIT_BYTES starts at rest[2]: 0xF3 0x00, protocol version (2), flags, client version (3),
            // 0, then app id (32 bytes, zero padded) at INIT_BYTES[9].
            AppId = rest.Length >= 2 + 41 ? Encoding.ASCII.GetString(rest, 2 + 9, 32).TrimEnd('\0') : "";
            Log.Debug($"{this} init, app id '{AppId}'");
            SendMessage(1, new byte[0]);
            return;
        }

        byte[] body;
        int offset;
        if (encrypted)
        {
            if (crypto == null) throw new InvalidDataException("encrypted message before key exchange");
            body = crypto.Decrypt(rest, 4, rest.Length - 4);
            offset = 0;
        }
        else
        {
            body = rest;
            offset = 4;
        }

        var request = Protocol16.ReadOperationRequest(new Reader(body, offset));
        switch (type)
        {
            case 2:
                handler.OnOperation(this, request);
                break;
            case 6:
                HandleInternal(request);
                break;
            default:
                Log.Warn($"{this} ignoring message type {type}");
                break;
        }
    }

    private void HandleInternal(OperationRequest request)
    {
        switch (request.OperationCode)
        {
            case 0: // InitEncryption: [1] = client public key
            {
                if (request[1] is not byte[] clientKey || clientKey.Length == 0)
                {
                    SendInternal(new OperationResponse(0, -1, "missing client key"));
                    return;
                }
                crypto = cryptoFactory();
                var resp = new OperationResponse(0);
                resp[1] = crypto.Establish(clientKey);
                SendInternal(resp);
                Log.Debug($"{this} encryption established");
                break;
            }
            case 1: // Ping as internal operation (unframed TCP mode)
            {
                var resp = new OperationResponse(1);
                resp[1] = request[1];
                resp[2] = ServerTime.Now;
                SendInternal(resp);
                break;
            }
            default:
                SendInternal(new OperationResponse(request.OperationCode, -2, "unknown internal operation"));
                break;
        }
    }

    // ---- Sending -------------------------------------------------------------------------

    public void SendResponse(OperationResponse response)
    {
        if (response.ReturnCode != 0)
            Log.Debug($"{this} op {response.OperationCode} -> error {response.ReturnCode} {response.DebugMessage}");
        var w = new Writer();
        Protocol16.WriteOperationResponse(w, response);
        SendMessage(3, w.ToArray());
    }

    public void SendEvent(EventData ev)
    {
        var w = new Writer();
        Protocol16.WriteEvent(w, ev);
        SendMessage(4, w.ToArray());
    }

    /// <summary>Send an already serialized event body (used to fan one event out to many connections).</summary>
    public void SendEventBody(byte[] body) => SendMessage(4, body);

    private void SendInternal(OperationResponse response)
    {
        var w = new Writer();
        Protocol16.WriteOperationResponse(w, response);
        SendMessage(7, w.ToArray());
    }

    private void SendMessage(byte messageType, byte[] body)
    {
        if (!IsConnected) return;
        var frame = new byte[9 + body.Length];
        frame[0] = 0xFB;
        WriteInt32(frame, 1, frame.Length);
        frame[5] = 0; // channel
        frame[6] = 1; // reliable (TCP: informational only)
        frame[7] = 0xF3;
        frame[8] = messageType;
        Buffer.BlockCopy(body, 0, frame, 9, body.Length);
        try { transport.Send(frame); }
        catch (Exception e)
        {
            Log.Debug($"{this} send failed: {e.Message}");
            Close();
        }
    }

    private static int ReadInt32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    private static void WriteInt32(byte[] b, int i, int v)
    {
        b[i] = (byte)(v >> 24);
        b[i + 1] = (byte)(v >> 16);
        b[i + 2] = (byte)(v >> 8);
        b[i + 3] = (byte)v;
    }
}

public static class ServerTime
{
    public static int Now => Environment.TickCount;
}
