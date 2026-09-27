using CoPose.Core.Relay;
using CoPose.Core.Sync;
using CoPose.Core.Tags;
using CoPose.Protocol;

namespace CoPose.Core;

public sealed record CoPoseOptions
{
    public static readonly CoPoseOptions Default = new();

    /// <summary>Minimum time between pairing-tag publishes.</summary>
    public int TagPublishIntervalMs { get; init; } = 500;

    public int SampleIntervalMs { get; init; } = 50;

    /// <summary>How long a nearby player's tag may be missing before they're dropped from the list.</summary>
    public int PartnerGraceMs { get; init; } = 10_000;

    /// <summary>Minimum time between live deltas (10 per second).</summary>
    public int LiveIntervalMs { get; init; } = 100;

    /// <summary>Automatic resync: a full state goes out this often while in a session.</summary>
    public int FullIntervalMs { get; init; } = 3_000;

    /// <summary>A session ends when the partner is off the relay and not paired by tag for this long.</summary>
    public int PartnerAbsentTimeoutMs { get; init; } = 30_000;
}

/// <summary>
/// Runs CoPose on one client. SimpleHeels tags carry pairing (who chose whom, plus a secret session nonce); once
/// paired, both clients join the relay room derived from their nonces, and the shared scene, readiness and stop travel
/// over the relay, which works in GPose where sync services defer tag updates. Call <see cref="Tick"/> on the game's
/// framework thread.
/// </summary>
public sealed class CoPoseClient : IDisposable
{
    private readonly ITagChannel tags;
    private readonly ISceneChannel scene;
    private readonly Func<ActorKey?> self;
    private readonly IActorRegistry registry;
    private readonly IPoseReader reader;
    private readonly IPoseWriter writer;
    private readonly ISyncEnvironment environment;
    private readonly IClock clock;
    private readonly CoPoseOptions options;
    private readonly IntervalGate tagGate;
    private readonly IntervalGate liveGate;
    private readonly IntervalGate fullGate;

    private string? publishedTag;
    private string? pendingTag;
    private (ActorKey? Me, ActorKey? Chosen)? lastTagFingerprint;
    private long? partnerAbsentSince;
    private bool fullDue;
    private bool disposed;

    public CoPoseClient(ITagChannel tags, ISceneChannel scene, Func<ActorKey?> self, IActorRegistry registry,
        IPoseReader reader, IPoseWriter writer, ISyncEnvironment environment, IClock? clock = null,
        CoPoseOptions? options = null, byte[]? nonce = null)
    {
        this.tags = tags;
        this.scene = scene;
        this.self = self;
        this.registry = registry;
        this.reader = reader;
        this.writer = writer;
        this.environment = environment;
        this.clock = clock ?? SystemClock.Instance;
        this.options = options ?? CoPoseOptions.Default;
        Nonce = nonce ?? RoomKeys.NewNonce();
        Pairing = new Pairing(this.options.PartnerGraceMs);
        tagGate = new IntervalGate(this.options.TagPublishIntervalMs);
        liveGate = new IntervalGate(this.options.LiveIntervalMs);
        fullGate = new IntervalGate(this.options.FullIntervalMs);
    }

    /// <summary>This plugin load's secret session nonce, published in the tag.</summary>
    public byte[] Nonce { get; }

    public Pairing Pairing { get; }

    /// <summary>The shared scene while in a session, otherwise null.</summary>
    public TagSync? Session { get; private set; }

    public SyncStats Stats { get; } = new();

    public ISceneChannel Relay => scene;

    public ActorKey? Self => self();

    /// <summary>The partner is connected to the session's relay room.</summary>
    public bool PartnerPresent { get; private set; }

    /// <summary>The partner's last reported readiness (from the relay).</summary>
    public bool PartnerReady { get; private set; }

    public PairingStatus Status =>
        Session != null ? PairingStatus.Paired : self() is { } me ? Pairing.Status(me) : PairingStatus.Idle;

    public void Tick()
    {
        if (disposed)
            return;

        var now = clock.NowMs;
        if (self() is not { } me)
        {
            EndSession(null, sendStop: true);
            PublishTag(null, now, force: true);
            return;
        }

        ReceiveTags(me, now);
        Pairing.Tick(me, now, holdChosen: Session != null);

        // A removed tag no longer names me, even while the session holds on to the partner.
        var tagPaired = Pairing.Status(me) == PairingStatus.Paired && Pairing.ChosenPeer is { Present: true };
        if (tagPaired && Pairing.ChosenPeer?.State is { } partnerTag)
        {
            // Start, or follow the partner into a new room after they reloaded (new nonce).
            var room = RoomKeys.RoomId(me, Nonce, partnerTag.Self, partnerTag.Nonce);
            if (Session == null || scene.Room != room)
                StartSession(me, partnerTag.Self, room);
        }

        if (Session != null)
            DrainRelay(now);

        if (Session is { } session)
        {
            if (tagPaired || PartnerPresent)
                partnerAbsentSince = null;
            else if ((partnerAbsentSince ??= now) + options.PartnerAbsentTimeoutMs <= now)
                EndSession($"{session.Partner.Name} is gone (left, crashed or stopped CoPose).", sendStop: false);
        }

        if (Session is { } active)
        {
            active.Tick(now);
            SendScene(me, active, now);
        }

        PublishTag(me, now);
    }

    /// <summary>Chooses a partner, or accepts their request.</summary>
    public bool Choose(ActorKey partner) => Pairing.Choose(partner);

    /// <summary>Stops posing together, on both clients (the stop also travels over the relay).</summary>
    public void Stop()
    {
        EndSession(null, sendStop: true);
        Pairing.Stop();
    }

    public bool PushPose(ActorKey actor) => Session?.PushPose(actor, clock.NowMs) ?? false;

    public bool Reset(ActorKey actor) => Session?.Reset(actor) ?? false;

    public bool ResetBoth() => Session is { } s && s.Reset(s.Self) | s.Reset(s.Partner);

    private void StartSession(ActorKey me, ActorKey partner, string room)
    {
        if (Session == null || Session.Partner != partner)
        {
            Session = new TagSync(me, partner, registry, reader, writer, environment, Stats, options.SampleIntervalMs);
            Stats.SessionStarted();
        }
        PartnerPresent = false;
        PartnerReady = false;
        partnerAbsentSince = null;
        fullDue = true;
        if (scene.Room != room)
        {
            while (scene.TryReceive(out _)) { } // events from an older room
        }
        scene.Join(room, RoomKeys.ParticipantId(Nonce));
    }

    private void EndSession(string? reason, bool sendStop)
    {
        if (Session == null)
            return;

        if (sendStop && scene.Status == RelayStatus.Connected)
            scene.Send(SceneCodec.Encode(FrameKind.Live, new StopMessage(reason ?? "stopped")));
        scene.Leave();
        Session = null;
        PartnerPresent = false;
        PartnerReady = false;
        partnerAbsentSince = null;
        if (reason != null)
            Pairing.End(reason);
    }

    private void ReceiveTags(ActorKey me, long now)
    {
        while (tags.TryReceive(out var tag))
        {
            if (tag.Owner == me)
                continue;

            if (tag.Value == null)
            {
                Pairing.ObserveRemoved(tag.Owner, now);
                continue;
            }

            var status = TagCodec.TryDecode(tag.Value, out var state, out var otherVersion);
            Pairing.Observe(tag.Owner, status, state, otherVersion, now);
        }
    }

    private void DrainRelay(long now)
    {
        while (Session is { } session && scene.TryReceive(out var e))
        {
            switch (e)
            {
                case ChannelConnected:
                    fullDue = true;
                    break;
                case ChannelDisconnected:
                    PartnerPresent = false;
                    break;
                case ChannelFrame { Frame: var frame }:
                    if (!SceneCodec.TryDecode(frame, out var kind, out var message))
                    {
                        Stats.Error("Ignored a malformed relay frame.", now);
                        break;
                    }
                    switch (kind, message)
                    {
                        case (FrameKind.PeerJoined, _):
                            PartnerPresent = true;
                            fullDue = true; // make sure the partner has our latest
                            break;
                        case (FrameKind.PeerLeft, _):
                            PartnerPresent = false;
                            break;
                        case (_, StateMessage state) when state.Self == session.Partner:
                            Stats.Received(now);
                            PartnerReady = state.Ready;
                            session.OnPartnerState(state.Actors);
                            break;
                        case (_, StopMessage):
                            EndSession($"{session.Partner.Name} stopped posing together.", sendStop: false);
                            Pairing.Stop();
                            break;
                    }
                    break;
            }
        }
    }

    /// <summary>Full state on demand and every few seconds; otherwise live deltas at most every 100 ms.</summary>
    private void SendScene(ActorKey me, TagSync session, long now)
    {
        if (scene.Status != RelayStatus.Connected)
            return;

        var full = fullDue || session.FullRequested || fullGate.IsOpen(now);
        if (full)
        {
            fullDue = false;
            fullGate.Mark(now);
            liveGate.Mark(now);
            Send(FrameKind.Full, State(me, session, session.TakeFull()), now);
        }
        else if (session.HasPendingLive && liveGate.TryPass(now))
        {
            Send(FrameKind.Live, State(me, session, session.TakeLiveDelta()), now);
        }
    }

    private static StateMessage State(ActorKey me, TagSync session, TagActor[] actors) =>
        new(me, session.Ready, session.Resolved, session.Scene.AuthoredClock(me), actors);

    private void Send(FrameKind kind, StateMessage state, long now)
    {
        var frame = SceneCodec.Encode(kind, state);
        if (frame.Length > ProtocolInfo.MaxFrameBytes)
        {
            Stats.Error($"State message is {frame.Length / 1024.0:0.0} KB, over the relay's {ProtocolInfo.MaxFrameBytes / 1024} KB limit.", now);
            return;
        }
        if (scene.Send(frame))
            Stats.Sent(frame.Length);
    }

    private void PublishTag(ActorKey? me, long now, bool force = false)
    {
        var fingerprint = (me, Pairing.Chosen);
        if (fingerprint != lastTagFingerprint)
        {
            lastTagFingerprint = fingerprint;
            pendingTag = me is { } key ? TagCodec.Encode(new TagState(ProtocolInfo.Version, key, Pairing.Chosen, Nonce)) : null;
        }

        if (pendingTag == publishedTag)
            return;
        if (!force && !tagGate.IsOpen(now))
            return;

        tags.Publish(pendingTag);
        publishedTag = pendingTag;
        tagGate.Mark(now);
        if (pendingTag != null)
            Stats.TagPublished();
    }

    /// <summary>Tells the partner we left (over the relay), closes the relay connection and removes the tag.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        EndSession(null, sendStop: true);
        tags.Publish(null);
        publishedTag = null;
    }
}
