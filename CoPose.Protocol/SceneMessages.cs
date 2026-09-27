using MessagePack;

namespace CoPose.Protocol;

/// <summary>First byte of every relay frame. Must match CoPose.Relay/src/protocol.ts.</summary>
public enum FrameKind : byte
{
    /// <summary>Client full state: the relay stores it as the sender's latest, then forwards it.</summary>
    Full = 0x01,

    /// <summary>Client live delta or control: forwarded only.</summary>
    Live = 0x02,

    /// <summary>Relay: the other participant is connected. No body.</summary>
    PeerJoined = 0x10,

    /// <summary>Relay: the other participant disconnected. No body.</summary>
    PeerLeft = 0x11,
}

/// <summary>A message between the two clients of a session, carried in a relay frame body.</summary>
[Union(0, typeof(StateMessage))]
[Union(1, typeof(StopMessage))]
public abstract record SceneMessage;

/// <summary>
/// A participant's presence and authored bone values. In a <see cref="FrameKind.Full"/> frame <see cref="Actors"/> holds
/// every bone the sender authored; in a <see cref="FrameKind.Live"/> frame only the ones changed since the last send.
/// </summary>
[MessagePackObject]
public sealed record StateMessage(
    [property: Key(0)] ActorKey Self,
    [property: Key(1)] bool Ready,
    [property: Key(2)] ActorKey[] Resolved,
    [property: Key(3)] uint Clock,
    [property: Key(4)] TagActor[] Actors) : SceneMessage;

/// <summary>The sender left the session ("Stop posing together", or the plugin unloading).</summary>
[MessagePackObject]
public sealed record StopMessage([property: Key(0)] string Reason) : SceneMessage;
