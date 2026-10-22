using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Benchmarks;

/// <summary>Hot input path: serialize, encrypt, decrypt, parse and map one pen sample. Buffers are reused as in production.</summary>
[MemoryDiagnoser]
public class HotPathBenchmarks : IDisposable
{
    private const int PressureCount = 256;
    private const uint SessionId = 0xA1B2C3D4;

    private PenSample _sample = new(
        12345, 54321, 40000, -1500, 2200, 18000, PenTool.Pen, PenState.InRange | PenState.Contact, PenButtons.None);

    private readonly byte[] _sampleBuffer = new byte[InputSampleCodec.PenSampleSize];
    private readonly byte[] _payloadBuffer = new byte[InputPayload.MaxSize];
    private readonly byte[] _datagram = new byte[InputDatagram.MaxSize];
    private byte[] _plainPayload = [];
    private InputCipher _sender = null!;
    private InputCipher _receiver = null!;
    private CoordinateMapper _mapper;
    private PressureCurve _curve;
    private readonly ushort[] _pressures = new ushort[PressureCount];
    private ulong _sequence;

    [GlobalSetup]
    public void Setup()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)i;
        }

        _sender = new InputCipher(key, SessionId);
        _receiver = new InputCipher(key, SessionId);
        _mapper = new CoordinateMapper(1000, 2000, new DisplayRect(0, 0, 2560, 1440));
        for (int i = 0; i < _pressures.Length; i++)
        {
            _pressures[i] = (ushort)(i * 257);
        }

        _curve = new PressureCurve(0.3, 0.05, 0.9);

        var payload = new InputPayload { TimeUs = 1_000_000, Pen = _sample };
        int length = payload.Write(_payloadBuffer);
        _plainPayload = _payloadBuffer.AsSpan(0, length).ToArray();
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sender.Dispose();
        _receiver.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public byte SerializePenSample()
    {
        _sample = _sample with { X = (ushort)(_sample.X + 1) };
        InputSampleCodec.WritePen(_sampleBuffer, in _sample);
        return _sampleBuffer[1];
    }

    [Benchmark]
    public int ParsePenSample()
    {
        InputSampleCodec.WritePen(_sampleBuffer, in _sample);
        return InputSampleCodec.TryReadPen(_sampleBuffer, out PenSample parsed) ? parsed.X + parsed.Pressure : -1;
    }

    [Benchmark]
    public int SerializePayload()
    {
        var payload = new InputPayload { TimeUs = 1_000_000, Pen = _sample };
        return payload.Write(_payloadBuffer);
    }

    [Benchmark]
    public int ParsePayload() =>
        InputPayload.TryRead(_plainPayload, out InputPayload parsed) && parsed.Pen is { } pen ? pen.X + pen.Pressure : -1;

    [Benchmark]
    public int Seal() => SealNext();

    /// <summary>Seal plus open of one datagram; subtract <see cref="Seal"/> for the decrypt cost.</summary>
    [Benchmark]
    public bool SealAndOpen()
    {
        int total = SealNext();
        return _receiver.TryOpen(_datagram.AsSpan(0, total), out _);
    }

    /// <summary>Full sample round trip: serialize, header, encrypt, decrypt, parse.</summary>
    [Benchmark]
    public bool FullRoundTrip()
    {
        var payload = new InputPayload { TimeUs = 1_000_000, Pen = _sample };
        int length = payload.Write(_datagram.AsSpan(InputCipher.HeaderLength));
        WriteHeader();
        int total = _sender.Seal(_datagram, length);
        return _receiver.TryOpen(_datagram.AsSpan(0, total), out int plain)
            && InputPayload.TryRead(_datagram.AsSpan(InputCipher.HeaderLength, plain), out InputPayload parsed)
            && parsed.Pen is not null;
    }

    [Benchmark]
    public double MapPoint()
    {
        DisplayPoint p = _mapper.Map(_sample.X, _sample.Y);
        return p.X + p.Y;
    }

    [Benchmark(OperationsPerInvoke = PressureCount)]
    public int ApplyPressureCurve()
    {
        int sum = 0;
        foreach (ushort pressure in _pressures)
        {
            if (_curve.TryApply(pressure, inContact: true, out ushort output))
            {
                sum += output;
            }
        }

        return sum;
    }

    private int SealNext()
    {
        _plainPayload.CopyTo(_datagram.AsSpan(InputCipher.HeaderLength));
        WriteHeader();
        return _sender.Seal(_datagram, _plainPayload.Length);
    }

    private void WriteHeader()
    {
        new InputDatagramHeader(InputDatagramOptions.None, SessionId, ++_sequence).Write(_datagram);
    }
}
