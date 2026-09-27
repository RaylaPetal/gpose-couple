using System;
using CoPose.Core;
using CoPose.Core.Sync;
using CoPose.Core.Tags;
using CoPose.Interop;
using CoPose.Protocol;
using Dalamud.Plugin.Services;

namespace CoPose.Session;

/// <summary>
/// Hosts the <see cref="CoPoseClient"/> in the game: keeps Ktisis, SimpleHeels and sync-service status fresh,
/// invalidates actor lookups when GPose, posing or the partner change, and ticks the client. Framework thread only.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private const long StatusRefreshMs = 1000;

    private readonly IClientState clientState;
    private readonly KtisisIpc ktisis;
    private readonly SimpleHeelsIpc heels;
    private readonly Prerequisites prerequisites;
    private readonly GposeActorRegistry registry;
    private readonly HavokPoseReader reader;

    private readonly IntervalGate refreshGate = new(StatusRefreshMs);
    private bool wasGposing;
    private bool wasPosing;
    private ActorKey? lastPartner;

    public SessionManager(IClientState clientState, KtisisIpc ktisis, SimpleHeelsIpc heels, Prerequisites prerequisites,
        HeelsTagChannel channel, GposeActorRegistry registry, HavokPoseReader reader, IPoseWriter writer, ISyncEnvironment environment)
    {
        this.clientState = clientState;
        this.ktisis = ktisis;
        this.heels = heels;
        this.prerequisites = prerequisites;
        this.registry = registry;
        this.reader = reader;
        Channel = channel;
        Client = new CoPoseClient(channel, () => registry.LocalKey, registry, reader, writer, environment);
        prerequisites.Refresh();
    }

    public CoPoseClient Client { get; }

    public HeelsTagChannel Channel { get; }

    public ActorKey? Self => registry.LocalKey;

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (refreshGate.TryPass(now))
        {
            ktisis.Refresh();
            heels.Refresh();
            prerequisites.Refresh();
        }

        var gposing = clientState.IsGPosing;
        var partner = Client.Session?.Partner;
        if (gposing != wasGposing || ktisis.IsPosing != wasPosing || partner != lastPartner)
        {
            wasGposing = gposing;
            wasPosing = ktisis.IsPosing;
            lastPartner = partner;
            registry.Invalidate();
            if (!gposing)
                reader.ClearCache();
        }

        // Tags need SimpleHeels; without it there is nothing to publish or read.
        if (heels.Available)
            Client.Tick();
    }

    public bool Choose(ActorKey partner) => Client.Choose(partner);

    public void Stop() => Client.Stop();

    public bool PushPose(ActorKey actor) => Client.PushPose(actor);

    public PairingStatus Status => Client.Status;

    public void Dispose()
    {
        Client.Dispose();
    }
}
