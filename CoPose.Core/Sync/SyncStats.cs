namespace CoPose.Core.Sync;

/// <summary>Diagnostics shown in the plugin's debug panel.</summary>
public sealed class SyncStats
{
    /// <summary>Times the local pairing tag was (re)published.</summary>
    public long TagPublishes { get; private set; }

    /// <summary>State messages sent to the relay.</summary>
    public long MessagesSent { get; private set; }

    /// <summary>State messages received from the partner in this session.</summary>
    public long MessagesReceived { get; private set; }

    /// <summary>Size in bytes of the last sent relay frame.</summary>
    public int LastMessageBytes { get; private set; }

    /// <summary>Clock time of the partner's last state message, or null if none yet in this session.</summary>
    public long? LastPartnerMessageAtMs { get; private set; }

    /// <summary>Bones in writes the pose writer has acknowledged successfully (not attempted writes).</summary>
    public long AppliedBones { get; private set; }

    /// <summary>Average time spent reading and diffing per sample, in milliseconds.</summary>
    public double SampleMs { get; private set; }

    public string? LastError { get; private set; }

    public long LastErrorAtMs { get; private set; }

    internal void TagPublished() => TagPublishes++;

    internal void Sent(int bytes)
    {
        MessagesSent++;
        LastMessageBytes = bytes;
    }

    internal void Received(long nowMs)
    {
        MessagesReceived++;
        LastPartnerMessageAtMs = nowMs;
    }

    internal void Applied(int bones) => AppliedBones += bones;

    internal void SampleTook(double ms) => SampleMs = SampleMs == 0 ? ms : SampleMs * 0.9 + ms * 0.1;

    internal void Error(string message, long nowMs)
    {
        LastError = message;
        LastErrorAtMs = nowMs;
    }

    /// <summary>A new session started: partner message stats start over.</summary>
    internal void SessionStarted()
    {
        MessagesReceived = 0;
        LastPartnerMessageAtMs = null;
    }
}
