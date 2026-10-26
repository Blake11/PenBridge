using Activity = Android.App.Activity;
using Stopwatch = System.Diagnostics.Stopwatch;
using Android.Content;
using Android.OS;
using Android.Util;
using PenBridge.Android.Pad;
using PenBridge.Android.Services;
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Android.Mirror;

/// <summary>
/// Connects the pad's <see cref="MirrorView"/> to the session (ADR-0038, spec "Control messages" rules and
/// "Heads → Android"): sends MirrorStart while mirroring is wanted and MirrorStop when it no longer is, feeds the
/// decoder, reports rendered frames, shows the state overlay, and is the pad menu's quick toggle.
/// </summary>
/// <remarks>
/// <para><b>Wanted</b> = MirrorEnabled in the Mac's snapshot, the pad in the foreground (resumed, so not behind
/// Settings or in the background), the screen on, and a session that offers mirroring. Each start re-reads the decoder
/// capabilities (<see cref="MirrorDecoderCapabilities.Get"/> re-probes only when the display changed). TLS fallback is
/// handled inside Core: it sends MirrorStop, reports Unavailable/TlsFallback and asks again once UDP is back. A new
/// session (background → foreground closes and reopens it) sends a new MirrorStart, so video resumes with a new
/// epoch, which starts with a keyframe.</para>
/// <para><b>Threads.</b> Lifecycle and UI on the UI thread. <see cref="OnMirrorConfig"/> runs on the read loop and
/// configures the decoder there, before the epoch's video can arrive; the view follows on the UI thread.
/// <see cref="OnVideoFrame"/> runs on Core's video receive thread and only copies into the decoder (allocation-free).
/// Nothing here runs on the pen input path.</para>
/// </remarks>
internal sealed class MirrorSession : IMirrorSink, IMirrorToggle, IDisposable
{
    private const string Tag = "PenBridge";

    private readonly Activity _activity;
    private readonly ClientHost _host;
    private readonly MirrorView _view;
    private readonly Handler _ui;
    private readonly PowerManager? _power;
    private readonly Action<uint, long> _onRendered;
    private readonly Action _onKeyframeNeeded;
    private readonly Action<string> _onError;
    private volatile bool _active; // a MirrorStart went out for _session: accept its config, state and video
    private int _session; // the session MirrorStart went out for, 0 = none (UI thread)
    private int _failedSession; // the decoder gave up in this session: don't ask again until the pad comes back
    private int _noDecoderSession; // the probe found no decoder for this session (shown once, not re-probed)
    private MirrorRequest _request;
    private MirrorCodecs _offeredCodecs; // the previous MirrorStart's codecs, to log a codec the re-probe dropped
    private bool _foreground;
    private bool _disposed;

    public MirrorSession(Activity activity, ClientHost host, MirrorView view)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(view);
        _activity = activity;
        _host = host;
        _view = view;
        _ui = new Handler(Looper.MainLooper!);
        _power = activity.GetSystemService(Context.PowerService) as PowerManager;
        _onRendered = OnRendered;
        _onKeyframeNeeded = OnKeyframeNeeded;
        _onError = OnDecoderError;
        _host.StateChanged += OnHostStateChanged;
        _host.MacSettingsChanged += OnMacSettingsChanged;
        _host.MirrorSink = this;
    }

    public bool IsAvailable => _host.MirrorOffered;

    public bool IsEnabled => _host.MacSettings?.MirrorEnabled == true;

    public void SetEnabled(bool enabled)
    {
        if (_host.MirrorOffered)
        {
            _failedSession = 0; // the user asks again: give the decoder another try
            _host.UpdateMacSettings([SettingEntry.Bool(MacSettingKey.MirrorEnabled, enabled)]);
        }
    }

    /// <summary>The pad is resumed (true) or paused (false). UI thread.</summary>
    public void SetForeground(bool foreground)
    {
        if (_foreground == foreground)
        {
            return;
        }

        _foreground = foreground;
        if (foreground)
        {
            _failedSession = 0;
        }

        Evaluate();
    }

    public void OnMirrorConfig(MirrorStreamConfig config)
    {
        if (!_active || config.WidthPx < 2 || config.HeightPx < 2)
        {
            return;
        }

        var video = new MirrorVideoConfig(config.Codec, config.WidthPx, config.HeightPx, config.Fps);
        try
        {
            _view.ConfigureDecoder(video);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ui.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!_active)
            {
                _view.Decoder.Stop(); // stopped meanwhile: undo the configure
                return;
            }

            // An OnMirrorState posted before this epoch may have hidden the view after ConfigureDecoder ran (Hide stops
            // the decoder), so configure again then.
            bool wasShown = _view.IsShown;
            _view.Show(video, configureDecoder: !wasShown);
            _view.SetState(MirrorOverlayState.None);
        });
    }

    public void OnMirrorState(MirrorState state, MirrorReason reason)
    {
        if (!_active)
        {
            return;
        }

        _ui.Post(() =>
        {
            if (_disposed || !_active)
            {
                return;
            }

            MirrorPresentation p = MirrorStatusText.Describe(state, reason);
            if (!p.KeepVideo)
            {
                _view.Hide();
            }

            _view.SetState(p.Overlay, p.Reason);
        });
    }

    public void OnVideoFrame(in VideoFrame frame)
    {
        if (_active)
        {
            _ = _view.Feed(frame.Span, frame.IsKeyframe, frame.FrameNumber);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopLocal();
        _disposed = true;
        _host.StateChanged -= OnHostStateChanged;
        _host.MacSettingsChanged -= OnMacSettingsChanged;
        if (ReferenceEquals(_host.MirrorSink, this))
        {
            _host.MirrorSink = null;
        }

        _ui.Dispose();
    }

    private static long StopwatchUs(long timestamp) => (long)(timestamp * (1_000_000.0 / Stopwatch.Frequency));

    private void OnHostStateChanged(object? sender, ClientHostStateChangedEventArgs e) => Evaluate();

    private void OnMacSettingsChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>UI thread: sends MirrorStart or MirrorStop when "wanted" changed (or the session did).</summary>
    private void Evaluate()
    {
        if (_disposed)
        {
            return;
        }

        int session = _host.SessionId;
        bool wanted = session != 0 && _foreground && (_power?.IsInteractive ?? true)
            && _host.MirrorOffered && _host.MacSettings?.MirrorEnabled == true && _failedSession != session;
        if (!wanted)
        {
            if (_session != 0 || _active)
            {
                StopLocal();
            }

            if (session == 0 || _host.MacSettings?.MirrorEnabled != true)
            {
                _noDecoderSession = 0;
                _view.SetState(MirrorOverlayState.None); // mirroring off or no session: no overlay
            }

            return;
        }

        if (_session == session || _noDecoderSession == session)
        {
            return; // already asked in this session
        }

        if (_session != 0)
        {
            StopLocal(); // the previous session's view
        }

        MirrorDecoderCapabilities caps = MirrorDecoderCapabilities.Get(_activity);
        LogDroppedCodecs(caps.Codecs);
        if (caps.Codecs == MirrorCodecs.None)
        {
            _noDecoderSession = session;
            _view.SetState(MirrorOverlayState.Unavailable, MirrorStatusText.NoDecoder);
            Log.Warn(Tag, "Mirror: no hardware decoder, not asking for video");
            return;
        }

        _request = new MirrorRequest(
            caps.Codecs,
            Math.Clamp(caps.MaxWidth, 1, ushort.MaxValue),
            Math.Clamp(caps.MaxHeight, 1, ushort.MaxValue),
            Math.Clamp(caps.MaxFps, 1, byte.MaxValue));
        _view.ClockOrigin = 0; // raw Stopwatch µs; converted to the Ping clock in OnRendered
        _view.FrameRendered = _onRendered;
        _view.KeyframeNeeded = _onKeyframeNeeded;
        _view.Error = _onError;
        _view.SetState(MirrorOverlayState.None);
        _active = true;
        if (!_host.StartMirror(_request))
        {
            _active = false;
            return;
        }

        _session = session;
        Log.Info(Tag, $"Mirror: MirrorStart {_request.Codecs} max {_request.MaxWidthPx}x{_request.MaxHeightPx}@{_request.MaxFps}");
    }

    /// <summary>UI thread: MirrorStop (if this session asked), hide the video and the overlay.</summary>
    private void StopLocal()
    {
        _active = false;
        if (_session != 0)
        {
            if (_session == _host.SessionId)
            {
                _host.StopMirror();
                Log.Info(Tag, "Mirror: MirrorStop");
            }

            _session = 0;
        }

        _view.Hide();
        _view.SetState(MirrorOverlayState.None);
    }

    private void LogDroppedCodecs(MirrorCodecs codecs)
    {
        MirrorCodecs dropped = _offeredCodecs & ~codecs;
        if (dropped != MirrorCodecs.None)
        {
            Log.Warn(Tag, $"Mirror: the decoder re-probe dropped {dropped} (display changed); offering {codecs}");
        }

        _offeredCodecs = codecs;
    }

    /// <summary>
    /// Decoder thread: a frame reached the screen. The decoder's time is Stopwatch µs; the Mac relates renders to the
    /// client's Ping clock, so the render's age is subtracted from <see cref="ClientHost.MirrorNowUs"/>.
    /// </summary>
    private void OnRendered(uint frame, long renderedUs)
    {
        if (!_active)
        {
            return;
        }

        ulong now = _host.MirrorNowUs;
        if (now == 0)
        {
            return;
        }

        long age = Math.Max(0, StopwatchUs(Stopwatch.GetTimestamp()) - renderedUs);
        _host.ReportFrameRendered(frame, (ulong)age >= now ? 0 : now - (ulong)age);
    }

    /// <summary>
    /// Decoder or Feed thread: the decoder lost frames Core delivered (its queue overflowed, or the codec restarted
    /// without a queued keyframe). Core's own keyframe requests only cover network loss, so ask it explicitly; it repeats
    /// the request until a keyframe arrives.
    /// </summary>
    private void OnKeyframeNeeded()
    {
        if (_active)
        {
            _host.RequestKeyframe();
        }
    }

    /// <summary>Decoder thread: the decoder can't run. Stop asking for video in this session and say why.</summary>
    private void OnDecoderError(string message)
    {
        _ui.Post(() =>
        {
            if (_disposed || !_active)
            {
                return;
            }

            Log.Warn(Tag, "Mirror: decoder failed (" + message + "), stopping video for this session");
            _failedSession = _session;
            StopLocal();
            _view.SetState(MirrorOverlayState.Unavailable, "The video decoder failed: " + message + ".");
        });
    }
}
