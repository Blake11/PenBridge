#if DEBUG
using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>
/// Debug only: the pad menu's "Debug: mirror" submenu: generate test streams at the active area's pixel size, play
/// them through the real decoder path (HEVC / H.264, at 60 or 120 fps, optionally with loss or same-size epoch
/// changes), preview the state
/// overlay, stop. The host forwards menu clicks to <see cref="Handle"/>.
/// </summary>
internal sealed class MirrorDebugMenu : IDisposable
{
    private const string Tag = "PenBridge";
    private const int Generate = 0;
    private const int PlayHevc = 1;
    private const int PlayH264 = 2;
    private const int PlayH264Loss = 3;
    private const int PlayHevc120 = 4;
    private const int OverlayPaused = 5;
    private const int OverlayUnavailable = 6;
    private const int Stop = 7;
    private const int PlayH264Epochs = 8;
    private const int Count = 9;

    private readonly Context _context;
    private readonly MirrorView _view;
    private readonly int _baseId;
    private readonly Func<(int Width, int Height)> _activePixels;
    private MirrorTestPlayer? _player;

    /// <param name="context">Activity context.</param>
    /// <param name="view">The pad's mirror view.</param>
    /// <param name="baseId">First menu item id (the submenu uses 9 ids from here).</param>
    /// <param name="activePixels">Current active area size in surface pixels.</param>
    public MirrorDebugMenu(Context context, MirrorView view, int baseId, Func<(int Width, int Height)> activePixels)
    {
        _context = context;
        _view = view;
        _baseId = baseId;
        _activePixels = activePixels;
    }

    public void AddTo(IMenu menu, int order)
    {
        ISubMenu? sub = menu.AddSubMenu(0, 0, order, "Debug: mirror");
        if (sub is null)
        {
            return;
        }

        (int w, int h) = _activePixels();
        sub.Add(0, _baseId + Generate, 0, $"Generate test streams ({w & ~1}x{h & ~1})");
        sub.Add(0, _baseId + PlayHevc, 1, "Play HEVC");
        sub.Add(0, _baseId + PlayH264, 2, "Play H.264");
        sub.Add(0, _baseId + PlayH264Loss, 3, "Play H.264 with loss");
        sub.Add(0, _baseId + PlayHevc120, 4, "Play HEVC at 120 fps");
        sub.Add(0, _baseId + OverlayPaused, 5, "Overlay: paused");
        sub.Add(0, _baseId + OverlayUnavailable, 6, "Overlay: unavailable");
        sub.Add(0, _baseId + PlayH264Epochs, 7, "Play H.264 with epoch changes");
        sub.Add(0, _baseId + Stop, 8, "Stop mirror test");
    }

    /// <summary>True if <paramref name="itemId"/> was one of this menu's items.</summary>
    public bool Handle(int itemId)
    {
        int item = itemId - _baseId;
        if (item is < 0 or >= Count)
        {
            return false;
        }

        switch (item)
        {
            case Generate:
                StartGenerate();
                break;
            case PlayH264Epochs:
                Play(MirrorCodec.H264, 60, loss: false, epochs: true);
                break;
            case PlayHevc:
                Play(MirrorCodec.Hevc, 60, loss: false);
                break;
            case PlayH264:
                Play(MirrorCodec.H264, 60, loss: false);
                break;
            case PlayH264Loss:
                Play(MirrorCodec.H264, 60, loss: true);
                break;
            case PlayHevc120:
                Play(MirrorCodec.Hevc, 120, loss: false);
                break;
            case OverlayPaused:
                _view.SetState(_view.Overlay.Visibility == ViewStates.Visible ? MirrorOverlayState.None : MirrorOverlayState.Paused);
                break;
            case OverlayUnavailable:
                _view.SetState(MirrorOverlayState.Unavailable, "Screen Recording permission is off on the Mac.");
                break;
            default:
                StopPlayer();
                _view.SetState(MirrorOverlayState.None);
                break;
        }

        return true;
    }

    public void StopPlayer()
    {
        _player?.Dispose();
        _player = null;
    }

    public void Dispose() => StopPlayer();

    private void Play(MirrorCodec codec, int fps, bool loss, bool epochs = false)
    {
        StopPlayer();
        (int w, int h) = _activePixels();
        string? path = MirrorTestStreams.Find(_context, codec, w & ~1, h & ~1, fps)
            ?? MirrorTestStreams.Find(_context, codec, w & ~1, h & ~1, 60);
        if (path is null)
        {
            Toast("No " + codec.Name() + " test file in " + MirrorTestStreams.Directory(_context));
            return;
        }

        if (!MirrorTestStreams.TryParse(path, out int fw, out int fh, out _))
        {
            (fw, fh) = (w & ~1, h & ~1);
        }

        Log.Info(Tag, $"Mirror test: capabilities {MirrorDecoderCapabilities.Get(_context)}");
        _player = new MirrorTestPlayer(_view, path, codec, fw, fh, fps, loss, epochs);
    }

    private void StartGenerate()
    {
        (int w, int h) = _activePixels();
        Toast("Generating test streams…");
        var context = _context;
        _ = Task.Run(() =>
        {
            foreach (MirrorCodec codec in (ReadOnlySpan<MirrorCodec>)[MirrorCodec.Hevc, MirrorCodec.H264])
            {
                try
                {
                    MirrorTestStreams.Generate(context, codec, w, h, 60, 6);
                }
                catch (Exception e) when (e is Java.Lang.Exception or IOException or InvalidOperationException)
                {
                    Log.Warn(Tag, "Mirror test stream " + codec.Name() + " failed: " + e.Message);
                }
            }

            Log.Info(Tag, "Mirror test streams done");
        });
    }

    private void Toast(string text) => global::Android.Widget.Toast.MakeText(_context, text, ToastLength.Short)?.Show();
}
#endif
