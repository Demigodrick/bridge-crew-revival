using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using StbcServer.Protocol;

namespace StbcServer.Net;

/// <summary>Photon's framed TCP over a socket: reads into the connection, writes its frames in order.</summary>
public sealed class TcpTransport : ITransport
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly Channel<byte[]> outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private TcpTransport(TcpClient client)
    {
        this.client = client;
        client.NoDelay = true;
        stream = client.GetStream();
    }

    public static async Task RunAsync(TcpClient client, IPeerHandler handler, int port, CancellationToken ct)
    {
        var transport = new TcpTransport(client);
        var local = client.Client.LocalEndPoint as IPEndPoint;
        var ip = local?.Address is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : local?.Address ?? IPAddress.Loopback;
        var connection = new Connection(handler, transport, () => new Encryption(),
            client.Client.RemoteEndPoint?.ToString() ?? "?", $"{ip}:{local?.Port ?? port}");
        Log.Debug($"{connection} connected");
        connection.Open();

        var writer = Task.Run(() => transport.WriteLoop(connection, ct), ct);
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                int n = await transport.stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                connection.Receive(buffer, 0, n);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            Log.Debug($"{connection} closed: {e.Message}");
        }
        finally
        {
            connection.Close();
            await writer.ContinueWith(_ => { });
        }
    }

    public void Send(byte[] frame) => outgoing.Writer.TryWrite(frame);

    public void Close()
    {
        outgoing.Writer.TryComplete();
        try { client.Close(); } catch { /* already gone */ }
    }

    private async Task WriteLoop(Connection connection, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in outgoing.Reader.ReadAllAsync(ct))
                await stream.WriteAsync(frame, ct);
        }
        catch (Exception e)
        {
            Log.Debug($"{connection} write failed: {e.Message}");
            connection.Close();
        }
    }
}
