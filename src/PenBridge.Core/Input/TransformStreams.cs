namespace PenBridge.Core.Input;

/// <summary>
/// What a head posts for one step of a two-finger transform: per stream, whether it begins now (post Began with
/// value 0 first) and the value to post as Changed. Streams that begin in the same step post in
/// <see cref="RotateFirst"/> order.
/// </summary>
/// <param name="MagnifyBegins">The magnify stream opens with this step.</param>
/// <param name="PostMagnify">Post <paramref name="Magnification"/> as a magnify Changed.</param>
/// <param name="Magnification">Additive magnification to post (trackpad convention).</param>
/// <param name="RotateBegins">The rotate stream opens with this step.</param>
/// <param name="PostRotate">Post <paramref name="Degrees"/> as a rotate Changed.</param>
/// <param name="Degrees">Counterclockwise degrees to post.</param>
/// <param name="RotateFirst">Post the rotate events before the magnify events (rotate opened as the dominant stream).</param>
public readonly record struct TransformStreamStep(
    bool MagnifyBegins,
    bool PostMagnify,
    double Magnification,
    bool RotateBegins,
    bool PostRotate,
    double Degrees,
    bool RotateFirst);

/// <summary>
/// Splits a two-finger transform (<see cref="GestureKind.Pinch"/>) into the magnify and rotate streams a host posts,
/// deciding which stream opens first by dominance. Apps such as Preview follow only the stream that began first, and a
/// natural twist also changes the finger spread a little, so the first stream must be the one the fingers mostly do.
/// </summary>
/// <remarks>
/// <para><b>Rule.</b> Both components are held back until either reaches <see cref="StartThreshold"/> in normalised
/// units: the magnification (relative spread change) and the rotation in radians, which are each finger's radial and
/// tangential travel divided by half the spread, so they compare like for like. The larger one then opens its stream
/// (with everything held back as its first Changed); a tie goes to rotate. Because the recognizer's Began already
/// carries the whole movement since landing, the first step usually decides.</para>
/// <para><b>Second stream.</b> The other stream accumulates from the moment the first one opened (its amount up to
/// then is dropped) and opens later in the same gesture only once that reaches <see cref="MagnifyJoinThreshold"/> or
/// <see cref="RotateJoinDegrees"/>, well above the drift of a deliberate pinch or twist. It then starts from that
/// point (the held drift is dropped, so it does not jump). Apps that combine both (Maps, Krita) still zoom while
/// rotating on a deliberate combined gesture; Preview keeps following the first stream, as with a trackpad.</para>
/// <para>A component that is zero throughout (its Zoom/Rotate toggle off, ADR-0033) never opens. Allocation-free;
/// not thread-safe (the head's lock guards it).</para>
/// </remarks>
public sealed class TransformStreams
{
    /// <summary>Normalised amount (magnification, or rotation in radians) at which the first stream opens.</summary>
    public const double StartThreshold = 0.02;

    /// <summary>Magnification accumulated since rotate opened first at which magnify opens too.</summary>
    public const double MagnifyJoinThreshold = 0.25;

    /// <summary>Rotation (degrees) accumulated since magnify opened first at which rotate opens too.</summary>
    public const double RotateJoinDegrees = 15;

    private const double RadiansPerDegree = Math.PI / 180;

    private double _heldMagnification;
    private double _heldDegrees;

    /// <summary>The magnify stream is open (its Began was returned).</summary>
    public bool MagnifyOpen { get; private set; }

    /// <summary>The rotate stream is open (its Began was returned).</summary>
    public bool RotateOpen { get; private set; }

    /// <summary>Forgets the gesture: call when it ends or is cancelled (after closing the open streams).</summary>
    public void Reset()
    {
        MagnifyOpen = false;
        RotateOpen = false;
        _heldMagnification = 0;
        _heldDegrees = 0;
    }

    /// <summary>
    /// One transform step: <paramref name="magnification"/> from <see cref="GestureOutput.ToMagnification"/>,
    /// <paramref name="degrees"/> from <see cref="GestureOutput.ToCounterClockwiseDegrees"/>. Non-finite values count
    /// as 0.
    /// </summary>
    public TransformStreamStep Step(double magnification, double degrees)
    {
        if (!double.IsFinite(magnification))
        {
            magnification = 0;
        }

        if (!double.IsFinite(degrees))
        {
            degrees = 0;
        }

        bool magnifyBegins = false;
        bool rotateBegins = false;
        bool postMagnify = false;
        bool postRotate = false;
        double magnifyValue = 0;
        double rotateValue = 0;
        bool rotateFirst = false;

        if (!MagnifyOpen && !RotateOpen)
        {
            _heldMagnification += magnification;
            _heldDegrees += degrees;
            double m = Math.Abs(_heldMagnification);
            double r = Math.Abs(_heldDegrees) * RadiansPerDegree;
            if (Math.Max(m, r) < StartThreshold)
            {
                return default;
            }

            if (r >= m)
            {
                rotateFirst = true;
                RotateOpen = rotateBegins = postRotate = true;
                rotateValue = _heldDegrees;
                _heldDegrees = 0;
                _heldMagnification = 0; // the join counts from here: drop the drift that came with the decision
                magnification = 0;
            }
            else
            {
                MagnifyOpen = magnifyBegins = postMagnify = true;
                magnifyValue = _heldMagnification;
                _heldMagnification = 0;
                _heldDegrees = 0;
                degrees = 0;
            }
        }
        else
        {
            if (MagnifyOpen && magnification != 0)
            {
                postMagnify = true;
                magnifyValue = magnification;
                magnification = 0;
            }

            if (RotateOpen && degrees != 0)
            {
                postRotate = true;
                rotateValue = degrees;
                degrees = 0;
            }
        }

        // The second stream: accumulate since the first opened, open past its join threshold from this point on.
        if (!MagnifyOpen)
        {
            _heldMagnification += magnification;
            if (Math.Abs(_heldMagnification) >= MagnifyJoinThreshold)
            {
                MagnifyOpen = magnifyBegins = true;
                _heldMagnification = 0;
            }
        }
        else if (!RotateOpen)
        {
            _heldDegrees += degrees;
            if (Math.Abs(_heldDegrees) >= RotateJoinDegrees)
            {
                RotateOpen = rotateBegins = true;
                _heldDegrees = 0;
            }
        }

        return new TransformStreamStep(
            magnifyBegins, postMagnify, magnifyValue, rotateBegins, postRotate, rotateValue, rotateFirst);
    }
}
