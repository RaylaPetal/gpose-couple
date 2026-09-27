namespace CoPose.Core.Sync;

/// <summary>
/// Synced state of one actor's bones on this client.
/// <list type="bullet">
/// <item><see cref="Committed"/>: the value considered in sync (what the diff compares against).</item>
/// <item><see cref="Pending"/>: local sequence of the newest local edit not yet echoed by the host (0 = none).
/// Remote updates for pending bones are ignored, because anything arriving before our echo was sequenced earlier.</item>
/// <item><see cref="InFlight"/>: number of writer calls still applying to the bone; the diff skips these.</item>
/// </list>
/// </summary>
internal sealed class ActorState
{
    private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);

    public long LayoutId { get; private set; }

    /// <summary>Incremented on every rebuild so completions from an older layout are ignored.</summary>
    public int Version { get; private set; }

    public bool Initialized { get; private set; }

    public string[] Names { get; private set; } = [];
    public BoneSample[] Committed { get; private set; } = [];
    public long[] Pending { get; private set; } = [];
    public int[] InFlight { get; private set; } = [];
    public int WholeInFlight { get; set; }
    public int Count { get; private set; }

    /// <summary>Takes the buffer as the new baseline: everything committed, nothing pending or in flight.</summary>
    public void Rebuild(PoseBuffer buffer)
    {
        Count = buffer.Count;
        LayoutId = buffer.LayoutId;
        Names = buffer.Names.AsSpan(0, Count).ToArray();
        Committed = buffer.Samples.AsSpan(0, Count).ToArray();
        Pending = new long[Count];
        InFlight = new int[Count];
        WholeInFlight = 0;
        index.Clear();
        for (var i = 0; i < Count; i++)
            index.TryAdd(Names[i], i);
        Version++;
        Initialized = true;
    }

    public void Invalidate()
    {
        Initialized = false;
        Version++;
    }

    public bool TryGetIndex(string name, out int i) => index.TryGetValue(name, out i);

    public bool AnyPending()
    {
        foreach (var p in Pending)
        {
            if (p != 0)
                return true;
        }
        return false;
    }

    /// <summary>Clears pending marks set by the local message with sequence <paramref name="localSeq"/>.</summary>
    public void Acknowledge(int boneIndex, long localSeq)
    {
        if (Pending[boneIndex] == localSeq)
            Pending[boneIndex] = 0;
    }
}
