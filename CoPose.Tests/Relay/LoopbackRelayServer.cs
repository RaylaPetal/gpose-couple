using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CoPose.Protocol;

namespace CoPose.Tests.Relay;

/// <summary>
/// A minimal WebSocket relay on 127.0.0.1 (hand-rolled HTTP upgrade, no http.sys): rooms by path, forward to the
/// other participant, PeerJoined/PeerLeft. Enough to exercise <see cref="CoPose.Core.Relay.RelayChannel"/> for real.
/// </summary>
internal sealed class LoopbackRelayServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> rooms = new();
    private readonly Task acceptLoop;

    public LoopbackRelayServer()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public int Connections;

    /// <summary>Aborts every open connection (the clients should reconnect).</summary>
    public void DropAll()
    {
        foreach (var room in rooms.Values)
            foreach (var ws in room.Values)
                ws.Abort();
    }

    private async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var request = await ReadHeaders(stream);
        var path = request.Split("\r\n")[0].Split(' ')[1];
        var key = request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));

        var uri = new Uri("http://x" + path);
        var roomId = uri.AbsolutePath.Split('/').Last();
        var participant = System.Web.HttpUtility.ParseQueryString(uri.Query)["p"] ?? "";
        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        Interlocked.Increment(ref Connections);

        var room = rooms.GetOrAdd(roomId, _ => new());
        room[participant] = ws;
        foreach (var (id, other) in room)
        {
            if (id == participant)
                continue;
            await Send(ws, [(byte)FrameKind.PeerJoined]);
            await Send(other, [(byte)FrameKind.PeerJoined]);
        }

        var buffer = new byte[128 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                var frame = buffer[..result.Count];
                foreach (var (id, other) in room)
                {
                    if (id != participant)
                        await Send(other, frame);
                }
            }
        }
        catch (Exception)
        {
            // aborted
        }

        room.TryRemove(new KeyValuePair<string, WebSocket>(participant, ws));
        foreach (var other in room.Values)
            await Send(other, [(byte)FrameKind.PeerLeft]);
    }

    private static async Task Send(WebSocket ws, byte[] frame)
    {
        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception)
        {
            // gone
        }
    }

    private static async Task<string> ReadHeaders(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        listener.Stop();
        DropAll();
        try { await acceptLoop; } catch { /* stopped */ }
        cts.Dispose();
    }
}
