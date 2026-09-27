using MessagePack;

namespace CoPose.Protocol;

/// <summary>
/// Everything a CoPose client publishes in its SimpleHeels tag: pairing only. Pose data travels over the relay,
/// because sync services defer tag updates while the receiver is in GPose.
/// </summary>
[MessagePackObject]
public sealed record TagState(
    [property: Key(0)] int Version,
    [property: Key(1)] ActorKey Self,
    [property: Key(2)] ActorKey? Partner,
    [property: Key(3)] byte[] Nonce)
{
    public const int NonceLength = 16;
}

/// <summary>
/// Authored bones of one actor. <see cref="Values"/> has 10 floats per bone: position (x, y, z),
/// rotation (x, y, z, w), scale (x, y, z).
/// </summary>
[MessagePackObject]
public sealed record TagActor(
    [property: Key(0)] ActorKey Key,
    [property: Key(1)] string[] Names,
    [property: Key(2)] float[] Values,
    [property: Key(3)] uint[] Clocks)
{
    public const int FloatsPerBone = 10;

    [IgnoreMember]
    public bool IsWellFormed =>
        Names.Length == Clocks.Length && Values.Length == Names.Length * FloatsPerBone;
}
