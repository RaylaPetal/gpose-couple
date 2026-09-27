using MessagePack;

namespace CoPose.Protocol;

/// <summary>Everything that travels over a CoPose TCP connection is one of these frames.</summary>
[Union(0, typeof(HelloFrame))]
[Union(1, typeof(WelcomeFrame))]
[Union(2, typeof(RejectFrame))]
[Union(3, typeof(PeerJoinedFrame))]
[Union(4, typeof(PeerLeftFrame))]
[Union(5, typeof(SendFrame))]
[Union(6, typeof(DeliverFrame))]
[Union(7, typeof(PingFrame))]
[Union(8, typeof(ByeFrame))]
public abstract record Frame;

/// <summary>Guest to host, first frame on a connection.</summary>
[MessagePackObject]
public sealed record HelloFrame(
    [property: Key(0)] int Version,
    [property: Key(1)] byte[] Secret,
    [property: Key(2)] PeerInfo Self) : Frame;

/// <summary>Host to guest after a successful hello.</summary>
[MessagePackObject]
public sealed record WelcomeFrame(
    [property: Key(0)] Guid HostId,
    [property: Key(1)] PeerInfo[] Participants) : Frame;

/// <summary>Host to guest before closing a refused connection.</summary>
[MessagePackObject]
public sealed record RejectFrame(
    [property: Key(0)] string Reason,
    [property: Key(1)] string Detail) : Frame;

[MessagePackObject]
public sealed record PeerJoinedFrame([property: Key(0)] PeerInfo Peer) : Frame;

[MessagePackObject]
public sealed record PeerLeftFrame([property: Key(0)] Guid ClientId) : Frame;

/// <summary>Participant to host: a message to sequence and broadcast.</summary>
[MessagePackObject]
public sealed record SendFrame(
    [property: Key(0)] MsgType Type,
    [property: Key(1)] byte[] Body) : Frame;

/// <summary>Host to participant: a sequenced message, including the participant's own.</summary>
[MessagePackObject]
public sealed record DeliverFrame([property: Key(0)] Envelope Envelope) : Frame;

[MessagePackObject]
public sealed record PingFrame : Frame;

/// <summary>Graceful close. Sent by a guest leaving, or by the host ending the session.</summary>
[MessagePackObject]
public sealed record ByeFrame([property: Key(0)] string Reason) : Frame;

public static class RejectReasons
{
    public const string InvalidInvite = "invalid_invite";
    public const string VersionMismatch = "version_mismatch";
    public const string SessionFull = "session_full";
}

public static class ByeReasons
{
    public const string Left = "left";
    public const string SessionEnded = "session_ended";
}
