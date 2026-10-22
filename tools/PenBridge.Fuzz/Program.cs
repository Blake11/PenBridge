using System.Globalization;
using SharpFuzz;

namespace PenBridge.Fuzz;

/// <summary>
/// Entry point. <c>instrument</c> must run in its own process before any other command: it rewrites
/// PenBridge.Core.dll next to this assembly, so nothing here may load PenBridge.Core first. See README.md.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: penbridge-fuzz <command> ...
          instrument <PenBridge.Core.dll>           add SharpFuzz edge coverage to the assembly (in place, once)
          fuzz <target> [--minutes N] [--corpus DIR] [--findings DIR] [--seed N] [--hang-seconds N]
                                                    in-process coverage-guided campaign (works on macOS)
          seeds <target> <DIR>                      write the seed corpus (valid messages from the writers)
          repro <target> <FILE>...                  run inputs once; print OK or the exception
          minimize <target> <FILE>                  shrink a finding while it keeps its signature (FILE.min)
          libfuzzer <target>                        child process for libfuzzer-dotnet (Linux)
          afl <target>                              child process for afl-fuzz (AFL++)
        targets: control, payload, datagram, qr
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 64;
        }

        // libfuzzer-dotnet passes --target_arg as one argument ("libfuzzer control").
        if (args.Length == 1 && args[0].Contains(' ', StringComparison.Ordinal))
        {
            args = args[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        if (args[0] != "instrument")
        {
            Coverage.Install();
        }

        try
        {
            return args[0] switch
            {
                "instrument" when args.Length == 2 => Instrument(args[1]),
                "fuzz" when args.Length >= 2 => Commands.Fuzz(args[1], args[2..]),
                "seeds" when args.Length == 3 => Commands.WriteSeeds(args[1], args[2]),
                "repro" when args.Length >= 3 => Commands.Repro(args[1], args[2..]),
                "minimize" when args.Length == 3 => Commands.Minimize(args[1], args[2]),
                "libfuzzer" when args.Length == 2 => Commands.LibFuzzer(args[1]),
                "afl" when args.Length == 2 => Commands.Afl(args[1]),
                _ => UsageError(),
            };
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 64;
        }
    }

    private static int UsageError()
    {
        Console.Error.WriteLine(Usage);
        return 64;
    }

    /// <summary>Instruments every PenBridge.Core type. Idempotent: an already instrumented assembly is left alone.</summary>
    private static int Instrument(string path)
    {
        try
        {
            int count = Fuzzer.Instrument(path, name => name.StartsWith("PenBridge.Core.", StringComparison.Ordinal), Options.Value).Count();
            Console.WriteLine($"instrumented {count} types in {path}");
        }
        catch (InstrumentationException e) when (e.Message.Contains("already instrumented", StringComparison.Ordinal))
        {
            Console.WriteLine($"already instrumented: {path}");
        }

        return 0;
    }
}

/// <summary>Commands that touch PenBridge.Core (kept out of <see cref="Program"/> so <c>instrument</c> doesn't load it).</summary>
internal static class Commands
{
    public static int Fuzz(string targetName, string[] options)
    {
        FuzzTarget target = Get(targetName);
        RequireInstrumented();
        string root = Path.Combine("artifacts", "fuzz", targetName);
        var settings = new FuzzOptions(
            TimeSpan.FromMinutes(double.Parse(Option(options, "--minutes") ?? "10", CultureInfo.InvariantCulture)),
            Option(options, "--corpus") ?? Path.Combine(root, "corpus"),
            Option(options, "--findings") ?? Path.Combine(root, "findings"),
            int.Parse(Option(options, "--seed") ?? Environment.TickCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(double.Parse(Option(options, "--hang-seconds") ?? "2", CultureInfo.InvariantCulture)));
        return new InProcessFuzzer(target, settings).Run();
    }

    public static int WriteSeeds(string targetName, string dir)
    {
        _ = Get(targetName);
        Directory.CreateDirectory(dir);
        List<byte[]> seeds = Seeds.For(targetName);
        foreach (byte[] seed in seeds)
        {
            _ = InProcessFuzzer.Save(dir, "seed-", seed);
        }

        Console.WriteLine($"wrote {seeds.Count} seeds to {dir}");
        return 0;
    }

    public static int Repro(string targetName, string[] files)
    {
        FuzzTarget target = Get(targetName);
        int failed = 0;
        foreach (string file in files)
        {
            byte[] input = File.ReadAllBytes(file);
            Exception? error = Run(target, input);
            Console.WriteLine(error is null ? $"OK    {file}" : $"FAIL  {file}\n      {InProcessFuzzer.Signature(error)}\n      hex: {Convert.ToHexString(input)}\n{error}");
            failed += error is null ? 0 : 1;
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Removes chunks, then simplifies bytes, keeping each change that preserves the finding's signature.</summary>
    public static int Minimize(string targetName, string file)
    {
        FuzzTarget target = Get(targetName);
        byte[] input = File.ReadAllBytes(file);
        if (Run(target, input) is not { } original)
        {
            Console.Error.WriteLine("input does not fail; nothing to minimize");
            return 1;
        }

        string signature = InProcessFuzzer.Signature(original);
        bool Keeps(byte[] candidate) => Run(target, candidate) is { } e && InProcessFuzzer.Signature(e) == signature;

        for (int chunk = Math.Max(1, input.Length / 2); chunk >= 1; chunk /= 2)
        {
            for (int at = 0; at + chunk <= input.Length;)
            {
                byte[] candidate = [.. input.AsSpan(0, at), .. input.AsSpan(at + chunk)];
                if (Keeps(candidate))
                {
                    input = candidate;
                }
                else
                {
                    at += chunk;
                }
            }
        }

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] != 0)
            {
                byte[] candidate = [.. input];
                candidate[i] = 0;
                if (Keeps(candidate))
                {
                    input = candidate;
                }
            }
        }

        File.WriteAllBytes(file + ".min", input);
        Console.WriteLine($"{signature}\n{input.Length} bytes -> {file}.min\nhex: {Convert.ToHexString(input)}");
        return 0;
    }

    public static int LibFuzzer(string targetName)
    {
        FuzzTarget target = Get(targetName);
        Fuzzer.LibFuzzer.Run(target.Run);
        return 0;
    }

    public static int Afl(string targetName)
    {
        FuzzTarget target = Get(targetName);
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            target.Run(copy.GetBuffer().AsSpan(0, (int)copy.Length));
        });
        return 0;
    }

    private static Exception? Run(FuzzTarget target, byte[] input)
    {
        try
        {
            target.Run(input);
            return null;
        }
#pragma warning disable CA1031 // Any exception escaping the target is the finding.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return e;
        }
    }

    private static FuzzTarget Get(string name) =>
        Targets.Find(name) ?? throw new ArgumentException($"Unknown target '{name}'. Targets: control, payload, datagram, qr.");

    private static string? Option(string[] options, string name)
    {
        int i = Array.IndexOf(options, name);
        return i >= 0 && i + 1 < options.Length ? options[i + 1] : null;
    }

    /// <summary>Without instrumentation the map stays empty and the campaign is blind; refuse to run.</summary>
    private static void RequireInstrumented()
    {
        foreach (System.Reflection.AssemblyName reference in typeof(PenBridge.Core.Protocol.ControlFrame).Assembly.GetReferencedAssemblies())
        {
            if (reference.Name == "SharpFuzz.Common")
            {
                return;
            }
        }

        throw new ArgumentException("PenBridge.Core.dll is not instrumented: run 'penbridge-fuzz instrument <path>/PenBridge.Core.dll' first (or tools/PenBridge.Fuzz/fuzz.sh).");
    }
}
