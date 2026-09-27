namespace CoPose.Core;

/// <summary>
/// "At most once per interval" without a sentinel start time: the first call always passes, and
/// <see cref="Reset"/> makes the next one pass. (Starting from <c>long.MinValue</c> overflows <c>now - last</c>.)
/// </summary>
public sealed class IntervalGate(long intervalMs)
{
    private long? last;

    public long IntervalMs { get; } = intervalMs;

    /// <summary>True, and records <paramref name="nowMs"/>, when no pass happened yet or the interval has elapsed.</summary>
    public bool TryPass(long nowMs)
    {
        if (last is { } previous && nowMs - previous < IntervalMs)
            return false;
        last = nowMs;
        return true;
    }

    /// <summary>True when <see cref="TryPass"/> would pass, without recording anything.</summary>
    public bool IsOpen(long nowMs) => last is not { } previous || nowMs - previous >= IntervalMs;

    /// <summary>Records a pass at <paramref name="nowMs"/> without checking (e.g. a forced action).</summary>
    public void Mark(long nowMs) => last = nowMs;

    public void Reset() => last = null;
}
