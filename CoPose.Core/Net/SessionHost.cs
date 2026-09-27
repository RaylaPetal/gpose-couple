using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using CoPose.Protocol;

namespace CoPose.Core.Net;

public sealed record SessionHostOptions
{
    /// <summary>Port to listen on. 0 picks a free port (tests).</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;

    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public ConnectionOptions Connection { get; init; } = ConnectionOptions.Default;
}

/// <summary>
/// Hosts a session inside the plugin: accepts one authenticated guest and sequences every message
/// (the guest's and the host's own) through a single consumer, delivering each to all participants
/// including its sender. Every participant therefore sees the same total order.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private abstract record Item;
    private sealed record MessageItem(Guid Sender, MsgType Type, byte[] Body) : Item;
    private sealed record AddGuestItem(Guest Guest) : Item;
    private sealed record RemoveGuestItem(Guest Guest) : Item;

    private sealed record Guest(PeerInfo Info, FrameConnection Connection);

    private readonly SessionHostOptions options;
    private readonly TcpListener listener;
    private readonly Channel<Item> items = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LocalParticipant local;
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptLoop;
    private readonly Task consumer;

    private Guest? guest; // consumer-only
    private Guest? claimedGuest; // the guest holding the slot, for StopAsync
    private int guestSlot; // 0 free, 1 claimed
    private long seq;
    private int stopped;

    private SessionHost(PeerInfo self, SessionHostOptions options, TcpListener listener)
    {
        this.options = options;
        this.listener = listener;
        Secret = RandomNumberGenerator.GetBytes(ProtocolInfo.SecretLength);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        local = new LocalParticipant(this, self);
        acceptLoop = Task.Run(AcceptLoop);
        consumer = Task.Run(ConsumeLoop);
    }

    public byte[] Secret { get; }

    public int Port { get; }

    /// <summary>The host's own participant view of the session.</summary>
    public ISessionTransport Local => local;

    public bool HasGuest => Volatile.Read(ref guestSlot) == 1;

    /// <exception cref="SessionException">The port is already in use or cannot be bound.</exception>
    public static SessionHost Start(PeerInfo self, SessionHostOptions? options = null)
    {
        options ??= new SessionHostOptions();
        var listener = new TcpListener(options.BindAddress, options.Port);
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new SessionException(SessionError.PortInUse, $"Port {options.Port} is already in use.", e);
        }
        return new SessionHost(self, options, listener);
    }

    /// <summary>An invite listing <paramref name="endpoints"/> in order (duplicates removed, at most <see cref="Invite.MaxEndpoints"/>).</summary>
    public Invite CreateInvite(IEnumerable<IPEndPoint> endpoints) =>
        new(ProtocolInfo.Version, Secret, endpoints.Distinct().Take(Invite.MaxEndpoints).ToArray());

    /// <summary>An invite for addresses reachable on the host's own listen port.</summary>
    public Invite CreateInvite(IEnumerable<IPAddress> addresses) =>
        CreateInvite(addresses.Select(a => new IPEndPoint(a, Port)));

    private async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => Handshake(socket));
        }
    }

    private async Task Handshake(Socket socket)
    {
        var connection = new FrameConnection(socket, options.Connection);
        var hello = new TaskCompletionSource<HelloFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Start(frame =>
        {
            if (frame is HelloFrame h)
                hello.TrySetResult(h);
            else
                connection.Abort("expected hello");
        });

        var first = await Task.WhenAny(hello.Task, connection.Closed, Task.Delay(options.HelloTimeout, cts.Token)).ConfigureAwait(false);
        if (first != hello.Task)
        {
            connection.Abort("no hello");
            return;
        }

        var h = hello.Task.Result;
        if (h.Version != ProtocolInfo.Version)
        {
            Reject(connection, RejectReasons.VersionMismatch, $"host uses protocol {ProtocolInfo.Version}, guest uses {h.Version}");
            return;
        }
        if (h.Secret is null || !CryptographicOperations.FixedTimeEquals(h.Secret, Secret))
        {
            Reject(connection, RejectReasons.InvalidInvite, "invalid invite");
            return;
        }
        if (Volatile.Read(ref stopped) != 0 || Interlocked.CompareExchange(ref guestSlot, 1, 0) != 0)
        {
            Reject(connection, RejectReasons.SessionFull, "session full");
            return;
        }

        var g = new Guest(h.Self, connection);
        Volatile.Write(ref claimedGuest, g);
        connection.SetHandler(frame => OnGuestFrame(g, frame));
        if (!items.Writer.TryWrite(new AddGuestItem(g)))
        {
            connection.CloseGracefully(ByeReasons.SessionEnded, new ByeFrame(ByeReasons.SessionEnded));
            return;
        }
        _ = connection.Closed.ContinueWith(_ => items.Writer.TryWrite(new RemoveGuestItem(g)), TaskScheduler.Default);
    }

    private static void Reject(FrameConnection connection, string reason, string detail) =>
        connection.CloseGracefully(reason, new RejectFrame(reason, detail));

    private void OnGuestFrame(Guest g, Frame frame)
    {
        switch (frame)
        {
            case SendFrame send when send.Body is { Length: <= ProtocolInfo.MaxBodyBytes }:
                items.Writer.TryWrite(new MessageItem(g.Info.ClientId, send.Type, send.Body));
                break;
            case SendFrame:
                g.Connection.Abort("message body too large");
                break;
            case ByeFrame:
                g.Connection.CloseGracefully(ByeReasons.Left);
                break;
        }
    }

    private async Task ConsumeLoop()
    {
        await foreach (var item in items.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (item)
            {
                case MessageItem m:
                    var envelope = new Envelope(m.Type, m.Sender, ++seq, m.Body);
                    local.Push(new MessageEvent(envelope));
                    guest?.Connection.Send(new DeliverFrame(envelope));
                    break;

                case AddGuestItem add:
                    guest = add.Guest;
                    add.Guest.Connection.Send(new WelcomeFrame(local.Self.ClientId, [local.Self, add.Guest.Info]));
                    local.Push(new PeerJoinedEvent(add.Guest.Info));
                    break;

                case RemoveGuestItem remove when ReferenceEquals(guest, remove.Guest):
                    guest = null;
                    Interlocked.CompareExchange(ref claimedGuest, null, remove.Guest);
                    Volatile.Write(ref guestSlot, 0);
                    local.Push(new PeerLeftEvent(remove.Guest.Info.ClientId));
                    break;
            }
        }
    }

    /// <summary>Ends the session: stops listening, tells the guest, and invalidates the invite.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;

        cts.Cancel();
        listener.Stop();

        var g = Volatile.Read(ref claimedGuest);
        if (g != null)
        {
            g.Connection.CloseGracefully(ByeReasons.SessionEnded, new ByeFrame(ByeReasons.SessionEnded));
            await Task.WhenAny(g.Connection.Closed, Task.Delay(options.Connection.FlushTimeout)).ConfigureAwait(false);
        }

        items.Writer.TryComplete();
        await consumer.ConfigureAwait(false);
        local.End("you stopped hosting");
        try { await acceptLoop.ConfigureAwait(false); } catch { /* cancelled */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        cts.Dispose();
    }

    /// <summary>The host's own participant: sends go straight into the sequencer, no socket.</summary>
    private sealed class LocalParticipant(SessionHost host, PeerInfo self) : ISessionTransport
    {
        private readonly Channel<SessionEvent> events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleWriter = true });

        public PeerInfo Self { get; } = self;

        public IReadOnlyList<PeerInfo> InitialParticipants => [Self];

        public ChannelReader<SessionEvent> Events => events.Reader;

        public SendResult Send(MsgType type, byte[] body)
        {
            if (body.Length > ProtocolInfo.MaxBodyBytes)
                return SendResult.TooLarge;
            return Volatile.Read(ref host.stopped) == 0 && host.items.Writer.TryWrite(new MessageItem(Self.ClientId, type, body))
                ? SendResult.Ok
                : SendResult.NotConnected;
        }

        public Task LeaveAsync() => host.StopAsync();

        internal void Push(SessionEvent e) => events.Writer.TryWrite(e);

        internal void End(string reason)
        {
            events.Writer.TryWrite(new SessionEndedEvent(reason));
            events.Writer.TryComplete();
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }
}
