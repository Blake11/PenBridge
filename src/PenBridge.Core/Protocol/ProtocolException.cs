namespace PenBridge.Core.Protocol;

/// <summary>
/// Thrown by the control-frame layer when the peer violates the wire protocol. The connection must be
/// closed, optionally after sending <c>Error(<see cref="Code"/>)</c>. The message never contains payload bytes.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException()
        : this(ProtocolErrorCode.ProtocolViolation, "Protocol violation.")
    {
    }

    public ProtocolException(string message)
        : this(ProtocolErrorCode.ProtocolViolation, message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ProtocolErrorCode.ProtocolViolation;
    }

    public ProtocolException(ProtocolErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Error code to report to the peer.</summary>
    public ProtocolErrorCode Code { get; }
}
