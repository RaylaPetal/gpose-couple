using CoPose.Protocol;

namespace CoPose.Core.Net;

public enum SessionError
{
    PortInUse,
    HostUnreachable,
    InvalidInvite,
    VersionMismatch,
    SessionFull,
    HandshakeFailed,
}

public sealed class SessionException(SessionError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SessionError Error { get; } = error;

    internal static SessionException FromReject(RejectFrame reject) => reject.Reason switch
    {
        RejectReasons.InvalidInvite => new SessionException(SessionError.InvalidInvite, "The host rejected the invite (invalid invite).", null),
        RejectReasons.VersionMismatch => new SessionException(SessionError.VersionMismatch, $"Version mismatch: {reject.Detail}", null),
        RejectReasons.SessionFull => new SessionException(SessionError.SessionFull, "The session is full.", null),
        _ => new SessionException(SessionError.HandshakeFailed, $"The host rejected the join: {reject.Reason} {reject.Detail}".Trim(), null),
    };
}
