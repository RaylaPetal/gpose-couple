using System.Net.Sockets;
using System.Threading.Channels;
using CoPose.Protocol;

namespace CoPose.Core.Net;

public sealed record ConnectionOptions
{
    public static readonly ConnectionOptions Default = new();

    /// <summary>Send a ping when nothing has been sent for this long.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Close the connection when nothing has been received for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a graceful close may spend flushing queued frames.</summary>
    public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// A TCP connection carrying CoPose frames: one reader loop, one writer loop fed by a channel
/// (so writes never interleave), and a keepalive loop.
/// </summary>
public sealed class FrameConnection : IAsyncDisposable
{
    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly ConnectionOptions options;
    private readonly Channel<Frame> outbound = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource cts = new();
    private readonly TaskCompletionSource<string> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Action<Frame>? onFrame;
    private long lastSentTicks;
    private long lastReceivedTicks;
    private int closing;
    private Task[] loops = [];

    public FrameConnection(Socket socket, ConnectionOptions? options = null)
    {
        this.socket = socket;
        this.options = options ?? ConnectionOptions.Default;
        socket.NoDelay = true;
        stream = new NetworkStream(socket, ownsSocket: false);
        RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "?";
    }

    public string RemoteEndPoint { get; }

    /// <summary>Completes with the close reason once the connection is fully closed.</summary>
    public Task<string> Closed => closed.Task;

    public bool IsOpen => Volatile.Read(ref closing) == 0;

    /// <summary>Starts the loops. <paramref name="handler"/> runs on the reader loop for every frame except pings.</summary>
    public void Start(Action<Frame> handler)
    {
        onFrame = handler;
        var now = Environment.TickCount64;
        lastSentTicks = now;
        lastReceivedTicks = now;
        loops = [Task.Run(ReadLoop), Task.Run(WriteLoop), Task.Run(KeepaliveLoop)];
    }

    /// <summary>Replaces the frame handler (used once a handshake completes).</summary>
    public void SetHandler(Action<Frame> handler) => onFrame = handler;

    public bool Send(Frame frame) => IsOpen && outbound.Writer.TryWrite(frame);

    /// <summary>Queues <paramref name="final"/> (if any), flushes, then closes.</summary>
    public void CloseGracefully(string reason, Frame? final = null)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0)
            return;
        if (final != null)
            outbound.Writer.TryWrite(final);
        outbound.Writer.TryComplete();
        _ = FinishAfterFlush(reason);
    }

    /// <summary>Closes immediately, dropping anything queued.</summary>
    public void Abort(string reason)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0)
            return;
        outbound.Writer.TryComplete();
        Finish(reason);
    }

    private async Task FinishAfterFlush(string reason)
    {
        var writer = loops.Length > 1 ? loops[1] : Task.CompletedTask;
        await Task.WhenAny(writer, Task.Delay(options.FlushTimeout)).ConfigureAwait(false);
        Finish(reason);
    }

    private void Finish(string reason)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        try { socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
        socket.Dispose();
        closed.TrySetResult(reason);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(stream, cts.Token).ConfigureAwait(false);
                if (frame == null)
                {
                    Abort("connection closed by peer");
                    return;
                }

                Volatile.Write(ref lastReceivedTicks, Environment.TickCount64);
                if (frame is PingFrame)
                    continue;

                onFrame?.Invoke(frame);
            }
        }
        catch (FrameTooLargeException e)
        {
            Abort(e.Message);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // closing
        }
        catch (Exception e)
        {
            Abort($"read failed: {e.Message}");
        }
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in outbound.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                await FrameCodec.WriteAsync(stream, frame, cts.Token).ConfigureAwait(false);
                Volatile.Write(ref lastSentTicks, Environment.TickCount64);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // closing
        }
        catch (Exception e)
        {
            Abort($"write failed: {e.Message}");
        }
    }

    private async Task KeepaliveLoop()
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(10, Math.Min(options.PingInterval.TotalMilliseconds, options.IdleTimeout.TotalMilliseconds) / 4));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref lastReceivedTicks) >= options.IdleTimeout.TotalMilliseconds)
                {
                    Abort("timed out");
                    return;
                }
                if (now - Volatile.Read(ref lastSentTicks) >= options.PingInterval.TotalMilliseconds)
                {
                    Volatile.Write(ref lastSentTicks, now);
                    Send(new PingFrame());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
    }

    public async ValueTask DisposeAsync()
    {
        Abort("disposed");
        try { await Task.WhenAll(loops).ConfigureAwait(false); } catch { /* loops swallow their own errors */ }
        await stream.DisposeAsync().ConfigureAwait(false);
        cts.Dispose();
    }
}
