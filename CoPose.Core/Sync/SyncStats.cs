namespace CoPose.Core.Sync;

/// <summary>Diagnostics shown in the plugin's debug panel.</summary>
public sealed class SyncStats
{
    /// <summary>Times the local tag was (re)published.</summary>
    public long Publishes { get; private set; }

    /// <summary>Tag updates received from the partner.</summary>
    public long Receives { get; private set; }

    /// <summary>Size in characters of the last published tag.</summary>
    public int LastTagBytes { get; private set; }

    /// <summary>Clock time of the partner's last tag update, or null if none yet.</summary>
    public long? LastPartnerTagAtMs { get; private set; }

    /// <summary>Bones applied from the partner's edits so far.</summary>
    public long AppliedBones { get; private set; }

    /// <summary>Average time spent reading and diffing per sample, in milliseconds.</summary>
    public double SampleMs { get; private set; }

    public string? LastError { get; private set; }

    public long LastErrorAtMs { get; private set; }

    internal void Published(int bytes)
    {
        Publishes++;
        LastTagBytes = bytes;
    }

    internal void Received(long nowMs)
    {
        Receives++;
        LastPartnerTagAtMs = nowMs;
    }

    internal void Applied(int bones) => AppliedBones += bones;

    internal void SampleTook(double ms) => SampleMs = SampleMs == 0 ? ms : SampleMs * 0.9 + ms * 0.1;

    internal void Error(string message, long nowMs)
    {
        LastError = message;
        LastErrorAtMs = nowMs;
    }

    /// <summary>A new partner was chosen: receive stats start over, from their current tag if known.</summary>
    internal void PartnerChosen(long? lastTagAtMs)
    {
        Receives = 0;
        LastPartnerTagAtMs = lastTagAtMs;
    }
}
