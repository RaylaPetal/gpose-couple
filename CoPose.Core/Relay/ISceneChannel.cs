namespace CoPose.Core.Relay;

public enum RelayStatus
{
    /// <summary>Not in a session: no connection wanted.</summary>
    Idle,
    Connecting,
    Connected,

    /// <summary>The last attempt failed; retrying after a delay.</summary>
    Unreachable,
}

public abstract record ChannelEvent;

public sealed record ChannelConnected : ChannelEvent;

public sealed record ChannelDisconnected(string Reason) : ChannelEvent;

/// <summary>A frame from the relay: <c>[FrameKind][body]</c>.</summary>
public sealed record ChannelFrame(byte[] Frame) : ChannelEvent;

/// <summary>The connection to a session's relay room. Frames and events are drained on the tick thread.</summary>
public interface ISceneChannel : IDisposable
{
    RelayStatus Status { get; }

    string? LastError { get; }

    /// <summary>The room currently joined, or null.</summary>
    string? Room { get; }

    /// <summary>Connects (and keeps reconnecting) to a room. Joining another room leaves the current one.</summary>
    void Join(string roomId, string participantId);

    /// <summary>Stops connecting and closes the connection.</summary>
    void Leave();

    /// <summary>Queues a frame. Returns false when not connected.</summary>
    bool Send(byte[] frame);

    bool TryReceive(out ChannelEvent channelEvent);
}
