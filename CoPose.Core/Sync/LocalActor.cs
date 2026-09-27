namespace CoPose.Core.Sync;

/// <summary>
/// What this client last saw on one actor's skeleton.
/// <list type="bullet">
/// <item><see cref="Committed"/>: the value considered in sync; the diff compares live values against it.</item>
/// <item><see cref="InFlight"/>: writer calls still applying to the bone; the diff skips these.</item>
/// </list>
/// </summary>
internal sealed class LocalActor
{
    private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);

    public long LayoutId { get; private set; }

    /// <summary>Incremented on every rebuild so completions from an older layout are ignored.</summary>
    public int Version { get; private set; }

    public bool Initialized { get; private set; }

    public string[] Names { get; private set; } = [];
    public BoneSample[] Committed { get; private set; } = [];
    public int[] InFlight { get; private set; } = [];
    public int Count { get; private set; }

    /// <summary>Takes the buffer as the new baseline: everything committed, nothing in flight.</summary>
    public void Rebuild(PoseBuffer buffer)
    {
        Count = buffer.Count;
        LayoutId = buffer.LayoutId;
        Names = buffer.Names.AsSpan(0, Count).ToArray();
        Committed = buffer.Samples.AsSpan(0, Count).ToArray();
        InFlight = new int[Count];
        index.Clear();
        for (var i = 0; i < Count; i++)
            index.TryAdd(Names[i], i);
        Version++;
        Initialized = true;
    }

    public bool TryGetIndex(string name, out int i) => index.TryGetValue(name, out i);
}
