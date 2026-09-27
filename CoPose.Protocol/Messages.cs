using MessagePack;

namespace CoPose.Protocol;

/// <summary>Identifies a character across clients. GPose object indices differ per client and never go into tags.</summary>
[MessagePackObject]
public readonly record struct ActorKey([property: Key(0)] string Name, [property: Key(1)] ushort HomeWorldId) : IComparable<ActorKey>
{
    public int CompareTo(ActorKey other)
    {
        var byName = string.CompareOrdinal(Name, other.Name);
        return byName != 0 ? byName : HomeWorldId.CompareTo(other.HomeWorldId);
    }

    public override string ToString() => $"{Name}@{HomeWorldId}";
}
