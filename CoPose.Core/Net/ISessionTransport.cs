using System.Threading.Channels;
using CoPose.Protocol;

namespace CoPose.Core.Net;

public abstract record SessionEvent;

/// <summary>A sequenced message from the host, possibly this participant's own (an echo).</summary>
public sealed record MessageEvent(Envelope Envelope) : SessionEvent;

public sealed record PeerJoinedEvent(PeerInfo Peer) : SessionEvent;

public sealed record PeerLeftEvent(Guid ClientId) : SessionEvent;

public sealed record SessionEndedEvent(string Reason) : SessionEvent;

public enum SendResult
{
    Ok,
    TooLarge,
    NotConnected,
}

/// <summary>A participant's view of a session, whether it is the host or a guest.</summary>
public interface ISessionTransport : IAsyncDisposable
{
    PeerInfo Self { get; }

    /// <summary>Participants known when the session was entered (including <see cref="Self"/>).</summary>
    IReadOnlyList<PeerInfo> InitialParticipants { get; }

    /// <summary>Events in host order. Drained by the consumer on its own thread.</summary>
    ChannelReader<SessionEvent> Events { get; }

    /// <summary>Sends a message to the host for sequencing. Bodies over <see cref="ProtocolInfo.MaxBodyBytes"/> are refused.</summary>
    SendResult Send(MsgType type, byte[] body);

    /// <summary>Leaves the session (a guest) or ends it (the host).</summary>
    Task LeaveAsync();
}
