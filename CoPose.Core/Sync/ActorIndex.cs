using CoPose.Protocol;

namespace CoPose.Core.Sync;

/// <summary>A GPose actor seen by a scan: its local object index, name and home world (0 if unknown).</summary>
public readonly record struct SeenActor(uint Index, string Name, ushort World);

/// <summary>
/// Lookup tables for GPose actors. The lowest object index wins in both tables: GPose creates copies of the real
/// characters before any clones spawned by Brio/Ktisis, so a clone never hides the original.
/// </summary>
public sealed class ActorIndex
{
    private readonly Dictionary<ActorKey, uint> byKey = [];
    private readonly Dictionary<string, uint> byName = new(StringComparer.Ordinal);

    public static readonly ActorIndex Empty = new([]);

    private ActorIndex(IReadOnlyList<SeenActor> actors)
    {
        Actors = actors;
        foreach (var actor in actors.OrderBy(a => a.Index))
        {
            if (string.IsNullOrEmpty(actor.Name))
                continue;
            byKey.TryAdd(new ActorKey(actor.Name, actor.World), actor.Index);
            byName.TryAdd(actor.Name, actor.Index);
        }
    }

    /// <summary>Everything the scan saw, in scan order (for diagnostics).</summary>
    public IReadOnlyList<SeenActor> Actors { get; }

    public static ActorIndex Build(IEnumerable<SeenActor> actors) => new(actors.ToList());

    /// <summary>
    /// Name and home world first; otherwise by name alone, because GPose copies may not carry the same home world
    /// as the overworld character a tag came from.
    /// </summary>
    public bool TryFind(ActorKey key, out uint index) =>
        byKey.TryGetValue(key, out index) || byName.TryGetValue(key.Name, out index);
}
