using CoPose.Protocol;

namespace CoPose.Core.Tags;

public enum PairingStatus
{
    /// <summary>No partner chosen.</summary>
    Idle,

    /// <summary>I chose someone who hasn't chosen me (yet).</summary>
    Waiting,

    /// <summary>We chose each other.</summary>
    Paired,
}

/// <summary>A nearby player whose CoPose tag we have seen.</summary>
public sealed class Peer(ActorKey key)
{
    public ActorKey Key { get; } = key;

    /// <summary>Their latest decoded tag, or null if it was incompatible.</summary>
    public TagState? State { get; internal set; }

    /// <summary>Their protocol version when it differs from ours.</summary>
    public int? IncompatibleVersion { get; internal set; }

    /// <summary>False once their tag disappeared (kept for a grace period).</summary>
    public bool Present { get; internal set; } = true;

    public long LastChangeMs { get; internal set; }

    public bool Compatible => IncompatibleVersion == null && State != null;
}

/// <summary>
/// Mutual pairing from tags: my tag names the partner I chose; we are paired while their tag names me too.
/// A peer whose tag disappears is kept for <see cref="graceMs"/> so brief gaps (zoning, redraws) don't end a session.
/// </summary>
public sealed class Pairing(long graceMs = 10_000)
{
    private readonly Dictionary<ActorKey, Peer> peers = [];
    private bool wasPaired;

    public ActorKey? Chosen { get; private set; }

    /// <summary>Why the last session or request ended, for the UI.</summary>
    public string? EndReason { get; private set; }

    public IEnumerable<Peer> Peers => peers.Values.Where(p => p.Present).OrderBy(p => p.Key);

    public Peer? ChosenPeer => Chosen is { } c && peers.TryGetValue(c, out var p) ? p : null;

    /// <summary>Players who chose me and whom I haven't chosen.</summary>
    public IEnumerable<Peer> Requests(ActorKey me) =>
        Peers.Where(p => p.Compatible && p.State!.Partner == me && Chosen != p.Key);

    public PairingStatus Status(ActorKey me) =>
        Chosen is null ? PairingStatus.Idle
        : ChosenPeer is { Compatible: true } p && p.State!.Partner == me ? PairingStatus.Paired
        : PairingStatus.Waiting;

    /// <summary>Records a tag change on another player's character.</summary>
    public void Observe(ActorKey owner, TagDecodeStatus status, TagState? state, int? otherVersion, long now)
    {
        if (status == TagDecodeStatus.Malformed)
            return;

        if (!peers.TryGetValue(owner, out var peer))
            peers[owner] = peer = new Peer(owner);

        peer.Present = true;
        peer.LastChangeMs = now;
        peer.State = status == TagDecodeStatus.Ok ? state : null;
        peer.IncompatibleVersion = status == TagDecodeStatus.VersionMismatch ? otherVersion : null;
    }

    /// <summary>Records that a player's tag disappeared (plugin unloaded, left the area, unpaired in the sync service).</summary>
    public void ObserveRemoved(ActorKey owner, long now)
    {
        if (peers.TryGetValue(owner, out var peer) && peer.Present)
        {
            peer.Present = false;
            peer.LastChangeMs = now;
        }
    }

    /// <summary>
    /// Drops peers gone longer than the grace period and ends the pairing when the partner leaves or stops.
    /// With <paramref name="holdChosen"/> (a live relay session decides liveness itself) the chosen partner is kept:
    /// sync services defer tag updates in GPose, so tags there are stale, not authoritative.
    /// </summary>
    public void Tick(ActorKey me, long now, bool holdChosen = false)
    {
        foreach (var gone in peers.Values.Where(p => !p.Present && now - p.LastChangeMs >= graceMs && !(holdChosen && p.Key == Chosen)).ToList())
            peers.Remove(gone.Key);

        if (holdChosen)
        {
            wasPaired = Status(me) == PairingStatus.Paired;
            return;
        }

        if (Chosen is { } chosen && !peers.ContainsKey(chosen))
        {
            EndReason = wasPaired ? $"{chosen.Name} is gone (left the area or stopped CoPose)." : $"{chosen.Name} is no longer nearby.";
            Chosen = null;
        }
        else if (wasPaired && Status(me) != PairingStatus.Paired && Chosen is { } partner)
        {
            EndReason = $"{partner.Name} stopped posing together.";
            Chosen = null;
        }

        wasPaired = Status(me) == PairingStatus.Paired;
    }

    /// <summary>Chooses a partner (or accepts their request). Only compatible, present peers can be chosen.</summary>
    public bool Choose(ActorKey key)
    {
        if (!peers.TryGetValue(key, out var peer) || !peer.Present || !peer.Compatible)
            return false;
        Chosen = key;
        EndReason = null;
        return true;
    }

    public void Stop()
    {
        Chosen = null;
        wasPaired = false;
    }

    /// <summary>Ends the pairing for a reason the UI shows (partner stopped over the relay, or gone).</summary>
    public void End(string reason)
    {
        Stop();
        EndReason = reason;
    }
}
