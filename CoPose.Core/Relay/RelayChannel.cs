using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using CoPose.Protocol;

namespace CoPose.Core.Relay;

/// <summary>
/// <see cref="ISceneChannel"/> over a WebSocket to the CoPose relay (<c>{baseUrl}/v1/room/{room}?p={participant}</c>).
/// While a room is joined it keeps reconnecting, with delays doubling from 1 s up to 30 s. A read loop and a send
/// loop run on the thread pool; events are queued for the tick thread.
/// </summary>
public sealed class RelayChannel : ISceneChannel
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Func<string> baseUrl;
    private readonly ConcurrentQueue<ChannelEvent> events = new();
    private readonly object gate = new();
    private readonly TimeSpan initialBackoff;

    private CancellationTokenSource? session;
    private Channel<byte[]>? outbound;
    private Task? loop;
    private volatile RelayStatus status = RelayStatus.Idle;
    private volatile string? lastError;

    /// <param name="baseUrl">Relay base URL (http/https or ws/wss), read on every connect so it can change.</param>
    public RelayChannel(Func<string> baseUrl, TimeSpan? initialBackoff = null)
    {
        this.baseUrl = baseUrl;
        this.initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(1);
    }

    public RelayStatus Status => status;

    public string? LastError => lastError;

    public string? Room { get; private set; }

    public void Join(string roomId, string participantId)
    {
        lock (gate)
        {
            if (Room == roomId && session != null)
                return;
            StopLoop();
            Room = roomId;
            session = new CancellationTokenSource();
            var token = session.Token;
            loop = Task.Run(() => RunAsync(roomId, participantId, token));
        }
    }

    public void Leave()
    {
        lock (gate)
        {
            StopLoop();
            Room = null;
            status = RelayStatus.Idle;
        }
    }

    public bool Send(byte[] frame)
    {
        var queue = outbound;
        return status == RelayStatus.Connected && queue != null && queue.Writer.TryWrite(frame);
    }

    public bool TryReceive(out ChannelEvent channelEvent) => events.TryDequeue(out channelEvent!);

    private void StopLoop()
    {
        session?.Cancel();
        session?.Dispose();
        session = null;
        outbound = null;
        loop = null;
    }

    private async Task RunAsync(string roomId, string participantId, CancellationToken ct)
    {
        var backoff = initialBackoff;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(RelayStatus.Connecting, ct);
            using var socket = new ClientWebSocket();
            var queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(RoomUri(roomId, participantId), connectTimeout.Token).ConfigureAwait(false);
                }

                outbound = queue;
                SetStatus(RelayStatus.Connected, ct);
                lastError = null;
                backoff = initialBackoff;
                events.Enqueue(new ChannelConnected());

                using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sending = SendLoopAsync(socket, queue.Reader, connection.Token);
                var reason = await ReceiveLoopAsync(socket, connection.Token).ConfigureAwait(false);
                connection.Cancel();
                try { await sending.ConfigureAwait(false); } catch (OperationCanceledException) { }

                if (ct.IsCancellationRequested)
                    break;
                lastError = reason;
                events.Enqueue(new ChannelDisconnected(reason));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                lastError = e is OperationCanceledException ? "timed out connecting to the relay" : e.Message;
                events.Enqueue(new ChannelDisconnected(lastError));
            }
            finally
            {
                queue.Writer.TryComplete();
                if (ReferenceEquals(outbound, queue))
                    outbound = null;
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
            }

            SetStatus(RelayStatus.Unreachable, ct);
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>A loop that was told to stop must not overwrite the status Leave() set.</summary>
    private void SetStatus(RelayStatus value, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
            status = value;
    }

    private Uri RoomUri(string roomId, string participantId)
    {
        var root = baseUrl().Trim().TrimEnd('/');
        if (root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            root = "wss://" + root["https://".Length..];
        else if (root.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            root = "ws://" + root["http://".Length..];
        return new Uri($"{root}/v1/room/{roomId}?p={participantId}");
    }

    private static async Task SendLoopAsync(ClientWebSocket socket, ChannelReader<byte[]> frames, CancellationToken ct)
    {
        await foreach (var frame in frames.ReadAllAsync(ct).ConfigureAwait(false))
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>Reads frames until the connection ends; returns why it ended.</summary>
    private async Task<string> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return $"relay closed the connection ({(int?)result.CloseStatus} {result.CloseStatusDescription})".Trim();

                message.Write(buffer, 0, result.Count);
                if (message.Length > ProtocolInfo.MaxFrameBytes + 1)
                    return "relay sent an oversized frame";
                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Binary && message.Length > 0)
                    events.Enqueue(new ChannelFrame(message.ToArray()));
                message.SetLength(0);
            }
            return "closed";
        }
        catch (OperationCanceledException)
        {
            return "closed";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    public void Dispose()
    {
        Task? running;
        lock (gate)
        {
            running = loop;
            StopLoop();
            Room = null;
            status = RelayStatus.Idle;
        }
        try
        {
            running?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // cancelled
        }
    }
}
