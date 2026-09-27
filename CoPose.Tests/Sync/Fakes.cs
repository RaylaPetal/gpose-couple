using System.Numerics;
using CoPose.Core;
using CoPose.Core.Sync;
using CoPose.Core.Tags;
using CoPose.Protocol;

namespace CoPose.Tests.Sync;

internal sealed class FakeClock : IClock
{
    public long NowMs { get; set; } = 1000;

    public void Advance(long ms) => NowMs += ms;
}

internal sealed class FakeSkeleton
{
    public readonly string[] Names;
    public readonly BoneSample[] Values;
    public long LayoutId = 1;

    public FakeSkeleton(int bones)
    {
        Names = Enumerable.Range(0, bones).Select(i => $"j_bone_{i:000}").ToArray();
        Values = Enumerable.Range(0, bones)
            .Select(i => new BoneSample(new Vector3(i * 0.1f, 0, 0), Quaternion.Identity, Vector3.One))
            .ToArray();
    }

    public int IndexOf(string name) => Array.IndexOf(Names, name);
}

/// <summary>One client's game: actors with skeletons, readable and writable like Havok via Ktisis.</summary>
internal sealed class FakeWorld : IActorRegistry, IPoseReader, IPoseWriter, ISyncEnvironment
{
    private readonly Dictionary<ActorKey, (ActorHandle Handle, FakeSkeleton Skeleton)> actors = [];

    public bool CanSync { get; set; } = true;
    public Func<Task<bool>>? ApplyResult { get; set; }

    public List<(ActorKey Actor, string[] Bones)> Applies { get; } = [];

    public FakeWorld Add(ActorKey key, int bones = 10)
    {
        actors[key] = (new ActorHandle((uint)(200 + actors.Count)), new FakeSkeleton(bones));
        return this;
    }

    public FakeSkeleton this[ActorKey key] => actors[key].Skeleton;

    public void Remove(ActorKey key) => actors.Remove(key);

    /// <summary>A local edit, as if made with a Ktisis gizmo.</summary>
    public void Pose(ActorKey key, int bone, Vector3 position) => this[key].Values[bone].Position = position;

    public Vector3 PositionOf(ActorKey key, int bone) => this[key].Values[bone].Position;

    public bool TryResolve(ActorKey key, out ActorHandle handle)
    {
        var found = actors.TryGetValue(key, out var entry);
        handle = entry.Handle;
        return found;
    }

    private FakeSkeleton? ByHandle(ActorHandle handle) => actors.Values.FirstOrDefault(a => a.Handle == handle).Skeleton;

    public bool TryRead(ActorHandle actor, PoseBuffer into)
    {
        var skeleton = ByHandle(actor);
        if (skeleton == null)
            return false;
        into.EnsureCapacity(skeleton.Names.Length);
        Array.Copy(skeleton.Names, into.Names, skeleton.Names.Length);
        Array.Copy(skeleton.Values, into.Samples, skeleton.Values.Length);
        into.Count = skeleton.Names.Length;
        into.LayoutId = skeleton.LayoutId;
        return true;
    }

    public Task<bool> ApplyBonesAsync(ActorHandle actor, IReadOnlyList<BoneValue> bones)
    {
        var skeleton = ByHandle(actor)!;
        Applies.Add((actors.First(a => a.Value.Handle == actor).Key, bones.Select(b => b.Name).ToArray()));
        if (ApplyResult is { } result)
            return result();
        foreach (var bone in bones)
        {
            var i = skeleton.IndexOf(bone.Name);
            if (i >= 0)
                skeleton.Values[i] = bone.Value;
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Stands in for SimpleHeels + the sync service: each player's latest published tag is delivered to everyone else
/// on <see cref="Flush"/>. Like the real debounces, publishes between flushes collapse into the last one.
/// </summary>
internal sealed class FakeTagHub
{
    private readonly Dictionary<ActorKey, FakeChannel> channels = [];
    private readonly Dictionary<ActorKey, string?> pending = [];

    /// <summary>When false, flushed tags are discarded instead of delivered (a lost update).</summary>
    public bool Deliver { get; set; } = true;

    public FakeChannel Join(ActorKey owner)
    {
        var channel = new FakeChannel(this, owner);
        channels[owner] = channel;
        return channel;
    }

    internal void Publish(ActorKey owner, string? value) => pending[owner] = value;

    /// <summary>Delivers a raw tag value as if published by <paramref name="owner"/> (who needs no client).</summary>
    public void Inject(ActorKey owner, string? value) => pending[owner] = value;

    public void Flush()
    {
        foreach (var (owner, value) in pending)
        {
            if (!Deliver)
                continue;
            foreach (var (key, channel) in channels)
            {
                if (key != owner)
                    channel.Inbox.Enqueue(new RemoteTag(owner, value));
            }
        }
        pending.Clear();
    }
}

internal sealed class FakeChannel(FakeTagHub hub, ActorKey owner) : ITagChannel
{
    public Queue<RemoteTag> Inbox { get; } = new();

    public int PublishCount { get; private set; }

    public string? Current { get; private set; }

    public void Publish(string? value)
    {
        PublishCount++;
        Current = value;
        hub.Publish(owner, value);
    }

    public bool TryReceive(out RemoteTag tag) => Inbox.TryDequeue(out tag);
}

