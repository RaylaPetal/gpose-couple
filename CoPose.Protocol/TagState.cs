using MessagePack;

namespace CoPose.Protocol;

/// <summary>
/// Everything a CoPose client publishes in its SimpleHeels tag.
/// <para>
/// <see cref="Actors"/> holds only the bone values <em>this client authored</em> (its latest edits), each with the
/// logical clock it was made at; the author is always <see cref="Self"/>. Receivers merge it into their own state,
/// keeping whichever version is newer per bone, so a tag is a complete statement of the owner's edits and a missed,
/// repeated or reordered tag update never loses anything.
/// </para>
/// </summary>
[MessagePackObject]
public sealed record TagState(
    [property: Key(0)] int Version,
    [property: Key(1)] ActorKey Self,
    [property: Key(2)] ActorKey? Partner,
    [property: Key(3)] bool Ready,
    [property: Key(4)] ActorKey[] Resolved,
    [property: Key(5)] uint Clock,
    [property: Key(6)] TagActor[] Actors);

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
