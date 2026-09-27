namespace CoPose.Core.Sync;

/// <summary>Diagnostics shown in the plugin's debug panel. Rates are over the last completed one-second window.</summary>
public sealed class SyncStats
{
    private long windowStart = -1;
    private int sentInWindow;
    private int receivedInWindow;
    private long sentDeltas;
    private long sentDeltaBones;

    public int SentPerSecond { get; private set; }

    /// <summary>Messages received from other participants (own echoes excluded).</summary>
    public int ReceivedPerSecond { get; private set; }

    public long TotalSent { get; private set; }

    public long TotalReceived { get; private set; }

    public double AverageBonesPerDelta => sentDeltas == 0 ? 0 : (double)sentDeltaBones / sentDeltas;

    /// <summary>Average time spent reading and diffing per sample, in milliseconds.</summary>
    public double SampleMs { get; private set; }

    public string? LastError { get; private set; }

    public long LastErrorAtMs { get; private set; }

    internal void Sent(int bonesInDelta = -1)
    {
        sentInWindow++;
        TotalSent++;
        if (bonesInDelta >= 0)
        {
            sentDeltas++;
            sentDeltaBones += bonesInDelta;
        }
    }

    internal void Received()
    {
        receivedInWindow++;
        TotalReceived++;
    }

    internal void SampleTook(double ms) => SampleMs = SampleMs == 0 ? ms : SampleMs * 0.9 + ms * 0.1;

    internal void Error(string message, long nowMs)
    {
        LastError = message;
        LastErrorAtMs = nowMs;
    }

    internal void Roll(long nowMs)
    {
        if (windowStart < 0)
        {
            windowStart = nowMs;
            return;
        }
        if (nowMs - windowStart < 1000)
            return;

        // A gap of several seconds means the last full window had no traffic.
        var stale = nowMs - windowStart >= 2000;
        SentPerSecond = stale ? 0 : sentInWindow;
        ReceivedPerSecond = stale ? 0 : receivedInWindow;
        sentInWindow = 0;
        receivedInWindow = 0;
        windowStart = nowMs;
    }
}
