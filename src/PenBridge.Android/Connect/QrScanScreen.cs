using Android.Gms.Tasks;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using Java.Util.Concurrent;
using PenBridge.Android.Services;
using PenBridge.Core.Protocol;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.BarCode;

namespace PenBridge.Android.Connect;

/// <summary>
/// QR pairing (ADR-0009/0019): CameraX preview + image analysis feeding ML Kit's bundled barcode model (no Play
/// Services at runtime). Only text Core parses as a <c>penbridge://pair</c> payload is used; anything else keeps
/// scanning. The camera runs only while this screen is on top and the activity is resumed. The CAMERA permission is
/// requested by the activity before this screen opens.
/// </summary>
internal sealed class QrScanScreen : AttemptScreen
{
    private readonly IExecutorService _analysisExecutor = Executors.NewSingleThreadExecutor()!;
    private readonly IBarcodeScanner _scanner = BarcodeScanning.GetClient(
        new BarcodeScannerOptions.Builder().SetBarcodeFormats(Barcode.FormatQrCode, []).Build());

    private PreviewView? _preview;
    private TextView? _hint;
    private TextView? _error;
    private Button? _retry;
    private ProcessCameraProvider? _provider;
    private Preview? _previewUseCase;
    private ImageAnalysis? _analysis;
    private QrAnalyzer? _analyzer;
    private bool _resumed;
    private bool _pairing;
    private bool _disposed;

    public QrScanScreen(MainActivity activity)
        : base(activity)
    {
    }

    public override void Resume()
    {
        _resumed = true;
        base.Resume();
        if (!_pairing)
        {
            StartCamera();
        }
    }

    public override void Pause()
    {
        _resumed = false;
        StopCamera();
    }

    protected override View CreateView()
    {
        var ctx = Activity;
        var root = new FrameLayout(ctx);
        _preview = new PreviewView(ctx);
        _preview.SetImplementationMode(PreviewView.ImplementationMode.Compatible!);
        root.AddView(_preview, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        LinearLayout top = Ui.Column(ctx);
        top.SetBackgroundColor(global::Android.Graphics.Color.Argb(0xCC, 0x11, 0x13, 0x18));
        int pad = Ui.Dp(ctx, 16);
        top.SetPadding(pad, Ui.Dp(ctx, 8), pad, pad);
        top.AddView(Ui.Header(ctx, "Scan QR code", () => Activity.Screens.Pop()));
        _hint = Ui.Text(ctx, "Point the camera at the QR code in the Mac's “Pair new device…” window.");
        top.AddView(_hint);
        _error = Ui.ErrorBanner(ctx);
        top.AddView(_error, Ui.FullWidthLp(ctx));
        _retry = Ui.FilledButton(ctx, "Scan again", Retry);
        _retry.Visibility = ViewStates.Gone;
        top.AddView(_retry, Ui.FullWidthLp(ctx));
        root.AddView(top, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));
        return root;
    }

    protected override void Render(ClientHostState state)
    {
        if (_hint is null)
        {
            return;
        }

        string? error = ErrorFor(state);
        Ui.ShowBanner(_error!, error);
        if (_pairing && state.Status is ClientHostStatus.Pairing or ClientHostStatus.Connecting)
        {
            _hint.Text = "Pairing…";
        }

        _retry!.Visibility = _pairing && error is not null ? ViewStates.Visible : ViewStates.Gone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopCamera();
            _analysis?.ClearAnalyzer();
            _analysisExecutor.Shutdown();
            _scanner.Close();
        }

        base.Dispose(disposing);
    }

    /// <summary>A decoded QR text (UI thread).</summary>
    internal void OnCode(string text)
    {
        if (_disposed || !_resumed || _pairing)
        {
            return;
        }

        if (!PairingQrPayload.TryParse(text, out _))
        {
            _hint!.Text = "That isn't a PenBridge pairing code. Scan the code in the Mac's “Pair new device…” window.";
            return;
        }

        _pairing = true;
        StopCamera();
        MarkAttempt();
        _ = Host.PairWithQrAsync(text); // success: the activity opens the pad; failure: Render shows the error + retry
        Render(Host.State);
    }

    private void Retry()
    {
        _pairing = false;
        _hint!.Text = "Point the camera at the QR code in the Mac's “Pair new device…” window.";
        Ui.ShowBanner(_error!, null);
        _retry!.Visibility = ViewStates.Gone;
        if (_resumed)
        {
            StartCamera();
        }
    }

    private void StartCamera()
    {
        if (_disposed)
        {
            return;
        }

        if (_provider is not null)
        {
            Bind();
            return;
        }

        var future = ProcessCameraProvider.GetInstance(Activity);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _provider = (ProcessCameraProvider)future.Get()!;
            }
            catch (Java.Lang.Exception)
            {
                ShowCameraError();
                return;
            }

            if (_resumed && !_pairing)
            {
                Bind();
            }
        }), Activity.MainExecutor!);
    }

    private void Bind()
    {
        if (_provider is null || _preview is null)
        {
            return;
        }

        try
        {
            _previewUseCase ??= new Preview.Builder().Build()!;
            _previewUseCase.SetSurfaceProvider(Activity.MainExecutor, _preview.SurfaceProvider);
            if (_analysis is null)
            {
                _analysis = new ImageAnalysis.Builder()
                    .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
                    .Build()!;
                _analyzer = new QrAnalyzer(this, _scanner, Activity.MainExecutor!);
                _analysis.SetAnalyzer(_analysisExecutor, _analyzer);
            }

            _analyzer!.Enabled = true;
            _provider.UnbindAll();
            _provider.BindToLifecycle(Activity, CameraSelector.DefaultBackCamera!, _previewUseCase, _analysis);
        }
        catch (Java.Lang.Exception)
        {
            ShowCameraError();
        }
    }

    private void StopCamera()
    {
        if (_analyzer is not null)
        {
            _analyzer.Enabled = false;
        }

        _provider?.UnbindAll();
    }

    private void ShowCameraError()
    {
        Ui.ShowBanner(_error!, "Couldn't open the camera. Use the PIN instead.");
    }

    /// <summary>
    /// Runs on the analysis thread: hands the newest frame to ML Kit (one at a time; KEEP_ONLY_LATEST drops the rest)
    /// and closes the frame when ML Kit is done with it. Decoded text goes to the screen on the main executor.
    /// </summary>
    private sealed class QrAnalyzer(QrScanScreen screen, IBarcodeScanner scanner, IExecutor main) : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        private int _busy;

        public volatile bool Enabled;

        public void Analyze(IImageProxy? image)
        {
            if (image is null)
            {
                return;
            }

            global::Android.Media.Image? media = image.Image;
            if (!Enabled || media is null || Interlocked.Exchange(ref _busy, 1) != 0)
            {
                image.Close();
                return;
            }

            try
            {
                scanner.Process(media, image.ImageInfo?.RotationDegrees ?? 0)!.AddOnCompleteListener(main, new Completion(this, image));
            }
            catch (Java.Lang.Exception)
            {
                image.Close();
                Volatile.Write(ref _busy, 0);
            }
        }

        private void Complete(IImageProxy image, global::Android.Gms.Tasks.Task task)
        {
            image.Close();
            Volatile.Write(ref _busy, 0);
            if (!Enabled || !task.IsSuccessful || AsList(task.Result) is not { } barcodes)
            {
                return;
            }

            for (int i = 0; i < barcodes.Size(); i++)
            {
                if (AsBarcode(barcodes.Get(i)) is { RawValue: { Length: > 0 and <= PairingQrPayload.MaxLength } raw })
                {
                    screen.OnCode(raw);
                    return;
                }
            }
        }

        /// <summary>
        /// The task result is a <c>java.util.List</c>, but its managed peer is a plain <see cref="Java.Lang.Object"/>
        /// (generic Java types have no registered peer), so a type test fails: cast through JNI instead.
        /// </summary>
        private static Java.Util.IList? AsList(Java.Lang.Object? result) =>
            result is null ? null : result as Java.Util.IList ?? result.JavaCast<Java.Util.IList>();

        private static Barcode? AsBarcode(Java.Lang.Object? item) =>
            item is null ? null : item as Barcode ?? item.JavaCast<Barcode>();

        /// <summary>ML Kit completion, delivered on the main executor.</summary>
        private sealed class Completion(QrAnalyzer owner, IImageProxy image) : Java.Lang.Object, IOnCompleteListener
        {
            public void OnComplete(global::Android.Gms.Tasks.Task task) => owner.Complete(image, task);
        }
    }
}
