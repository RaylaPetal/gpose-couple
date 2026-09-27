using CoPose.Core;
using CoPose.Core.Relay;
using CoPose.Core.Tags;
using CoPose.Protocol;

namespace CoPose.Tests.Sync;

/// <summary>
/// In-memory stand-in for the Cloudflare relay with the same semantics as CoPose.Relay: two participants per room,
/// forward without echo, store each participant's latest FULL frame and replay it on connect, announce joins/leaves.
/// Delivery is immediate.
/// </summary>
internal sealed class FakeRelay
{
    private sealed class Room
    {
        public readonly Dictionary<string, FakeSceneChannel> Members = [];
        public readonly Dictionary<string, byte[]> Stored = [];
    }

    private readonly Dictionary<string, Room> rooms = [];

    /// <summary>When false, connection attempts fail (relay unreachable).</summary>
    public bool Online { get; set; } = true;

    /// <summary>When false, frames are neither forwarded nor stored (lost in transit).</summary>
    public bool Deliver { get; set; } = true;

    public int RoomCount => rooms.Count;

    public IReadOnlyCollection<string> Participants(string room) =>
        rooms.TryGetValue(room, out var r) ? r.Members.Keys : [];

    internal string? Connect(FakeSceneChannel channel, string roomId, string participant)
    {
        if (!Online)
            return "relay unreachable";

        if (!rooms.TryGetValue(roomId, out var room))
            rooms[roomId] = room = new Room();
        if (!room.Members.ContainsKey(participant) && room.Members.Count >= 2)
            return "room full";

        if (room.Members.TryGetValue(participant, out var old) && old != channel)
            old.Dropped("replaced");
        room.Members[participant] = channel;
        channel.Connected();

        foreach (var (id, other) in room.Members)
        {
            if (id == participant)
                continue;
            channel.Deliver([(byte)FrameKind.PeerJoined]);
            other.Deliver([(byte)FrameKind.PeerJoined]);
        }
        foreach (var (id, frame) in room.Stored)
        {
            if (id != participant)
                channel.Deliver(frame);
        }
        return null;
    }

    internal void Disconnect(FakeSceneChannel channel, string roomId, string participant)
    {
        if (!rooms.TryGetValue(roomId, out var room) || !room.Members.TryGetValue(participant, out var member) || member != channel)
            return;
        room.Members.Remove(participant);
        foreach (var other in room.Members.Values)
            other.Deliver([(byte)FrameKind.PeerLeft]);
    }

    internal void Send(string roomId, string participant, byte[] frame)
    {
        if (!Deliver || !rooms.TryGetValue(roomId, out var room))
            return;
        if (frame[0] == (byte)FrameKind.Full)
            room.Stored[participant] = frame;
        foreach (var (id, other) in room.Members)
        {
            if (id != participant)
                other.Deliver(frame);
        }
    }
}

internal sealed class FakeSceneChannel(FakeRelay relay) : ISceneChannel
{
    private readonly Queue<ChannelEvent> inbox = new();
    private string? participant;

    public RelayStatus Status { get; private set; } = RelayStatus.Idle;

    public string? LastError { get; private set; }

    public string? Room { get; private set; }

    public List<byte[]> Sent { get; } = [];

    public IEnumerable<(FrameKind Kind, SceneMessage? Message)> SentMessages =>
        Sent.Select(f => SceneCodec.TryDecode(f, out var k, out var m) ? (k, m) : ((FrameKind)0, null));

    public int SentCount(FrameKind kind) => Sent.Count(f => f[0] == (byte)kind);

    public void Join(string roomId, string participantId)
    {
        if (Room == roomId && Status == RelayStatus.Connected)
            return;
        Leave();
        Room = roomId;
        participant = participantId;
        Reconnect();
    }

    /// <summary>Tries to (re)connect to the joined room, as the real channel's backoff loop would.</summary>
    public void Reconnect()
    {
        if (Room == null || participant == null)
            return;
        Status = RelayStatus.Connecting;
        var error = relay.Connect(this, Room, participant);
        if (error != null)
        {
            Status = RelayStatus.Unreachable;
            LastError = error;
            inbox.Enqueue(new ChannelDisconnected(error));
        }
    }

    /// <summary>Simulates a dropped connection (the relay sees the participant leave).</summary>
    public void Drop()
    {
        if (Room != null && participant != null)
            relay.Disconnect(this, Room, participant);
        Dropped("connection lost");
    }

    public void Leave()
    {
        if (Room != null && participant != null && Status == RelayStatus.Connected)
            relay.Disconnect(this, Room, participant);
        Room = null;
        participant = null;
        Status = RelayStatus.Idle;
    }

    public bool Send(byte[] frame)
    {
        if (Status != RelayStatus.Connected || Room == null || participant == null)
            return false;
        Sent.Add(frame);
        relay.Send(Room, participant, frame);
        return true;
    }

    public bool TryReceive(out ChannelEvent channelEvent) => inbox.TryDequeue(out channelEvent!);

    internal void Connected()
    {
        Status = RelayStatus.Connected;
        LastError = null;
        inbox.Enqueue(new ChannelConnected());
    }

    internal void Deliver(byte[] frame) => inbox.Enqueue(new ChannelFrame(frame));

    internal void Dropped(string reason)
    {
        Status = RelayStatus.Unreachable;
        LastError = reason;
        inbox.Enqueue(new ChannelDisconnected(reason));
    }

    public void Dispose() => Leave();
}

/// <summary>Two players, A and B, who can both see both characters; tags via a fake SimpleHeels hub, scene via a fake relay.</summary>
internal sealed class TwoPlayers
{
    public static readonly ActorKey KeyA = new("Alice", 73);
    public static readonly ActorKey KeyB = new("Bob", 73);

    public FakeTagHub Hub { get; } = new();
    public FakeRelay Relay { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeWorld WorldA { get; } = new FakeWorld().Add(KeyA).Add(KeyB);
    public FakeWorld WorldB { get; } = new FakeWorld().Add(KeyA).Add(KeyB);
    public FakeChannel ChannelA { get; }
    public FakeChannel ChannelB { get; }
    public FakeSceneChannel SceneA { get; }
    public FakeSceneChannel SceneB { get; }
    public CoPoseClient A { get; }
    public CoPoseClient B { get; }

    public TwoPlayers(CoPoseOptions? options = null)
    {
        ChannelA = Hub.Join(KeyA);
        ChannelB = Hub.Join(KeyB);
        SceneA = new FakeSceneChannel(Relay);
        SceneB = new FakeSceneChannel(Relay);
        A = new CoPoseClient(ChannelA, SceneA, () => KeyA, WorldA, WorldA, WorldA, WorldA, Clock, options);
        B = new CoPoseClient(ChannelB, SceneB, () => KeyB, WorldB, WorldB, WorldB, WorldB, Clock, options);
    }

    /// <summary>Ticks both clients and delivers tags, <paramref name="rounds"/> times, <paramref name="stepMs"/> apart.</summary>
    public void Run(int rounds = 1, long stepMs = 100)
    {
        for (var i = 0; i < rounds; i++)
        {
            A.Tick();
            B.Tick();
            Hub.Flush();
            Clock.Advance(stepMs);
        }
    }

    /// <summary>Discover each other, A requests, B accepts, and let the scene settle over the relay.</summary>
    public TwoPlayers Paired()
    {
        Run(3);
        Assert.True(A.Choose(KeyB));
        Run(8);
        Assert.True(B.Choose(KeyA));
        Run(20);
        Assert.Equal(PairingStatus.Paired, A.Status);
        Assert.Equal(PairingStatus.Paired, B.Status);
        Assert.Equal(RelayStatus.Connected, SceneA.Status);
        Assert.Equal(RelayStatus.Connected, SceneB.Status);
        return this;
    }
}
