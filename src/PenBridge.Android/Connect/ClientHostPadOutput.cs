using PenBridge.Android.Pad;
using PenBridge.Android.Services;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Connect;

/// <summary>
/// Adapts <see cref="ClientHost"/> to the pad's <see cref="IPadOutput"/>: input goes straight through to the host's
/// allocation-free <see cref="ClientHost.SendState"/> (sender thread), and the host's state is folded into the
/// <see cref="PadConnectionState"/> the status dot renders. Create and dispose on the UI thread.
/// </summary>
internal sealed class ClientHostPadOutput : IPadOutput, IDisposable
{
    private readonly ClientHost _host;
    private PadConnectionState _state;
    private bool _disposed;

    public ClientHostPadOutput(ClientHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _state = Map(host.State);
        host.StateChanged += OnHostStateChanged;
    }

    /// <summary>Read and written on the UI thread only (the host raises state changes there).</summary>
    public PadConnectionState ConnectionState => _state;

    public event EventHandler? ConnectionStateChanged;

    public bool SendState(in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history) =>
        _host.SendState(pen, touches, history);

    public void UpdateSurface(SurfaceInfoMessage surface)
    {
        if (!_disposed)
        {
            _host.UpdateSurface(surface);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.StateChanged -= OnHostStateChanged;
    }

    /// <summary>Host phase → pad overlay phase (pairing counts as connecting; errors and idle as disconnected).</summary>
    internal static PadConnectionState Map(ClientHostState state) => state.Status switch
    {
        ClientHostStatus.Connected => PadConnectionState.Connected(
            state.Transport,
            state.RoundTripTime is { } rtt ? (int)Math.Min(Math.Round(rtt.TotalMilliseconds), int.MaxValue) : null),
        ClientHostStatus.Connecting when state.IsReconnecting => PadConnectionState.Reconnecting,
        ClientHostStatus.Connecting or ClientHostStatus.Pairing => PadConnectionState.Connecting,
        _ => PadConnectionState.Disconnected,
    };

    private void OnHostStateChanged(object? sender, ClientHostStateChangedEventArgs e)
    {
        PadConnectionState next = Map(e.State);
        if (_disposed || next == _state) // an event already being raised still reaches a just-disposed adapter
        {
            return;
        }

        _state = next;
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
