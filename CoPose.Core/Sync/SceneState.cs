using System.Numerics;
using CoPose.Protocol;

namespace CoPose.Core.Sync;

/// <summary>Version of a bone value: a Lamport clock, with the author's key breaking ties.</summary>
public readonly record struct BoneVersion(uint Clock, ActorKey Author) : IComparable<BoneVersion>
{
    public int CompareTo(BoneVersion other)
    {
        var byClock = Clock.CompareTo(other.Clock);
        return byClock != 0 ? byClock : Author.CompareTo(other.Author);
    }

    public bool IsNewerThan(BoneVersion other) => CompareTo(other) > 0;
}

public readonly record struct BoneRegister(BoneSample Value, BoneVersion Version);

public readonly record struct BoneChange(ActorKey Actor, string Bone, BoneSample Value);

/// <summary>
/// The shared scene as last-writer-wins registers, one per (actor, bone). Merging is commutative, associative and
/// idempotent, so two clients that have seen the same edits hold the same state regardless of order or repetition.
/// </summary>
public sealed class SceneState
{
    private readonly Dictionary<ActorKey, Dictionary<string, BoneRegister>> actors = [];

    /// <summary>The local Lamport clock: at least every clock seen, local or remote.</summary>
    public uint Clock { get; private set; }

    /// <summary>Changes whenever any register changes.</summary>
    public int Revision { get; private set; }

    public int Count => actors.Values.Sum(a => a.Count);

    /// <summary>A version newer than anything seen so far, for a local edit.</summary>
    public BoneVersion NextVersion(ActorKey author) => new(++Clock, author);

    /// <summary>Records a local edit. <paramref name="version"/> should come from <see cref="NextVersion"/>.</summary>
    public void Record(ActorKey actor, string bone, BoneSample value, BoneVersion version)
    {
        if (!actors.TryGetValue(actor, out var bones))
            actors[actor] = bones = new Dictionary<string, BoneRegister>(StringComparer.Ordinal);
        bones[bone] = new BoneRegister(value, version);
        Clock = Math.Max(Clock, version.Clock);
        Revision++;
    }

    public bool TryGet(ActorKey actor, string bone, out BoneRegister register)
    {
        register = default;
        return actors.TryGetValue(actor, out var bones) && bones.TryGetValue(bone, out register);
    }

    public IEnumerable<(string Bone, BoneRegister Register)> Bones(ActorKey actor) =>
        actors.TryGetValue(actor, out var bones) ? bones.Select(b => (b.Key, b.Value)) : [];

    public IEnumerable<ActorKey> Actors => actors.Keys;

    /// <summary>
    /// Merges bones authored by <paramref name="author"/> (the owner of a received tag) and returns the bones whose
    /// value changed as a result. Advances the clock past every received clock.
    /// </summary>
    public List<BoneChange> Merge(ActorKey author, IReadOnlyList<TagActor> authored)
    {
        var changes = new List<BoneChange>();
        foreach (var actor in authored)
        {
            if (!actor.IsWellFormed)
                continue;
            for (var i = 0; i < actor.Names.Length; i++)
            {
                var version = new BoneVersion(actor.Clocks[i], author);
                Clock = Math.Max(Clock, version.Clock);
                if (TryGet(actor.Key, actor.Names[i], out var existing) && !version.IsNewerThan(existing.Version))
                    continue;

                var value = Read(actor.Values, i);
                if (!actors.TryGetValue(actor.Key, out var bones))
                    actors[actor.Key] = bones = new Dictionary<string, BoneRegister>(StringComparer.Ordinal);
                bones[actor.Names[i]] = new BoneRegister(value, version);
                changes.Add(new BoneChange(actor.Key, actor.Names[i], value));
            }
        }
        if (changes.Count > 0)
            Revision++;
        return changes;
    }

    /// <summary>The bones <paramref name="author"/> currently holds the newest version of, as tag data (sorted for stable output).</summary>
    public TagActor[] ExportAuthored(ActorKey author) => ExportAuthored(author, null);

    /// <summary>Like <see cref="ExportAuthored(ActorKey)"/>, limited to the given bones (a live delta).</summary>
    public TagActor[] ExportAuthored(ActorKey author, IReadOnlySet<(ActorKey Actor, string Bone)>? only)
    {
        var result = new List<TagActor>();
        foreach (var (key, bones) in actors.OrderBy(a => a.Key))
        {
            var mine = bones
                .Where(b => b.Value.Version.Author == author && (only == null || only.Contains((key, b.Key))))
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .ToList();
            if (mine.Count == 0)
                continue;

            var names = new string[mine.Count];
            var clocks = new uint[mine.Count];
            var values = new float[mine.Count * TagActor.FloatsPerBone];
            for (var i = 0; i < mine.Count; i++)
            {
                names[i] = mine[i].Key;
                clocks[i] = mine[i].Value.Version.Clock;
                Write(values, i, mine[i].Value.Value);
            }
            result.Add(new TagActor(key, names, values, clocks));
        }
        return result.ToArray();
    }

    /// <summary>Highest clock among bones <paramref name="author"/> authored (stable across merges of others' edits).</summary>
    public uint AuthoredClock(ActorKey author) =>
        actors.Values.SelectMany(b => b.Values).Where(r => r.Version.Author == author).Select(r => r.Version.Clock).DefaultIfEmpty(0u).Max();

    public void Clear()
    {
        actors.Clear();
        Revision++;
    }

    private static BoneSample Read(float[] v, int i)
    {
        var o = i * TagActor.FloatsPerBone;
        return new BoneSample(new Vector3(v[o], v[o + 1], v[o + 2]), new Quaternion(v[o + 3], v[o + 4], v[o + 5], v[o + 6]), new Vector3(v[o + 7], v[o + 8], v[o + 9]));
    }

    private static void Write(float[] v, int i, BoneSample s)
    {
        var o = i * TagActor.FloatsPerBone;
        v[o] = s.Position.X;
        v[o + 1] = s.Position.Y;
        v[o + 2] = s.Position.Z;
        v[o + 3] = s.Rotation.X;
        v[o + 4] = s.Rotation.Y;
        v[o + 5] = s.Rotation.Z;
        v[o + 6] = s.Rotation.W;
        v[o + 7] = s.Scale.X;
        v[o + 8] = s.Scale.Y;
        v[o + 9] = s.Scale.Z;
    }
}
