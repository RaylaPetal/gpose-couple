using System.Numerics;
using CoPose.Protocol;

namespace CoPose.Core.Sync;

/// <summary>A client-local reference to an actor (the GPose object index in the plugin). Never put in tags.</summary>
public readonly record struct ActorHandle(uint ObjectIndex);

/// <summary>Local-space position/rotation and model-space scale of one bone.</summary>
public struct BoneSample
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public BoneSample(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }
}

public readonly record struct BoneValue(string Name, BoneSample Value);

/// <summary>
/// Reusable read buffer. The reader fills <see cref="Names"/>/<see cref="Samples"/> and sets
/// <see cref="LayoutId"/>, which must change whenever the set or order of bones changes (e.g. a gear swap).
/// </summary>
public sealed class PoseBuffer
{
    /// <summary>
    /// Pseudo-bone carrying the actor's world transform (the draw object's position, rotation and scale, which is
    /// what Ktisis moves when the whole actor is dragged). It syncs like any other bone.
    /// </summary>
    public const string WorldBone = "@world";

    public string[] Names = [];
    public BoneSample[] Samples = [];
    public int Count;
    public long LayoutId;

    public void EnsureCapacity(int count)
    {
        if (Names.Length >= count)
            return;
        var size = Math.Max(count, Names.Length * 2);
        Array.Resize(ref Names, size);
        Array.Resize(ref Samples, size);
    }
}

public interface IActorRegistry
{
    bool TryResolve(ActorKey key, out ActorHandle handle);
}

public interface IPoseReader
{
    /// <summary>Reads the actor's current bones. Each bone name appears at most once. Returns false if no skeleton is available.</summary>
    bool TryRead(ActorHandle actor, PoseBuffer into);
}

public interface IPoseWriter
{
    /// <summary>Applies bone values by name. Unknown names are ignored. Called on the thread that runs <see cref="TagSync.Tick"/>.</summary>
    Task<bool> ApplyBonesAsync(ActorHandle actor, IReadOnlyList<BoneValue> bones);
}

/// <summary>Whether the local game state allows syncing (in GPose, Ktisis available and posing).</summary>
public interface ISyncEnvironment
{
    bool CanSync { get; }
}

public interface IClock
{
    long NowMs { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public long NowMs => Environment.TickCount64;
}
