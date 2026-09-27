using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using CoPose.Core.Net;
using CoPose.Core.Sync;
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

    /// <summary>When set, writer tasks stay pending until <see cref="CompleteDeferred"/>.</summary>
    public bool DeferWrites { get; set; }

    public bool FailExports { get; set; }

    public List<(ActorKey Actor, string[] Bones)> BoneApplies { get; } = [];
    public List<ActorKey> SnapshotApplies { get; } = [];
    public int Exports { get; private set; }

    private readonly List<(TaskCompletionSource<bool> Tcs, Action Write)> deferred = [];

    public FakeWorld Add(ActorKey key, int bones = 10)
    {
        actors[key] = (new ActorHandle((uint)(200 + actors.Count)), new FakeSkeleton(bones));
        return this;
    }

    public FakeSkeleton this[ActorKey key] => actors[key].Skeleton;

    public void Remove(ActorKey key) => actors.Remove(key);

    /// <summary>A local edit, as if made with a Ktisis gizmo.</summary>
    public void Pose(ActorKey key, int bone, Vector3 position) => this[key].Values[bone].Position = position;

    public bool TryResolve(ActorKey key, out ActorHandle handle)
    {
        var found = actors.TryGetValue(key, out var entry);
        handle = entry.Handle;
        return found;
    }

    private FakeSkeleton? ByHandle(ActorHandle handle) =>
        actors.Values.FirstOrDefault(a => a.Handle == handle).Skeleton;

    private ActorKey KeyOf(ActorHandle handle) => actors.First(a => a.Value.Handle == handle).Key;

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
        BoneApplies.Add((KeyOf(actor), bones.Select(b => b.Name).ToArray()));
        return Write(() =>
        {
            foreach (var bone in bones)
            {
                var i = skeleton.IndexOf(bone.Name);
                if (i >= 0)
                    skeleton.Values[i] = bone.Value;
            }
        });
    }

    public Task<bool> ApplySnapshotAsync(ActorHandle actor, string poseJson)
    {
        var skeleton = ByHandle(actor)!;
        SnapshotApplies.Add(KeyOf(actor));
        var bones = JsonSerializer.Deserialize<Dictionary<string, float[]>>(poseJson)!;
        return Write(() =>
        {
            foreach (var (name, v) in bones)
            {
                var i = skeleton.IndexOf(name);
                if (i >= 0)
                    skeleton.Values[i] = new BoneSample(new Vector3(v[0], v[1], v[2]), new Quaternion(v[3], v[4], v[5], v[6]), new Vector3(v[7], v[8], v[9]));
            }
        });
    }

    public Task<string?> ExportSnapshotAsync(ActorHandle actor)
    {
        Exports++;
        if (FailExports)
            return Task.FromResult<string?>(null);
        var skeleton = ByHandle(actor)!;
        var bones = new Dictionary<string, float[]>();
        for (var i = 0; i < skeleton.Names.Length; i++)
        {
            var s = skeleton.Values[i];
            bones[skeleton.Names[i]] = [s.Position.X, s.Position.Y, s.Position.Z, s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W, s.Scale.X, s.Scale.Y, s.Scale.Z];
        }
        return Task.FromResult<string?>(JsonSerializer.Serialize(bones));
    }

    private Task<bool> Write(Action write)
    {
        if (!DeferWrites)
        {
            write();
            return Task.FromResult(true);
        }
        var tcs = new TaskCompletionSource<bool>();
        deferred.Add((tcs, write));
        return tcs.Task;
    }

    public void CompleteDeferred()
    {
        foreach (var (tcs, write) in deferred)
        {
            write();
            tcs.SetResult(true);
        }
        deferred.Clear();
    }
}

/// <summary>An in-memory host: sequences messages and delivers them to all members when flushed.</summary>
internal sealed class FakeHub
{
    private readonly List<FakeTransport> members = [];
    private readonly Queue<(Guid Sender, MsgType Type, byte[] Body)> queue = new();
    private long seq;

    public FakeTransport Join(PeerInfo peer)
    {
        var transport = new FakeTransport(this, peer, [.. members.Select(m => m.Self), peer]);
        foreach (var member in members)
            member.Push(new PeerJoinedEvent(peer));
        members.Add(transport);
        return transport;
    }

    public void Leave(FakeTransport transport)
    {
        members.Remove(transport);
        foreach (var member in members)
            member.Push(new PeerLeftEvent(transport.Self.ClientId));
    }

    internal void Enqueue(Guid sender, MsgType type, byte[] body) => queue.Enqueue((sender, type, body));

    public int Queued => queue.Count;

    /// <summary>Sequences and delivers everything queued so far.</summary>
    public void Flush()
    {
        while (queue.TryDequeue(out var item))
        {
            var envelope = new Envelope(item.Type, item.Sender, ++seq, item.Body);
            foreach (var member in members)
                member.Push(new MessageEvent(envelope));
        }
    }
}

internal sealed class FakeTransport(FakeHub hub, PeerInfo self, IReadOnlyList<PeerInfo> initial) : ISessionTransport
{
    private readonly Channel<SessionEvent> events = Channel.CreateUnbounded<SessionEvent>();

    public PeerInfo Self { get; } = self;
    public IReadOnlyList<PeerInfo> InitialParticipants { get; } = initial;
    public ChannelReader<SessionEvent> Events => events.Reader;
    public List<(MsgType Type, byte[] Body)> Sent { get; } = [];

    public IEnumerable<T> SentOf<T>(MsgType type) => Sent.Where(s => s.Type == type).Select(s => Wire.Deserialize<T>(s.Body));

    public SendResult Send(MsgType type, byte[] body)
    {
        if (body.Length > ProtocolInfo.MaxBodyBytes)
            return SendResult.TooLarge;
        Sent.Add((type, body));
        hub.Enqueue(Self.ClientId, type, body);
        return SendResult.Ok;
    }

    public void Push(SessionEvent e) => events.Writer.TryWrite(e);

    public Task LeaveAsync()
    {
        hub.Leave(this);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Two clients (A hosting, B joined) that can both see both characters.</summary>
internal sealed class TwoClients
{
    public static readonly ActorKey KeyA = new("Alice", 73);
    public static readonly ActorKey KeyB = new("Bob", 73);

    public FakeHub Hub { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeWorld WorldA { get; } = new FakeWorld().Add(KeyA).Add(KeyB);
    public FakeWorld WorldB { get; } = new FakeWorld().Add(KeyA).Add(KeyB);
    public FakeTransport TransportA { get; }
    public FakeTransport TransportB { get; }
    public SceneSync A { get; }
    public SceneSync B { get; }

    public TwoClients(SceneSyncOptions? options = null)
    {
        TransportA = Hub.Join(new PeerInfo(Guid.NewGuid(), KeyA, "Alice"));
        TransportB = Hub.Join(new PeerInfo(Guid.NewGuid(), KeyB, "Bob"));
        A = new SceneSync(TransportA, WorldA, WorldA, WorldA, WorldA, Clock, options);
        B = new SceneSync(TransportB, WorldB, WorldB, WorldB, WorldB, Clock, options);
    }

    /// <summary>Ticks both clients and delivers queued messages, <paramref name="rounds"/> times, advancing the clock past the sample interval.</summary>
    public void Run(int rounds = 5)
    {
        for (var i = 0; i < rounds; i++)
        {
            A.Tick();
            B.Tick();
            Hub.Flush();
            Clock.Advance(60);
        }
    }

    public void ClearSent()
    {
        TransportA.Sent.Clear();
        TransportB.Sent.Clear();
    }
}
