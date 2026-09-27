using MessagePack;

namespace CoPose.Protocol;

/// <summary>Identifies a character across clients. GPose object indices differ per client and never go on the wire.</summary>
[MessagePackObject]
public readonly record struct ActorKey([property: Key(0)] string Name, [property: Key(1)] ushort HomeWorldId)
{
    public override string ToString() => $"{Name}@{HomeWorldId}";
}

[MessagePackObject]
public sealed record PeerInfo(
    [property: Key(0)] Guid ClientId,
    [property: Key(1)] ActorKey Actor,
    [property: Key(2)] string DisplayName);

public enum MsgType : byte
{
    BoneDelta = 1,
    FullSnapshot = 2,
    Presence = 3,
    SnapshotRequest = 4,
}

/// <summary>A message as delivered by the host: stamped with the sender and the session sequence number.</summary>
[MessagePackObject]
public sealed record Envelope(
    [property: Key(0)] MsgType Type,
    [property: Key(1)] Guid SenderId,
    [property: Key(2)] long Seq,
    [property: Key(3)] byte[] Body);

/// <summary>One bone's local-space position/rotation and model-space scale, addressed by name.</summary>
[MessagePackObject]
public readonly record struct BoneTransform(
    [property: Key(0)] string Name,
    [property: Key(1)] float Px,
    [property: Key(2)] float Py,
    [property: Key(3)] float Pz,
    [property: Key(4)] float Rx,
    [property: Key(5)] float Ry,
    [property: Key(6)] float Rz,
    [property: Key(7)] float Rw,
    [property: Key(8)] float Sx,
    [property: Key(9)] float Sy,
    [property: Key(10)] float Sz);

/// <summary>Changed bones of one actor. <see cref="LocalSeq"/> is the sender's own counter, used to match the host's echo.</summary>
[MessagePackObject]
public sealed record BoneDelta(
    [property: Key(0)] ActorKey Actor,
    [property: Key(1)] long LocalSeq,
    [property: Key(2)] BoneTransform[] Bones);

/// <summary>A full Ktisis pose export (JSON, Brotli-compressed) for one actor.</summary>
[MessagePackObject]
public sealed record FullSnapshot(
    [property: Key(0)] ActorKey Actor,
    [property: Key(1)] long LocalSeq,
    [property: Key(2)] byte[] PoseJsonBrotli);

[MessagePackObject]
public sealed record Presence(
    [property: Key(0)] bool Ready,
    [property: Key(1)] ActorKey[] Resolved);

[MessagePackObject]
public sealed record SnapshotRequest([property: Key(0)] ActorKey Actor);
