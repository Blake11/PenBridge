using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FuzzTrace = SharpFuzz.Common.Trace;

namespace PenBridge.Fuzz;

/// <summary>Settings of one in-process campaign.</summary>
internal sealed record FuzzOptions(TimeSpan Duration, string CorpusDir, string FindingsDir, int Seed, TimeSpan HangTimeout);

/// <summary>
/// A small AFL-style coverage-guided fuzzer that runs in this process. The edge coverage comes from SharpFuzz's
/// instrumentation of PenBridge.Core (<c>Trace.SharedMem[cur ^ prev]++</c>), which writes into a 64 KiB map owned
/// here instead of an AFL/libFuzzer shared-memory segment. An input that lights up a new edge or a new hit-count
/// bucket joins the corpus; inputs are mutated with AFL's havoc operators, a dictionary, splicing and
/// length-prefix fixing. Any exception escaping the target is a finding; an execution longer than the hang
/// timeout is a hang (the input is saved and the process exits).
/// </summary>
internal sealed unsafe class InProcessFuzzer
{
    public const int MapSize = 1 << 16;

    private static readonly byte[] CountClass = BuildCountClasses();
    private static readonly sbyte[] Interesting8 = [-128, -1, 0, 1, 16, 32, 64, 100, 127];
    private static readonly short[] Interesting16 = [-32768, -129, 128, 255, 256, 512, 1000, 1024, 4096, 32767, -1, 0, 1, 2, 3];
    private static readonly int[] LengthWidths = [1, 2, 4];
    private static readonly int[] Interesting32 = [int.MinValue, -100663046, -32769, 32768, 65535, 65536, 65537, 100663045, int.MaxValue, -1, 0, 1];

    private readonly FuzzTarget _target;
    private readonly FuzzOptions _options;
    private readonly Random _random;
    private readonly byte* _map;
    private readonly byte[] _virgin = new byte[MapSize];
    private readonly List<byte[]> _corpus = [];
    private readonly List<byte[]> _dictionary;
    private readonly Dictionary<string, int> _findings = [];
    private readonly byte[] _work;
    private long _execs;
    private long _execStart;
    private int _workLength;

    public InProcessFuzzer(FuzzTarget target, FuzzOptions options)
    {
        _target = target;
        _options = options;
        _random = new Random(options.Seed);
        _map = Coverage.Map;
        _dictionary = Seeds.Dictionary(target.Name);
        _work = new byte[target.MaxLength];
    }

    public int Edges { get; private set; }

    public int Run()
    {
        Directory.CreateDirectory(_options.CorpusDir);
        Directory.CreateDirectory(_options.FindingsDir);
        List<byte[]> initial = [.. Directory.EnumerateFiles(_options.CorpusDir).Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
        if (initial.Count == 0)
        {
            foreach (byte[] seed in Seeds.For(_target.Name))
            {
                Save(_options.CorpusDir, "seed-", seed);
                initial.Add(seed);
            }
        }

        var watchdog = new Thread(Watchdog) { IsBackground = true, Name = "fuzz-watchdog" };
        watchdog.Start();

        foreach (byte[] input in initial)
        {
            int len = Math.Min(input.Length, _work.Length);
            input.AsSpan(0, len).CopyTo(_work);
            _workLength = len;
            if (Execute() is { } error)
            {
                Report(error, input.AsSpan(0, len));
            }

            // Keep every starting entry (even with no new edge) so a resumed corpus isn't lost.
            _ = Classify();
            _corpus.Add(input[..len]);
        }

        int seedEdges = Edges;
        int seedCorpus = _corpus.Count;
        Console.WriteLine($"[{_target.Name}] start: corpus {seedCorpus}, edges {seedEdges}, seed {_options.Seed}, {_options.Duration.TotalMinutes:0.#} min");

        var clock = Stopwatch.StartNew();
        TimeSpan nextStatus = TimeSpan.FromSeconds(30);
        long execsAtStart = _execs;
        while (clock.Elapsed < _options.Duration)
        {
            byte[] parent = PickParent();
            int len = Math.Min(parent.Length, _work.Length);
            parent.AsSpan(0, len).CopyTo(_work);
            _workLength = len;
            Mutate();
            ReadOnlySpan<byte> input = _work.AsSpan(0, _workLength);
            if (Execute() is { } error)
            {
                _ = Classify();
                Report(error, input);
                continue;
            }

            if (Classify())
            {
                byte[] copy = input.ToArray();
                _corpus.Add(copy);
                Save(_options.CorpusDir, string.Empty, copy);
            }

            if (clock.Elapsed >= nextStatus)
            {
                Status(clock.Elapsed, execsAtStart, seedEdges, seedCorpus);
                nextStatus += TimeSpan.FromSeconds(30);
            }
        }

        Console.WriteLine($"[{_target.Name}] done: {_execs - execsAtStart} execs in {clock.Elapsed.TotalSeconds:0}s, {(_execs - execsAtStart) / clock.Elapsed.TotalSeconds:0} exec/s, corpus {seedCorpus} -> {_corpus.Count}, edges {seedEdges} -> {Edges}, findings {_findings.Count}");
        foreach ((string signature, int count) in _findings)
        {
            Console.WriteLine($"  finding x{count}: {signature}");
        }

        return _findings.Count == 0 ? 0 : 1;
    }

    /// <summary>Runs the target once on the work buffer; returns the escaping exception, if any.</summary>
    private Exception? Execute()
    {
        FuzzTrace.PrevLocation = 0;
        Volatile.Write(ref _execStart, Stopwatch.GetTimestamp());
        try
        {
            _target.Run(_work.AsSpan(0, _workLength));
            return null;
        }
#pragma warning disable CA1031 // Any exception escaping the target is the finding.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return e;
        }
        finally
        {
            Volatile.Write(ref _execStart, 0);
            _execs++;
        }
    }

    /// <summary>Folds the trace map into the virgin map; true when it holds a new edge or hit-count bucket. Clears the map.</summary>
    private bool Classify()
    {
        bool interesting = false;
        ulong* words = (ulong*)_map;
        for (int w = 0; w < MapSize / 8; w++)
        {
            if (words[w] == 0)
            {
                continue;
            }

            for (int i = w * 8; i < (w * 8) + 8; i++)
            {
                byte hits = _map[i];
                if (hits == 0)
                {
                    continue;
                }

                byte bucket = CountClass[hits];
                if ((_virgin[i] & bucket) != bucket)
                {
                    if (_virgin[i] == 0)
                    {
                        Edges++;
                    }

                    _virgin[i] |= bucket;
                    interesting = true;
                }
            }

            words[w] = 0;
        }

        return interesting;
    }

    private byte[] PickParent()
    {
        // Favour recent finds (they sit at the coverage frontier), but keep visiting the whole corpus.
        int count = _corpus.Count;
        return _random.Next(2) == 0 && count > 16
            ? _corpus[count - 1 - _random.Next(16)]
            : _corpus[_random.Next(count)];
    }

    private void Mutate()
    {
        int stack = 1 << _random.Next(1, 5);
        for (int s = 0; s < stack; s++)
        {
            MutateOnce();
        }
    }

    private void MutateOnce()
    {
        Span<byte> buf = _work;
        int len = _workLength;
        switch (_random.Next(len == 0 ? 8 : 16))
        {
            case 0 or 1:
                Insert(RandomChunk(), _random.Next(len + 1));
                return;
            case 2:
                InsertToken(insert: true);
                return;
            case 3:
                Splice();
                return;
            case 4:
                if (len < buf.Length)
                {
                    Insert([(byte)_random.Next(256)], _random.Next(len + 1));
                }

                return;
            case 5:
                InsertToken(insert: false);
                return;
            case 6:
                DuplicateChunk();
                return;
            case 7:
                FixLengthPrefix();
                return;
            case 8:
                buf[_random.Next(len)] ^= (byte)(1 << _random.Next(8));
                return;
            case 9:
                buf[_random.Next(len)] = (byte)Interesting8[_random.Next(Interesting8.Length)];
                return;
            case 10 when len >= 2:
                WriteBigEndian(buf, _random.Next(len - 1), Interesting16[_random.Next(Interesting16.Length)], 2);
                return;
            case 11 when len >= 4:
                WriteBigEndian(buf, _random.Next(len - 3), Interesting32[_random.Next(Interesting32.Length)], 4);
                return;
            case 12:
                buf[_random.Next(len)] += (byte)(_random.Next(1, 36) * (_random.Next(2) == 0 ? 1 : -1));
                return;
            case 13:
                buf[_random.Next(len)] = (byte)_random.Next(256);
                return;
            case 14:
                Delete();
                return;
            default:
                OverwriteFromSelf();
                return;
        }
    }

    private byte[] RandomChunk()
    {
        var chunk = new byte[_random.Next(1, 17)];
        if (_random.Next(2) == 0)
        {
            _random.NextBytes(chunk);
        }
        else
        {
            chunk.AsSpan().Fill((byte)Interesting8[_random.Next(Interesting8.Length)]);
        }

        return chunk;
    }

    private void Insert(ReadOnlySpan<byte> chunk, int at)
    {
        int room = _work.Length - _workLength;
        if (room <= 0)
        {
            return;
        }

        chunk = chunk[..Math.Min(chunk.Length, room)];
        _work.AsSpan(at, _workLength - at).CopyTo(_work.AsSpan(at + chunk.Length));
        chunk.CopyTo(_work.AsSpan(at));
        _workLength += chunk.Length;
    }

    private void InsertToken(bool insert)
    {
        byte[] token = _dictionary[_random.Next(_dictionary.Count)];
        if (insert || _workLength < token.Length)
        {
            Insert(token, _random.Next(_workLength + 1));
        }
        else
        {
            token.CopyTo(_work.AsSpan(_random.Next(_workLength - token.Length + 1)));
        }
    }

    private void Splice()
    {
        byte[] other = _corpus[_random.Next(_corpus.Count)];
        if (other.Length == 0)
        {
            return;
        }

        int cut = _random.Next(_workLength + 1);
        int from = _random.Next(other.Length);
        int take = Math.Min(other.Length - from, _work.Length - cut);
        other.AsSpan(from, take).CopyTo(_work.AsSpan(cut));
        _workLength = cut + take;
    }

    private void DuplicateChunk()
    {
        if (_workLength == 0)
        {
            return;
        }

        int from = _random.Next(_workLength);
        int size = _random.Next(1, Math.Min(_workLength - from, 64) + 1);
        Insert(_work.AsSpan(from, size).ToArray(), _random.Next(_workLength + 1));
    }

    private void Delete()
    {
        int from = _random.Next(_workLength);
        int size = _random.Next(1, Math.Min(_workLength - from, 32) + 1);
        _work.AsSpan(from + size, _workLength - from - size).CopyTo(_work.AsSpan(from));
        _workLength -= size;
    }

    private void OverwriteFromSelf()
    {
        int size = _random.Next(1, Math.Min(_workLength, 32) + 1);
        int from = _random.Next(_workLength - size + 1);
        int to = _random.Next(_workLength - size + 1);
        _work.AsSpan(from, size).CopyTo(_work.AsSpan(to));
    }

    /// <summary>Writes a u8/u16/u32 big-endian length at a random offset that matches the bytes after it (± a little).</summary>
    private void FixLengthPrefix()
    {
        int width = LengthWidths[_random.Next(LengthWidths.Length)];
        if (_workLength < width)
        {
            return;
        }

        int at = _random.Next(_workLength - width + 1);
        int value = _workLength - at - width + _random.Next(-2, 3);
        WriteBigEndian(_work, at, Math.Max(0, value), width);
    }

    private static void WriteBigEndian(Span<byte> buf, int at, long value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            buf[at + i] = (byte)value;
            value >>= 8;
        }
    }

    private void Report(Exception error, ReadOnlySpan<byte> input)
    {
        string signature = Signature(error);
        if (_findings.TryGetValue(signature, out int count))
        {
            _findings[signature] = count + 1;
            return;
        }

        _findings[signature] = 1;
        string path = Save(_options.FindingsDir, "crash-", input.ToArray());
        Console.WriteLine($"[{_target.Name}] FINDING {signature}\n  input: {path}\n{error}");
    }

    /// <summary>Exception type, message for contract violations, and the first frame inside PenBridge.</summary>
    internal static string Signature(Exception error)
    {
        if (error is ContractViolationException)
        {
            return $"{error.GetType().Name}: {error.Message}";
        }

        string frame = error.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("PenBridge.Core", StringComparison.Ordinal))
            ?? error.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "?";
        int paren = frame.IndexOf(" in ", StringComparison.Ordinal);
        frame = paren > 0 ? frame[..paren] : frame;
        return $"{error.GetType().Name} @ {frame}";
    }

    private void Status(TimeSpan elapsed, long execsAtStart, int seedEdges, int seedCorpus)
    {
        long execs = _execs - execsAtStart;
        Console.WriteLine($"[{_target.Name}] {elapsed.TotalSeconds,5:0}s execs {execs,10} ({execs / Math.Max(elapsed.TotalSeconds, 1e-3),7:0}/s) corpus {_corpus.Count,5} (+{_corpus.Count - seedCorpus}) edges {Edges,5} (seeds {seedEdges}) findings {_findings.Count}");
    }

    private void Watchdog()
    {
        while (true)
        {
            Thread.Sleep(250);
            long start = Volatile.Read(ref _execStart);
            if (start != 0 && Stopwatch.GetElapsedTime(start) > _options.HangTimeout)
            {
                string path = Save(_options.FindingsDir, "hang-", _work.AsSpan(0, _workLength).ToArray());
                Console.WriteLine($"[{_target.Name}] FINDING hang (> {_options.HangTimeout.TotalSeconds:0}s in one execution)\n  input: {path}");
                Environment.Exit(2);
            }
        }
    }

    internal static string Save(string dir, string prefix, byte[] data)
    {
        string path = Path.Combine(dir, prefix + Convert.ToHexStringLower(SHA256.HashData(data).AsSpan(0, 10)));
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] BuildCountClasses()
    {
        var classes = new byte[256];
        for (int i = 1; i < 256; i++)
        {
            classes[i] = i switch
            {
                1 => 1,
                2 => 2,
                3 => 4,
                <= 7 => 8,
                <= 15 => 16,
                <= 31 => 32,
                <= 127 => 64,
                _ => 128,
            };
        }

        return classes;
    }
}

/// <summary>
/// The process-wide trace map. Instrumented PenBridge.Core code writes to it on every branch, including static
/// constructors, so it must be installed before any Core code runs (an instrumented assembly with no map crashes).
/// </summary>
internal static unsafe class Coverage
{
    public static byte* Map { get; private set; }

    public static void Install()
    {
        if (Map == null)
        {
            Map = (byte*)NativeMemory.AllocZeroed(InProcessFuzzer.MapSize);
            FuzzTrace.SharedMem = Map;
        }
    }
}
