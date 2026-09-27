using System.Diagnostics;
using CoPose.Protocol;

namespace CoPose.Core.Sync;

/// <summary>
/// The shared scene of one session between <see cref="Self"/> and <see cref="Partner"/>. Everything runs on one thread
/// via <see cref="Tick"/>: finish in-flight applies (re-reading what the skeleton actually holds so it is not recorded
/// as a local edit), apply bones the partner's edits changed, and at the sample interval read, diff and record local
/// edits into <see cref="Scene"/>.
/// </summary>
public sealed class TagSync
{
    private sealed record PendingApply(ActorKey Key, ActorHandle Handle, LocalActor Local, int Version, Task<bool> Task, int[] Bones, BoneRegister[] Registers);
    private const int ApplyRetryMs = 500;

    private readonly IActorRegistry registry;
    private readonly IPoseReader reader;
    private readonly IPoseWriter writer;
    private readonly ISyncEnvironment environment;
    private readonly SyncStats stats;
    private readonly int sampleIntervalMs;

    private readonly Dictionary<ActorKey, LocalActor> locals = [];
    private readonly HashSet<(ActorKey Actor, string Bone)> dirty = [];
    private readonly List<PendingApply> applies = [];
    private readonly Dictionary<ActorKey, long> retryAt = [];
    private readonly PoseBuffer buffer = new();
    private readonly List<int> changed = [];
    private readonly HashSet<(ActorKey Actor, string Bone)> pendingLive = [];
    private readonly Dictionary<ActorKey, (string[] Names, BoneSample[] Samples)> startPoses = [];

    private long nextSampleAt;
    private bool needsSeed;

    public TagSync(ActorKey self, ActorKey partner, IActorRegistry registry, IPoseReader reader, IPoseWriter writer,
        ISyncEnvironment environment, SyncStats stats, int sampleIntervalMs = 50)
    {
        Self = self;
        Partner = partner;
        this.registry = registry;
        this.reader = reader;
        this.writer = writer;
        this.environment = environment;
        this.stats = stats;
        this.sampleIntervalMs = sampleIntervalMs;
    }

    public ActorKey Self { get; }

    public ActorKey Partner { get; }

    public SceneState Scene { get; } = new();

    /// <summary>In GPose with posing on and both characters found.</summary>
    public bool Ready { get; private set; }

    /// <summary>The session actors that resolve to local GPose actors (only while syncing is possible).</summary>
    public ActorKey[] Resolved { get; private set; } = [];

    private ActorKey[] Keys => [Self, Partner];

    /// <summary>Local edits recorded since the last send.</summary>
    public bool HasPendingLive => pendingLive.Count > 0;

    /// <summary>Set when the partner should get a full state now (readiness changed, seed, Push pose, Reset).</summary>
    public bool FullRequested { get; private set; }

    /// <summary>Characters whose starting pose was captured when this client last became ready.</summary>
    public IReadOnlyCollection<ActorKey> StartPoses => startPoses.Keys;

    /// <summary>Returns and clears the local edits since the last send, as tag data.</summary>
    public TagActor[] TakeLiveDelta()
    {
        var delta = Scene.ExportAuthored(Self, pendingLive);
        pendingLive.Clear();
        return delta;
    }

    /// <summary>Returns every bone this client authored and clears pending live edits (a full state covers them).</summary>
    public TagActor[] TakeFull()
    {
        FullRequested = false;
        pendingLive.Clear();
        return Scene.ExportAuthored(Self);
    }

    /// <summary>Records the character's starting pose as new edits, so it returns to it on both clients.</summary>
    public bool Reset(ActorKey actor)
    {
        if (!Ready || !startPoses.TryGetValue(actor, out var start))
            return false;

        var version = Scene.NextVersion(Self);
        for (var i = 0; i < start.Names.Length; i++)
        {
            Scene.Record(actor, start.Names[i], start.Samples[i], version);
            dirty.Add((actor, start.Names[i])); // apply locally too
        }
        FullRequested = true;
        return true;
    }

    /// <summary>Merges the partner's authored edits; changed bones are applied on the next ready tick.</summary>
    public void OnPartnerState(IReadOnlyList<TagActor> actors)
    {
        foreach (var change in Scene.Merge(Partner, actors))
            dirty.Add((change.Actor, change.Bone));
    }

    /// <summary>Stamps the actor's current bones as the newest edits, so they win on both clients.</summary>
    public bool PushPose(ActorKey actor, long now)
    {
        if (!Ready || !registry.TryResolve(actor, out var handle) || !reader.TryRead(handle, buffer))
            return false;

        var local = GetLocal(actor);
        if (!local.Initialized || buffer.LayoutId != local.LayoutId)
            local.Rebuild(buffer);

        var version = Scene.NextVersion(Self);
        for (var i = 0; i < buffer.Count; i++)
        {
            if (local.InFlight[i] != 0)
                continue;
            Scene.Record(actor, buffer.Names[i], buffer.Samples[i], version);
            local.Committed[i] = buffer.Samples[i];
            dirty.Remove((actor, buffer.Names[i]));
        }
        FullRequested = true;
        return true;
    }

    public void Tick(long now)
    {
        var canSync = environment.CanSync;
        var resolved = canSync ? Keys.Where(k => registry.TryResolve(k, out _)).ToArray() : [];
        var ready = resolved.Length == 2;
        Resolved = resolved;

        if (ready != Ready)
        {
            Ready = ready;
            locals.Clear();
            applies.Clear();
            retryAt.Clear();
            FullRequested = true; // the partner shows our ready state
            if (ready)
            {
                startPoses.Clear();
                // The skeleton may have been reset while we were not syncing: re-apply the whole scene,
                // then stamp any of our own bones nobody has set yet.
                foreach (var actor in Scene.Actors)
                {
                    foreach (var (bone, _) in Scene.Bones(actor))
                        dirty.Add((actor, bone));
                }
                needsSeed = true;
                nextSampleAt = now;
            }
        }

        if (!Ready)
            return;

        CompleteApplies(now);
        if (now >= nextSampleAt)
        {
            nextSampleAt = now + sampleIntervalMs;
            Sample();
        }
        ApplyDirty(now);
    }

    private void Sample()
    {
        var start = Stopwatch.GetTimestamp();
        foreach (var key in Keys)
        {
            if (!registry.TryResolve(key, out var handle) || !reader.TryRead(handle, buffer))
                continue;

            var local = GetLocal(key);
            if (!local.Initialized || buffer.LayoutId != local.LayoutId)
            {
                // First sight (or the skeleton changed): take it as the baseline without recording edits.
                local.Rebuild(buffer);
                startPoses.TryAdd(key, (local.Names.ToArray(), local.Committed.ToArray()));
                if (key == Self && needsSeed)
                    Seed(local);
                continue;
            }

            changed.Clear();
            PoseDiff.FindChanged(local.Committed, buffer.Samples.AsSpan(0, local.Count), local.InFlight, changed);
            if (changed.Count == 0)
                continue;

            var version = Scene.NextVersion(Self);
            foreach (var i in changed)
            {
                local.Committed[i] = buffer.Samples[i];
                Scene.Record(key, local.Names[i], buffer.Samples[i], version);
                dirty.Remove((key, local.Names[i]));
                pendingLive.Add((key, local.Names[i]));
            }
        }
        stats.SampleTook(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>Stamps our own character's bones that have no version yet, so the partner sees our current pose.</summary>
    private void Seed(LocalActor local)
    {
        needsSeed = false;
        BoneVersion? version = null;
        for (var i = 0; i < local.Count; i++)
        {
            if (Scene.TryGet(Self, local.Names[i], out _))
                continue;
            version ??= Scene.NextVersion(Self);
            Scene.Record(Self, local.Names[i], local.Committed[i], version.Value);
        }
        if (version != null)
            FullRequested = true;
    }

    private void ApplyDirty(long now)
    {
        if (dirty.Count == 0)
            return;

        foreach (var group in dirty.GroupBy(d => d.Actor).ToList())
        {
            // The Ktisis writer rebuilds the whole actor, so never overlap writes on that actor.
            if (applies.Any(a => a.Key == group.Key) || retryAt.GetValueOrDefault(group.Key) > now)
                continue;
            if (!registry.TryResolve(group.Key, out var handle))
                continue;

            var local = GetLocal(group.Key);
            if (!local.Initialized)
            {
                if (!reader.TryRead(handle, buffer))
                    continue;
                local.Rebuild(buffer);
            }

            var values = new List<BoneValue>();
            var indices = new List<int>();
            var registers = new List<BoneRegister>();
            foreach (var (actor, bone) in group)
            {
                if (!local.TryGetIndex(bone, out var i))
                {
                    dirty.Remove((actor, bone)); // not on this skeleton: skip
                    continue;
                }
                if (local.InFlight[i] != 0 || !Scene.TryGet(actor, bone, out var register))
                    continue; // retry once the current apply lands
                values.Add(new BoneValue(bone, register.Value));
                indices.Add(i);
                registers.Add(register);
                dirty.Remove((actor, bone));
            }
            if (values.Count == 0)
                continue;

            foreach (var i in indices)
                local.InFlight[i]++;
            applies.Add(new PendingApply(group.Key, handle, local, local.Version, Guard(() => writer.ApplyBonesAsync(handle, values)), indices.ToArray(), registers.ToArray()));
        }
    }

    private void CompleteApplies(long now)
    {
        if (applies.Count == 0)
            return;

        foreach (var apply in applies.Where(a => a.Task.IsCompleted).ToList())
        {
            applies.Remove(apply);
            var local = apply.Local;
            if (local.Version != apply.Version || !locals.TryGetValue(apply.Key, out var current) || !ReferenceEquals(current, local))
                continue;

            var succeeded = apply.Task.IsCompletedSuccessfully && apply.Task.Result;
            if (!succeeded)
            {
                stats.Error($"Applying pose to {apply.Key} failed; retrying{(apply.Task.Exception is { } ex ? ": " + ex.InnerException?.Message : "")}", now);
                retryAt[apply.Key] = now + ApplyRetryMs;
                for (var j = 0; j < apply.Bones.Length; j++)
                {
                    var bone = local.Names[apply.Bones[j]];
                    // Do not resurrect a write superseded by a newer edit.
                    if (Scene.TryGet(apply.Key, bone, out var currentRegister) && currentRegister.Version == apply.Registers[j].Version)
                        dirty.Add((apply.Key, bone));
                }
            }
            else
            {
                retryAt.Remove(apply.Key);
                stats.Applied(apply.Bones.Length);
            }

            foreach (var i in apply.Bones)
                local.InFlight[i]--;

            if (!reader.TryRead(apply.Handle, buffer))
                continue;
            if (buffer.LayoutId != local.LayoutId)
            {
                local.Rebuild(buffer);
                continue;
            }

            // Echo suppression: what the skeleton actually holds is in sync, so the next diff records no edit.
            foreach (var i in apply.Bones)
            {
                if (local.InFlight[i] == 0)
                    local.Committed[i] = buffer.Samples[i];
            }
        }
    }

    private LocalActor GetLocal(ActorKey key)
    {
        if (!locals.TryGetValue(key, out var local))
            locals[key] = local = new LocalActor();
        return local;
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
