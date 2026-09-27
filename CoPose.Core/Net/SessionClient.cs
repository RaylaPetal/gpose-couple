using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using CoPose.Protocol;

namespace CoPose.Core.Net;

public sealed record SessionClientOptions
{
    public static readonly SessionClientOptions Default = new();

    /// <summary>Per-address TCP connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for the host's welcome or rejection after connecting.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public ConnectionOptions Connection { get; init; } = ConnectionOptions.Default;
}

/// <summary>A guest's connection to a hosted session.</summary>
public sealed class SessionClient : ISessionTransport
{
    private readonly FrameConnection connection;
    private readonly Channel<SessionEvent> events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private int ended;

    private readonly TaskCompletionSource<WelcomeFrame> handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool welcomed; // reader-loop only

    private SessionClient(FrameConnection connection, PeerInfo self, IPEndPoint endPoint)
    {
        this.connection = connection;
        Self = self;
        HostEndPoint = endPoint;
    }

    public PeerInfo Self { get; }

    public IReadOnlyList<PeerInfo> InitialParticipants { get; private set; } = [];

    public IPEndPoint HostEndPoint { get; }

    public ChannelReader<SessionEvent> Events => events.Reader;

    /// <summary>Connects to the first reachable address in the invite and completes the handshake.</summary>
    /// <exception cref="SessionException">The host could not be reached or rejected the join.</exception>
    public static async Task<SessionClient> ConnectAsync(Invite invite, PeerInfo self, SessionClientOptions? options = null, CancellationToken ct = default)
    {
        options ??= SessionClientOptions.Default;

        Socket? socket = null;
        IPEndPoint? endPoint = null;
        var failures = new List<string>();
        foreach (var candidateEndPoint in invite.Endpoints)
        {
            endPoint = candidateEndPoint;
            var candidate = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ConnectTimeout);
            try
            {
                await candidate.ConnectAsync(endPoint, timeout.Token).ConfigureAwait(false);
                socket = candidate;
                break;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                candidate.Dispose();
                failures.Add($"{endPoint}: {(e is OperationCanceledException ? "timed out" : e.Message)}");
            }
        }

        if (socket == null || endPoint == null)
        {
            throw new SessionException(SessionError.HostUnreachable,
                "Could not reach host (" + string.Join("; ", failures) + "). The host may need UPnP, a port-forward, or a shared LAN/VPN.");
        }

        var connection = new FrameConnection(socket, options.Connection);
        var client = new SessionClient(connection, self, endPoint);
        var handshake = client.handshake;
        connection.Start(client.OnFrame);
        connection.Send(new HelloFrame(ProtocolInfo.Version, invite.Secret, self));

        var first = await Task.WhenAny(handshake.Task, connection.Closed, Task.Delay(options.HandshakeTimeout, ct)).ConfigureAwait(false);
        if (first != handshake.Task)
        {
            // A reject is followed by the host closing; prefer the reject if it raced the close.
            await Task.WhenAny(handshake.Task, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
            if (!handshake.Task.IsCompleted)
            {
                connection.Abort("handshake failed");
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new SessionException(SessionError.HandshakeFailed, "The host did not answer the join request.");
            }
        }

        if (handshake.Task.IsFaulted)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw handshake.Task.Exception!.InnerException!;
        }

        client.InitialParticipants = handshake.Task.Result.Participants;
        _ = connection.Closed.ContinueWith(t => client.End(t.Result == ByeReasons.SessionEnded ? "the host ended the session" : t.Result), TaskScheduler.Default);
        return client;
    }

    private void OnFrame(Frame frame)
    {
        if (!welcomed)
        {
            switch (frame)
            {
                case WelcomeFrame welcome:
                    welcomed = true;
                    handshake.TrySetResult(welcome);
                    break;
                case RejectFrame reject:
                    handshake.TrySetException(SessionException.FromReject(reject));
                    break;
            }
            return;
        }

        switch (frame)
        {
            case DeliverFrame deliver:
                events.Writer.TryWrite(new MessageEvent(deliver.Envelope));
                break;
            case PeerJoinedFrame joined:
                events.Writer.TryWrite(new PeerJoinedEvent(joined.Peer));
                break;
            case PeerLeftFrame left:
                events.Writer.TryWrite(new PeerLeftEvent(left.ClientId));
                break;
            case ByeFrame bye:
                connection.CloseGracefully(bye.Reason);
                break;
        }
    }

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref ended, 1) != 0)
            return;
        events.Writer.TryWrite(new SessionEndedEvent(reason));
        events.Writer.TryComplete();
    }

    public SendResult Send(MsgType type, byte[] body)
    {
        if (body.Length > ProtocolInfo.MaxBodyBytes)
            return SendResult.TooLarge;
        return connection.Send(new SendFrame(type, body)) ? SendResult.Ok : SendResult.NotConnected;
    }

    public async Task LeaveAsync()
    {
        connection.CloseGracefully(ByeReasons.Left, new ByeFrame(ByeReasons.Left));
        await connection.Closed.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.IsOpen)
            await LeaveAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
