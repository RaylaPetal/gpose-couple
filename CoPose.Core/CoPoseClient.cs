using CoPose.Core.Sync;
using CoPose.Core.Tags;
using CoPose.Protocol;

namespace CoPose.Core;

public sealed record CoPoseOptions
{
    public static readonly CoPoseOptions Default = new();

    /// <summary>
    /// Minimum time between tag publishes. Longer than SimpleHeels' 250 ms restarting debounce, so every publish
    /// actually reaches the sync service even during a continuous drag.
    /// </summary>
    public int PublishIntervalMs { get; init; } = 500;

    public int SampleIntervalMs { get; init; } = 50;

    /// <summary>How long a partner's tag may be missing before the session ends.</summary>
    public int PartnerGraceMs { get; init; } = 10_000;
}

/// <summary>
/// Runs CoPose on one client: reads other players' tags, drives <see cref="Pairing"/> and, while paired, a
/// <see cref="TagSync"/> scene, and publishes this client's tag. Call <see cref="Tick"/> on the game's framework thread.
/// </summary>
public sealed class CoPoseClient : IDisposable
{
    private readonly ITagChannel channel;
    private readonly Func<ActorKey?> self;
    private readonly IActorRegistry registry;
    private readonly IPoseReader reader;
    private readonly IPoseWriter writer;
    private readonly ISyncEnvironment environment;
    private readonly IClock clock;
    private readonly CoPoseOptions options;

    private string? published;
    private string? pendingPublish;
    private readonly IntervalGate publishGate;
    private (ActorKey? Chosen, bool Ready, int Resolved, int Revision)? lastFingerprint;
    private bool disposed;

    public CoPoseClient(ITagChannel channel, Func<ActorKey?> self, IActorRegistry registry, IPoseReader reader,
        IPoseWriter writer, ISyncEnvironment environment, IClock? clock = null, CoPoseOptions? options = null)
    {
        this.channel = channel;
        this.self = self;
        this.registry = registry;
        this.reader = reader;
        this.writer = writer;
        this.environment = environment;
        this.clock = clock ?? SystemClock.Instance;
        this.options = options ?? CoPoseOptions.Default;
        Pairing = new Pairing(this.options.PartnerGraceMs);
        publishGate = new IntervalGate(this.options.PublishIntervalMs);
    }

    public Pairing Pairing { get; }

    /// <summary>The shared scene while paired, otherwise null.</summary>
    public TagSync? Session { get; private set; }

    public SyncStats Stats { get; } = new();

    public ActorKey? Self => self();

    public PairingStatus Status => self() is { } me ? Pairing.Status(me) : PairingStatus.Idle;

    public void Tick()
    {
        if (disposed)
            return;

        var now = clock.NowMs;
        if (self() is not { } me)
        {
            Publish(null, now, force: true);
            return;
        }

        Receive(me, now);
        Pairing.Tick(me, now);

        var paired = Pairing.Status(me) == PairingStatus.Paired;
        if (paired && Session == null)
        {
            Session = new TagSync(me, Pairing.Chosen!.Value, registry, reader, writer, environment, Stats, options.SampleIntervalMs);
            if (Pairing.ChosenPeer?.State is { } partnerState)
                Session.OnPartnerTag(partnerState);
        }
        else if (!paired && Session != null)
        {
            Session = null;
        }

        Session?.Tick(now);
        PublishState(me, now);
    }

    /// <summary>Chooses a partner, or accepts their request.</summary>
    public bool Choose(ActorKey partner)
    {
        if (!Pairing.Choose(partner))
            return false;

        // Receive stats describe the chosen partner; start from their tag if we already have one.
        var peer = Pairing.ChosenPeer;
        Stats.PartnerChosen(peer?.State != null ? peer.LastChangeMs : null);
        return true;
    }

    public void Stop()
    {
        Pairing.Stop();
        Session = null;
    }

    public bool PushPose(ActorKey actor) => Session?.PushPose(actor, clock.NowMs) ?? false;

    private void Receive(ActorKey me, long now)
    {
        while (channel.TryReceive(out var tag))
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

            if (status != TagDecodeStatus.Ok || tag.Owner != Pairing.Chosen)
                continue;

            Stats.Received(now);
            if (Session != null && tag.Owner == Session.Partner)
                Session.OnPartnerTag(state!);
        }
    }

    private void PublishState(ActorKey me, long now)
    {
        var session = Session;
        var fingerprint = (Pairing.Chosen, session?.Ready ?? false, session?.Resolved.Length ?? 0, session?.Scene.Revision ?? -1);
        if (fingerprint != lastFingerprint)
        {
            lastFingerprint = fingerprint;
            var state = new TagState(
                ProtocolInfo.Version,
                me,
                Pairing.Chosen,
                session?.Ready ?? false,
                session?.Resolved ?? [],
                session?.Scene.AuthoredClock(me) ?? 0,
                session?.Scene.ExportAuthored(me) ?? []);
            pendingPublish = TagCodec.Encode(state);
        }

        if (pendingPublish != null && pendingPublish != published)
            Publish(pendingPublish, now, force: false);
    }

    private void Publish(string? value, long now, bool force)
    {
        if (value == published)
            return;
        if (!force && !publishGate.IsOpen(now))
            return;

        channel.Publish(value);
        published = value;
        publishGate.Mark(now);
        if (value == null)
            return;

        Stats.Published(value.Length);
        if (value.Length > ProtocolInfo.TagBudgetBytes)
            Stats.Error($"Tag is {value.Length / 1024.0:0.0} KB, over the {ProtocolInfo.TagBudgetBytes / 1024} KB budget.", now);
    }

    /// <summary>Removes the local tag, which ends any session on the partner's side.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        channel.Publish(null);
        published = null;
    }
}
