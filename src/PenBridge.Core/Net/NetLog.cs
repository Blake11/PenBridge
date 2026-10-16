using System.Net;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Net;

/// <summary>
/// Source-generated log messages (no reflection, no formatting when disabled). Never pass keys, PINs, tokens or
/// raw peer text: peer strings go through <see cref="PeerText.Sanitize"/>. Nothing here runs per input sample.
/// </summary>
internal static partial class NetLog
{
    [LoggerMessage(1, LogLevel.Information, "PenBridge server listening on TCP+UDP port {Port}")]
    public static partial void Listening(ILogger logger, int port);

    [LoggerMessage(2, LogLevel.Warning, "Port {Port} is unavailable; trying the next one")]
    public static partial void PortUnavailable(ILogger logger, int port);

    [LoggerMessage(3, LogLevel.Warning, "Refused a connection from {RemoteEndPoint}: too many pending handshakes")]
    public static partial void TooManyPending(ILogger logger, EndPoint? remoteEndPoint);

    [LoggerMessage(4, LogLevel.Debug, "Connection from {RemoteEndPoint}")]
    public static partial void ConnectionAccepted(ILogger logger, EndPoint? remoteEndPoint);

    [LoggerMessage(5, LogLevel.Information, "Connection from {RemoteEndPoint} closed: {Reason}")]
    public static partial void ConnectionClosed(ILogger logger, EndPoint? remoteEndPoint, SessionEndReason reason);

    [LoggerMessage(6, LogLevel.Warning, "Peer {RemoteEndPoint} uses incompatible protocol {Version}")]
    public static partial void IncompatibleVersion(ILogger logger, EndPoint? remoteEndPoint, ProtocolVersion version);

    [LoggerMessage(7, LogLevel.Information, "Pairing attempt ({Kind}) from device {DeviceId}: {Start}")]
    public static partial void PairingStart(ILogger logger, PairingSecretKind kind, Guid deviceId, string start);

    [LoggerMessage(8, LogLevel.Information, "Paired device {DeviceId} ('{DeviceName}')")]
    public static partial void Paired(ILogger logger, Guid deviceId, string deviceName);

    [LoggerMessage(9, LogLevel.Warning, "Pairing with device {DeviceId} failed: {Outcome}, {AttemptsLeft} attempts left")]
    public static partial void PairingFailed(ILogger logger, Guid deviceId, string outcome, int attemptsLeft);

    [LoggerMessage(10, LogLevel.Warning, "Authentication failed for device {DeviceId}")]
    public static partial void AuthFailed(ILogger logger, Guid deviceId);

    [LoggerMessage(11, LogLevel.Information, "Session started for device {DeviceId} ('{DeviceName}')")]
    public static partial void SessionStarted(ILogger logger, Guid deviceId, string deviceName);

    [LoggerMessage(12, LogLevel.Information, "Session for device {DeviceId} ended: {Reason}")]
    public static partial void SessionEnded(ILogger logger, Guid deviceId, SessionEndReason reason);

    [LoggerMessage(13, LogLevel.Information, "Device {DeviceId} refused: another session is active")]
    public static partial void Busy(ILogger logger, Guid deviceId);

    [LoggerMessage(14, LogLevel.Warning, "Peer error {Code}: '{Text}'")]
    public static partial void PeerError(ILogger logger, ProtocolErrorCode code, string text);

    [LoggerMessage(15, LogLevel.Warning, "Protocol violation by {RemoteEndPoint}: {Code}")]
    public static partial void ProtocolViolation(ILogger logger, EndPoint? remoteEndPoint, ProtocolErrorCode code);

    [LoggerMessage(16, LogLevel.Error, "Unexpected failure on connection {RemoteEndPoint}")]
    public static partial void Unexpected(ILogger logger, EndPoint? remoteEndPoint, Exception exception);

    [LoggerMessage(17, LogLevel.Warning, "Forged or corrupted input datagrams exceed the budget; dropping undecrypted")]
    public static partial void ForgedFlood(ILogger logger);

    [LoggerMessage(18, LogLevel.Information, "Session {DeviceId} switched input to TLS fallback")]
    public static partial void ServerTlsFallback(ILogger logger, Guid deviceId);

    [LoggerMessage(19, LogLevel.Warning, "UDP receive error: {Error}")]
    public static partial void UdpReceiveError(ILogger logger, System.Net.Sockets.SocketError error);

    [LoggerMessage(20, LogLevel.Debug, "Dropped accepted connection {RemoteEndPoint}: {Error}")]
    public static partial void AcceptFailed(ILogger logger, EndPoint? remoteEndPoint, string error);

    [LoggerMessage(21, LogLevel.Error, "Input sink failed; closing the session of device {DeviceId}")]
    public static partial void InputFault(ILogger logger, Guid deviceId, Exception exception);

    [LoggerMessage(30, LogLevel.Information, "Connected to '{ServerName}' over {Protocol}")]
    public static partial void ClientConnected(ILogger logger, string serverName, System.Security.Authentication.SslProtocols protocol);

    [LoggerMessage(31, LogLevel.Warning, "No UdpAck within {Timeout}; switching input to TLS fallback")]
    public static partial void ClientTlsFallback(ILogger logger, TimeSpan timeout);

    [LoggerMessage(32, LogLevel.Information, "Disconnected: {Reason}")]
    public static partial void ClientDisconnected(ILogger logger, SessionEndReason reason);

    [LoggerMessage(34, LogLevel.Information, "UDP probe acknowledged; switching input back to UDP")]
    public static partial void ClientUdpRestored(ILogger logger);

    [LoggerMessage(33, LogLevel.Information, "Paired with server '{ServerName}'")]
    public static partial void ClientPaired(ILogger logger, string serverName);

    [LoggerMessage(35, LogLevel.Debug, "Device {DeviceId} requested {Count} setting changes")]
    public static partial void SettingsUpdateReceived(ILogger logger, Guid deviceId, int count);
}
