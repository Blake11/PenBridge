using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Tests.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Net;

/// <summary><see cref="SessionInput"/> settings (ADR-0031..0033): active area and its event, ApplySettings mid-session.</summary>
[Collection(AllocationGroup.Name)]
public sealed class SessionInputSettingsTests : IDisposable
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly RecordingSink _sink = new();
    private readonly List<NormalizedRect> _areas = [];
    private readonly SessionInput _input;
    private readonly byte[] _payload = new byte[InputPayload.MaxSize];
    private uint _timeUs = 1_000;

    public SessionInputSettingsTests()
    {
        _input = new SessionInput(new Lock(), _sink, new FakeTime(), null, Key, 7, new DisplayRect(0, 0, 1920, 1080), NormalizedRect.Full, 16, null, null);
        _input.ActiveAreaChanged += _areas.Add;
    }

    public void Dispose() => _input.Dispose();

    private void Frame(PenSample? pen)
    {
        int n = InputPayload.Write(_payload, _timeUs, pen, [], []);
        _input.ProcessInputFrame(_payload.AsSpan(0, n));
        _timeUs += 5_000;
    }

    private static NormalizedRect Landscape16x9 => CoordinateMapper.ActiveAreaFor(3088, 1440, 1920, 1080);

    [Fact]
    public void Surface_info_raises_the_active_area_every_time()
    {
        Assert.Equal(NormalizedRect.Full, _input.ActiveArea);
        _input.ConfigureSurface(3088, 1440);
        _input.ConfigureSurface(3088, 1440);
        Assert.Equal([Landscape16x9, Landscape16x9], _areas);
        Assert.Equal(Landscape16x9, _input.ActiveArea);
        Assert.True(Landscape16x9.Width < 1);
    }

    [Fact]
    public void Target_changes_raise_only_when_the_area_changes_and_only_after_surface_info()
    {
        _input.SetTarget(new DisplayRect(0, 0, 1000, 1000), NormalizedRect.Full);
        Assert.Empty(_areas);
        _input.ConfigureSurface(2000, 1000);
        _input.SetTarget(new DisplayRect(500, 500, 1000, 1000), NormalizedRect.Full); // same aspect
        Assert.Single(_areas);
        _input.SetTarget(MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 1000, 1000), new DisplayRect(1000, 0, 1000, 1000)]));
        Assert.Equal([new NormalizedRect(0.25, 0, 0.5, 1), NormalizedRect.Full], _areas);
    }

    [Fact]
    public void Mouse_mode_reports_the_full_surface()
    {
        _input.ConfigureSurface(3088, 1440);
        _input.ApplySettings(new InputSettings { StylusMode = StylusMode.Mouse });
        _input.ApplySettings(new InputSettings { StylusMode = StylusMode.Mouse, MouseSpeed = 2 }); // no area change
        _input.ApplySettings(InputSettings.Default);
        Assert.Equal([Landscape16x9, NormalizedRect.Full, Landscape16x9], _areas);
        Assert.Same(InputSettings.Default, _input.Settings);
    }

    [Fact]
    public void Pen_maps_through_the_active_area_with_edges_pinned()
    {
        _input.ConfigureSurface(3088, 1440);
        Frame(Pens.Hover(0, 32768)); // in the left margin
        Assert.Equal(0, _sink.Pen[^1].Position.X, 6);
        Assert.Equal(540, _sink.Pen[^1].Position.Y, 0.1);
    }

    [Fact]
    public void Apply_settings_mid_session_and_mid_stroke()
    {
        _input.ConfigureSurface(3088, 1440, SurfaceCapabilities.FingerFallback, 500, 500);
        Frame(Pens.Contact(20000, 20000));
        _input.ApplySettings(new InputSettings { TipFeel = 1, SideButton = SideButtonAction.MiddleClick });
        Frame(Pens.Contact(21000, 20000));
        Assert.Equal(Drag, _sink.Pen[^1].Kind);
        Assert.Equal(SideButtonAction.RightClick, _sink.Pen[^1].SideButton); // the stroke finishes as it started
        Frame(Pens.Hover(21000, 20000));
        Frame(Pens.Contact(21000, 20000));
        Assert.Equal(Down, _sink.Pen[^1].Kind);
        Assert.Equal(SideButtonAction.MiddleClick, _sink.Pen[^1].SideButton);
        Assert.True(_sink.Pen[^1].Sample.Pressure < Pens.Contact(0, 0).Pressure);
    }

    [Fact]
    public void Invalid_settings_throw_and_closed_sessions_ignore_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _input.ApplySettings(new InputSettings { ScrollSpeed = 0 }));
        _input.Shutdown();
        _input.ApplySettings(new InputSettings { StylusMode = StylusMode.Mouse });
        _input.ConfigureSurface(100, 100);
        Assert.Empty(_areas);
    }

    [Fact]
    public void Input_path_with_settings_applied_is_allocation_free()
    {
        _input.ConfigureSurface(3088, 1440, SurfaceCapabilities.FingerFallback, 500, 500);
        _input.ApplySettings(new InputSettings
        {
            StylusMode = StylusMode.Mouse,
            SideButton = SideButtonAction.Eraser,
            TipFeel = -0.5,
            ClickThreshold = 0.05,
            PointerSpeed = 2,
            ScrollSpeed = 0.5,
        });
        byte[][] frames = new byte[8][];
        int[] lengths = new int[8];
        for (int i = 0; i < 8; i++)
        {
            frames[i] = new byte[InputPayload.MaxSize];
        }

        long ran = 0;
        uint t = 1_000;
        Alloc.AssertZero(4_000, i =>
        {
            ushort x = (ushort)(10_000 + (i % 40 * 50));
            PenSample pen = (i % 4) switch
            {
                0 => Pens.Hover(x, 20000) with { Buttons = PenButtons.Primary },
                1 or 2 => Pens.Contact(x, 20000),
                _ => Pens.Hover(x, 20000),
            };
            int k = i % 8;
            lengths[k] = InputPayload.Write(frames[k], t, pen, [], []);
            _input.ProcessInputFrame(frames[k].AsSpan(0, lengths[k]));
            t += 5_000;
            ran++;
        }, () => ran);
    }
}
