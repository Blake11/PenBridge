using PenBridge.Core.Input;

namespace PenBridge.Android.Pad;

/// <summary>
/// Hands input frames from the UI thread to a dedicated sender thread that calls <see cref="IPadOutput.SendState"/>,
/// so the network never runs on the UI thread. Preallocated ring of frames; allocation-free per frame.
/// </summary>
/// <remarks>
/// Every frame is the complete current state, so when the ring is full the newest slot is overwritten (latest state
/// wins; only that slot's batched history is lost). Frames enqueued while stopped are dropped. <see cref="Stop"/>
/// drains what is queued before the thread exits.
/// <para>Single sender: each <see cref="Start"/> begins a new generation, and a thread only dequeues and sends while
/// holding the send gate with its own generation current. If <see cref="Stop"/> times out waiting for a thread stuck
/// in SendState, that thread exits at its next gate check, so two threads never call SendState concurrently.</para>
/// </remarks>
internal sealed class PadSendQueue : IDisposable
{
    /// <summary>History samples per frame (the datagram limit).</summary>
    public const int MaxHistory = 8;

    private const int Capacity = 64;

    private readonly IPadOutput _output;
    private readonly Lock _lock = new();
    private readonly Lock _sendGate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly PenSample[] _pens = new PenSample[Capacity];
    private readonly bool[] _hasPen = new bool[Capacity];
    private readonly TouchSample[] _touches = new TouchSample[Capacity * InputFilter.MaxTouches];
    private readonly int[] _touchCounts = new int[Capacity];
    private readonly PenSample[] _history = new PenSample[Capacity * MaxHistory];
    private readonly int[] _historyCounts = new int[Capacity];

    // Sender-thread copies, filled under the lock and sent outside it.
    private readonly TouchSample[] _sendTouches = new TouchSample[InputFilter.MaxTouches];
    private readonly PenSample[] _sendHistory = new PenSample[MaxHistory];

    private int _generation;
    private int _head;
    private int _count;
    private volatile bool _running;
    private Thread? _thread;
    private long _sendErrors;

    public PadSendQueue(IPadOutput output)
    {
        _output = output;
    }

    /// <summary>SendState calls that threw (bug indicator; never logged per frame).</summary>
    public long SendErrors => Interlocked.Read(ref _sendErrors);

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        int generation = Interlocked.Increment(ref _generation);
        _running = true;
        _thread = new Thread(() => Run(generation))
        {
            IsBackground = true,
            Name = "PenBridge.PadSender",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Stops the sender after it sent everything already queued (bounded wait).</summary>
    public void Stop()
    {
        if (_thread is not { } thread)
        {
            return;
        }

        _running = false;
        _signal.Set();
        thread.Join(TimeSpan.FromSeconds(1));
        Interlocked.Increment(ref _generation); // a thread still stuck in SendState retires at its next gate check
        _thread = null;
        lock (_lock)
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Queues one frame. Called on the UI thread; never blocks on the network.</summary>
    public void Enqueue(in PenSample pen, bool hasPen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        if (!_running)
        {
            return;
        }

        if (touches.Length > InputFilter.MaxTouches)
        {
            touches = touches[..InputFilter.MaxTouches];
        }

        if (history.Length > MaxHistory)
        {
            history = history[^MaxHistory..];
        }

        lock (_lock)
        {
            int slot;
            if (_count == Capacity)
            {
                slot = (_head + _count - 1) % Capacity;
            }
            else
            {
                slot = (_head + _count) % Capacity;
                _count++;
            }

            _pens[slot] = pen;
            _hasPen[slot] = hasPen;
            touches.CopyTo(_touches.AsSpan(slot * InputFilter.MaxTouches, InputFilter.MaxTouches));
            _touchCounts[slot] = touches.Length;
            history.CopyTo(_history.AsSpan(slot * MaxHistory, MaxHistory));
            _historyCounts[slot] = history.Length;
        }

        _signal.Set();
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    private void Run(int generation)
    {
        while (true)
        {
            _signal.WaitOne();
            while (true)
            {
                lock (_sendGate)
                {
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        _signal.Set(); // pass on a wake-up this stale thread may have taken from the current one
                        return;
                    }

                    if (!TryDequeue(out PenSample pen, out bool hasPen, out int touchCount, out int historyCount))
                    {
                        break;
                    }

                    PenSample? p = hasPen ? pen : null;
                    try
                    {
                        _output.SendState(p, _sendTouches.AsSpan(0, touchCount), _sendHistory.AsSpan(0, historyCount));
                    }
#pragma warning disable CA1031 // The sender thread must survive a faulty output; failures are counted, not logged per frame.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        Interlocked.Increment(ref _sendErrors);
                    }
                }
            }

            if (!_running)
            {
                return;
            }
        }
    }

    private bool TryDequeue(out PenSample pen, out bool hasPen, out int touchCount, out int historyCount)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                pen = default;
                hasPen = false;
                touchCount = 0;
                historyCount = 0;
                return false;
            }

            int slot = _head;
            _head = (_head + 1) % Capacity;
            _count--;
            pen = _pens[slot];
            hasPen = _hasPen[slot];
            touchCount = _touchCounts[slot];
            historyCount = _historyCounts[slot];
            _touches.AsSpan(slot * InputFilter.MaxTouches, touchCount).CopyTo(_sendTouches);
            _history.AsSpan(slot * MaxHistory, historyCount).CopyTo(_sendHistory);
            return true;
        }
    }
}
