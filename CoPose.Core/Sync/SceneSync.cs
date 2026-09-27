using System.Diagnostics;
using CoPose.Core.Net;
using CoPose.Protocol;

namespace CoPose.Core.Sync;

public sealed record SceneSyncOptions
{
    public static readonly SceneSyncOptions Default = new();

    /// <summary>Minimum time between local samples (20 Hz by default).</summary>
    public int SampleIntervalMs { get; init; } = 50;

    /// <summary>A local change touching more bones than this is sent as a full snapshot instead of a delta.</summary>
    public int SnapshotThresholdBones { get; init; } = 150;
}

public sealed class ParticipantState(PeerInfo info, bool isSelf)
{
    public PeerInfo Info { get; } = info;
    public bool IsSelf { get; } = isSelf;
    public bool Ready { get; internal set; }
    public ActorKey[] Resolved { get; internal set; } = [];
}

/// <summary>
/// The sync loop. Everything here runs on one thread (the game's framework thread in the plugin) via <see cref="Tick"/>:
/// drain host-ordered events and apply remote edits, finish in-flight applies (re-reading what the skeleton actually holds
/// so it is not echoed back), then at <see cref="SceneSyncOptions.SampleIntervalMs"/> read, diff and send local edits.
/// </summary>
public sealed class SceneSync
{
    private sealed record PendingApply(ActorKey Key, ActorHandle Handle, ActorState State, int Version, Task<bool> Task, int[]? Bones, BoneValue[] Reapply);
    private sealed record PendingExport(ActorKey Key, ActorHandle Handle, Task<string?> Task, long Seq, int[]? DiffBones, int Generation);

    private readonly ISessionTransport transport;
    private readonly IActorRegistry registry;
    private readonly IPoseReader reader;
    private readonly IPoseWriter writer;
    private readonly ISyncEnvironment environment;
    private readonly IClock clock;
    private readonly SceneSyncOptions options;

    private readonly List<ParticipantState> participants = [];
    private readonly Dictionary<ActorKey, ActorState> actors = [];
    private readonly List<PendingApply> applies = [];
    private readonly List<PendingExport> exports = [];
    private readonly PoseBuffer buffer = new();
    private readonly List<int> changed = [];

    private long localSeq;
    private long nextSampleAt;
    private int generation;
    private bool ready;
    private bool announced;
    private int resolvedMask = -1;

    public SceneSync(
        ISessionTransport transport,
        IActorRegistry registry,
        IPoseReader reader,
        IPoseWriter writer,
        ISyncEnvironment environment,
        IClock? clock = null,
        SceneSyncOptions? options = null)
    {
        this.transport = transport;
        this.registry = registry;
        this.reader = reader;
        this.writer = writer;
        this.environment = environment;
        this.clock = clock ?? SystemClock.Instance;
        this.options = options ?? SceneSyncOptions.Default;

        foreach (var peer in transport.InitialParticipants)
            participants.Add(new ParticipantState(peer, peer.ClientId == Self.ClientId));
        if (participants.All(p => !p.IsSelf))
            participants.Insert(0, new ParticipantState(Self, true));
    }

    public PeerInfo Self => transport.Self;

    public IReadOnlyList<ParticipantState> Participants => participants;

    public bool IsReady => ready;

    public bool Ended { get; private set; }

    public string? EndReason { get; private set; }

    public SyncStats Stats { get; } = new();

    public bool IsResolved(ActorKey key) => registry.TryResolve(key, out _);

    public void Tick()
    {
        var now = clock.NowMs;
        DrainEvents(now);
        if (Ended)
            return;

        UpdateReadiness(now);
        CompleteApplies(now);
        CompleteExports(now);

        if (ready && now >= nextSampleAt)
        {
            nextSampleAt = now + options.SampleIntervalMs;
            Sample(now);
        }

        Stats.Roll(now);
    }

    /// <summary>Sends a full snapshot of any resolved actor ("Push pose").</summary>
    public bool PushPose(ActorKey key)
    {
        if (!ready || !registry.TryResolve(key, out var handle))
            return false;
        StartExport(key, handle, 0, null);
        return true;
    }

    /// <summary>Asks every other participant for a snapshot of their own character ("Request resync").</summary>
    public void RequestResync()
    {
        foreach (var p in participants)
        {
            if (!p.IsSelf)
                Send(MsgType.SnapshotRequest, Wire.Serialize(new SnapshotRequest(p.Info.Actor)), clock.NowMs);
        }
    }

    // Events

    private void DrainEvents(long now)
    {
        while (transport.Events.TryRead(out var e))
        {
            switch (e)
            {
                case MessageEvent m:
                    HandleMessage(m.Envelope, now);
                    break;
                case PeerJoinedEvent joined:
                    if (participants.All(p => p.Info.ClientId != joined.Peer.ClientId))
                        participants.Add(new ParticipantState(joined.Peer, false));
                    resolvedMask = -1;
                    BroadcastPresence(now);
                    break;
                case PeerLeftEvent left:
                    participants.RemoveAll(p => !p.IsSelf && p.Info.ClientId == left.ClientId);
                    resolvedMask = -1;
                    break;
                case SessionEndedEvent ended:
                    Ended = true;
                    EndReason = ended.Reason;
                    SetReady(false, now);
                    return;
            }
        }
    }

    private void HandleMessage(Envelope envelope, long now)
    {
        var own = envelope.SenderId == Self.ClientId;
        if (!own)
            Stats.Received();

        try
        {
            switch (envelope.Type)
            {
                case MsgType.BoneDelta:
                    var delta = Wire.Deserialize<BoneDelta>(envelope.Body);
                    if (own)
                        AcknowledgeDelta(delta);
                    else
                        ApplyRemoteDelta(delta, now);
                    break;

                case MsgType.FullSnapshot:
                    var snapshot = Wire.Deserialize<FullSnapshot>(envelope.Body);
                    if (own)
                        AcknowledgeSnapshot(snapshot);
                    else
                        ApplyRemoteSnapshot(snapshot, now);
                    break;

                case MsgType.Presence when !own:
                    OnPresence(envelope.SenderId, Wire.Deserialize<Presence>(envelope.Body), now);
                    break;

                case MsgType.SnapshotRequest when !own:
                    var request = Wire.Deserialize<SnapshotRequest>(envelope.Body);
                    if (ready && request.Actor == Self.Actor && registry.TryResolve(request.Actor, out var handle))
                        StartExport(request.Actor, handle, 0, null);
                    break;
            }
        }
        catch (Exception ex)
        {
            Stats.Error($"Bad {envelope.Type} message: {ex.Message}", now);
        }
    }

    private void OnPresence(Guid sender, Presence presence, long now)
    {
        var participant = participants.FirstOrDefault(p => p.Info.ClientId == sender);
        if (participant == null)
            return;

        var becameReady = presence.Ready && !participant.Ready;
        participant.Ready = presence.Ready;
        participant.Resolved = presence.Resolved;

        if (becameReady && ready)
            SendOwnSnapshot(now);
    }

    private void AcknowledgeDelta(BoneDelta delta)
    {
        if (!actors.TryGetValue(delta.Actor, out var state) || !state.Initialized)
            return;
        foreach (var bone in delta.Bones)
        {
            if (state.TryGetIndex(bone.Name, out var i))
                state.Acknowledge(i, delta.LocalSeq);
        }
    }

    private void AcknowledgeSnapshot(FullSnapshot snapshot)
    {
        if (!actors.TryGetValue(snapshot.Actor, out var state) || !state.Initialized)
            return;
        for (var i = 0; i < state.Count; i++)
            state.Acknowledge(i, snapshot.LocalSeq);
    }

    private void ApplyRemoteDelta(BoneDelta delta, long now)
    {
        if (!ready || !TryGetState(delta.Actor, out var handle, out var state))
            return;

        var values = new List<BoneValue>(delta.Bones.Length);
        var indices = new List<int>(delta.Bones.Length);
        foreach (var bone in delta.Bones)
        {
            // Unknown bones are skipped; bones with an unacknowledged local edit keep the local value.
            if (!state.TryGetIndex(bone.Name, out var i) || state.Pending[i] != 0)
                continue;
            values.Add(new BoneValue(bone.Name, BoneSample.FromWire(bone)));
            indices.Add(i);
        }

        if (values.Count > 0)
            StartApply(delta.Actor, handle, state, values, indices.ToArray(), now);
    }

    private void ApplyRemoteSnapshot(FullSnapshot snapshot, long now)
    {
        if (!ready || !TryGetState(snapshot.Actor, out var handle, out var state))
            return;

        var json = SnapshotCompression.Decompress(snapshot.PoseJsonBrotli);

        // The snapshot was sequenced before our unacknowledged edits, so those must survive it.
        var reapply = new List<BoneValue>();
        for (var i = 0; i < state.Count; i++)
        {
            if (state.Pending[i] != 0)
                reapply.Add(new BoneValue(state.Names[i], state.Committed[i]));
        }

        state.WholeInFlight++;
        applies.Add(new PendingApply(snapshot.Actor, handle, state, state.Version,
            Guard(() => writer.ApplySnapshotAsync(handle, json)), null, reapply.ToArray()));
    }

    // Applying

    private void StartApply(ActorKey key, ActorHandle handle, ActorState state, IReadOnlyList<BoneValue> values, int[] indices, long now)
    {
        foreach (var i in indices)
            state.InFlight[i]++;
        applies.Add(new PendingApply(key, handle, state, state.Version,
            Guard(() => writer.ApplyBonesAsync(handle, values)), indices, []));
    }

    private void CompleteApplies(long now)
    {
        if (applies.Count == 0)
            return;

        var done = applies.Where(a => a.Task.IsCompleted).ToList();
        foreach (var apply in done)
        {
            applies.Remove(apply);
            var state = apply.State;
            if (state.Version != apply.Version || !actors.TryGetValue(apply.Key, out var current) || !ReferenceEquals(current, state))
                continue;

            if (apply.Task.IsFaulted || !apply.Task.Result)
                Stats.Error($"Applying pose to {apply.Key} failed{(apply.Task.Exception is { } ex ? ": " + ex.InnerException?.Message : "")}", now);

            if (apply.Bones == null)
                state.WholeInFlight--;
            else
            {
                foreach (var i in apply.Bones)
                    state.InFlight[i]--;
            }

            if (!reader.TryRead(apply.Handle, buffer))
                continue;
            if (buffer.LayoutId != state.LayoutId)
            {
                state.Rebuild(buffer);
                continue;
            }

            if (apply.Bones == null)
            {
                if (apply.Reapply.Length > 0)
                {
                    var indices = new List<int>(apply.Reapply.Length);
                    foreach (var value in apply.Reapply)
                    {
                        if (state.TryGetIndex(value.Name, out var i))
                            indices.Add(i);
                    }
                    StartApply(apply.Key, apply.Handle, state, apply.Reapply, indices.ToArray(), now);
                }
                for (var i = 0; i < state.Count; i++)
                    CommitIfSettled(state, i);
            }
            else
            {
                foreach (var i in apply.Bones)
                    CommitIfSettled(state, i);
            }
        }
    }

    /// <summary>Echo suppression: store what the skeleton actually holds, so the next diff sees no change.</summary>
    private void CommitIfSettled(ActorState state, int i)
    {
        if (state.WholeInFlight == 0 && state.InFlight[i] == 0)
            state.Committed[i] = buffer.Samples[i];
    }

    // Local changes

    private void Sample(long now)
    {
        var start = Stopwatch.GetTimestamp();
        foreach (var participant in participants)
        {
            var key = participant.Info.Actor;
            if (!registry.TryResolve(key, out var handle))
                continue;
            var state = GetOrCreate(key);
            if (!reader.TryRead(handle, buffer))
                continue;

            // First sight of this actor (or its skeleton changed): take it as the baseline without sending.
            if (!state.Initialized || buffer.LayoutId != state.LayoutId)
            {
                state.Rebuild(buffer);
                continue;
            }
            if (state.WholeInFlight > 0)
                continue;

            changed.Clear();
            PoseDiff.FindChanged(state.Committed, buffer.Samples.AsSpan(0, state.Count), state.InFlight, changed);
            if (changed.Count == 0)
                continue;

            var seq = ++localSeq;
            foreach (var i in changed)
            {
                state.Committed[i] = buffer.Samples[i];
                state.Pending[i] = seq;
            }

            if (changed.Count > options.SnapshotThresholdBones)
            {
                if (exports.Any(e => e.Key == key))
                    SendDeltaChunks(key, seq, state, changed.ToArray(), now);
                else
                    StartExport(key, handle, seq, changed.ToArray());
            }
            else
            {
                SendDelta(key, seq, state, changed, now);
            }
        }
        Stats.SampleTook(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private void SendDelta(ActorKey key, long seq, ActorState state, IReadOnlyList<int> indices, long now)
    {
        var bones = new BoneTransform[indices.Count];
        for (var j = 0; j < bones.Length; j++)
        {
            var i = indices[j];
            bones[j] = state.Committed[i].ToWire(state.Names[i]);
        }
        Send(MsgType.BoneDelta, Wire.Serialize(new BoneDelta(key, seq, bones)), now, bones.Length);
    }

    private void SendDeltaChunks(ActorKey key, long seq, ActorState state, int[] indices, long now)
    {
        var size = Math.Max(1, options.SnapshotThresholdBones);
        for (var offset = 0; offset < indices.Length; offset += size)
            SendDelta(key, seq, state, new ArraySegment<int>(indices, offset, Math.Min(size, indices.Length - offset)), now);
    }

    // Snapshots

    private void SendOwnSnapshot(long now)
    {
        if (registry.TryResolve(Self.Actor, out var handle))
            StartExport(Self.Actor, handle, 0, null);
    }

    /// <param name="seq">A local sequence already reserved by the diff (its bones are marked pending), or 0.</param>
    /// <param name="diffBones">Bones to fall back to sending as deltas if the export fails.</param>
    private void StartExport(ActorKey key, ActorHandle handle, long seq, int[]? diffBones)
    {
        if (seq == 0 && exports.Any(e => e.Key == key && e.Seq == 0))
            return;
        exports.Add(new PendingExport(key, handle, Guard(() => writer.ExportSnapshotAsync(handle)), seq, diffBones, generation));
    }

    private void CompleteExports(long now)
    {
        if (exports.Count == 0)
            return;

        var done = exports.Where(e => e.Task.IsCompleted).ToList();
        foreach (var export in done)
        {
            exports.Remove(export);
            if (export.Generation != generation)
                continue;

            var json = export.Task.IsCompletedSuccessfully ? export.Task.Result : null;
            byte[]? compressed = json == null ? null : SnapshotCompression.Compress(json);

            // Leave room for the rest of the message around the pose bytes.
            if (compressed == null || compressed.Length > ProtocolInfo.MaxBodyBytes - 1024)
            {
                Stats.Error(compressed == null
                    ? $"Exporting the pose of {export.Key} failed"
                    : $"The pose of {export.Key} is too large to send ({compressed.Length} bytes compressed)", now);
                if (export.DiffBones != null && actors.TryGetValue(export.Key, out var fallback) && fallback.Initialized)
                    SendDeltaChunks(export.Key, export.Seq, fallback, export.DiffBones, now);
                continue;
            }

            var seq = export.Seq != 0 ? export.Seq : ++localSeq;

            // Everything in the snapshot is now our in-flight edit: until its echo, earlier remote edits must not win.
            if (TryGetState(export.Key, out var handle, out var state) && reader.TryRead(handle, buffer) && buffer.LayoutId == state.LayoutId)
            {
                for (var i = 0; i < state.Count; i++)
                {
                    CommitIfSettled(state, i);
                    state.Pending[i] = seq;
                }
            }

            Send(MsgType.FullSnapshot, Wire.Serialize(new FullSnapshot(export.Key, seq, compressed)), now);
        }
    }

    // Readiness and presence

    private void UpdateReadiness(long now)
    {
        var canSync = environment.CanSync;
        var mask = 0;
        for (var i = 0; i < participants.Count && i < 31; i++)
        {
            if (canSync && registry.TryResolve(participants[i].Info.Actor, out _))
                mask |= 1 << i;
        }
        var allResolved = mask == (1 << Math.Min(participants.Count, 31)) - 1;

        var readinessChanged = SetReady(canSync && allResolved, now);
        if (!readinessChanged && (!announced || mask != resolvedMask))
            BroadcastPresence(now);
        resolvedMask = mask;
    }

    private bool SetReady(bool value, long now)
    {
        if (value == ready)
            return false;

        ready = value;
        generation++;
        actors.Clear();
        applies.Clear();
        exports.Clear();

        if (Ended)
            return true;

        BroadcastPresence(now);
        if (ready)
        {
            nextSampleAt = now;
            SendOwnSnapshot(now);
        }
        return true;
    }

    private void BroadcastPresence(long now)
    {
        announced = true;
        var resolved = participants
            .Select(p => p.Info.Actor)
            .Where(k => environment.CanSync && registry.TryResolve(k, out _))
            .ToArray();
        Send(MsgType.Presence, Wire.Serialize(new Presence(ready, resolved)), now);
        var self = participants.First(p => p.IsSelf);
        self.Ready = ready;
        self.Resolved = resolved;
    }

    // Helpers

    private ActorState GetOrCreate(ActorKey key)
    {
        if (!actors.TryGetValue(key, out var state))
        {
            state = new ActorState();
            actors[key] = state;
        }
        return state;
    }

    private bool TryGetState(ActorKey key, out ActorHandle handle, out ActorState state)
    {
        state = null!;
        if (!registry.TryResolve(key, out handle))
            return false;
        state = GetOrCreate(key);
        if (!state.Initialized)
        {
            if (!reader.TryRead(handle, buffer))
                return false;
            state.Rebuild(buffer);
        }
        return true;
    }

    private void Send(MsgType type, byte[] body, long now, int bonesInDelta = -1)
    {
        switch (transport.Send(type, body))
        {
            case SendResult.Ok:
                Stats.Sent(bonesInDelta);
                break;
            case SendResult.TooLarge:
                Stats.Error($"{type} message too large ({body.Length} bytes)", now);
                break;
            case SendResult.NotConnected:
                Stats.Error("Not connected", now);
                break;
        }
    }

    /// <summary>Turns a synchronous throw from a writer into a faulted task.</summary>
    private static Task<T> Guard<T>(Func<Task<T>> start)
    {
        try
        {
            return start();
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }
}
