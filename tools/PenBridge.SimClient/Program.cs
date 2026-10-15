using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PenBridge.Core;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.SimClient;

/// <summary>
/// A fake phone for driving the Mac server without a device. Its identity (device id and per-server keys) lives in
/// <c>~/.penbridge/simclient/</c>. With <c>--in-process-server</c> it also hosts a <see cref="PenBridgeServer"/> that
/// prints the events it would inject.
/// </summary>
internal static class Program
{
    private const string LastServerKey = "simclient.last-server";

    private const string Usage = """
        PenBridge SimClient: a fake phone for the PenBridge Mac server.

        Usage:
          penbridge-sim pair --host <host> [--port <port>] --pin <pin>   Pair with the PIN shown on the Mac
          penbridge-sim pair --qr "<penbridge://pair?...>"                Pair with a QR payload
          penbridge-sim draw <script> [--host <host>] [--port <port>] [--repeat <n>]
                                                                          Reconnect to the last paired Mac and draw
          penbridge-sim serve [--port <port>]                             Run a server here that prints its input
          penbridge-sim scripts                                           List the draw scripts

        Options:
          --in-process-server   pair/draw against a server started in this process (no Mac app needed)
          --finger-touches      the in-process/serve server offers FingerTouches (ADR-0026: one finger is a
                                relative cursor; try the one-finger script)
          --verbose             debug logging

        PENBRIDGE_LATENCY_LOG=1 turns on the in-process/serve server's latency capture (dumped as
        latency-sim-*.tsv next to the Mac app's dumps; scripts/latency-report.sh prints them). Pings run every
        2 s, so draw for a few seconds (--repeat) to get a clock-offset estimate.

        Scripts: line, circle, pressure-ramp, tilt-sweep, hover, button-click, two-finger-scroll (ends in a
                 flick), pinch, rotate, two-finger-tap (right-click), smart-zoom (two-finger double tap),
                 three-finger-drag, swipe-up, swipe-down, swipe-left, swipe-right, four-finger-pinch-in,
                 four-finger-pinch-out, page-swipe-left, page-swipe-right (two-finger horizontal flick:
                 back/forward), one-finger (relative cursor: move, click, double click, tap-and-a-half drag;
                 needs a server offering FingerTouches), packet-loss (a circle with 20% of datagrams dropped)
        """;

    private static readonly SurfaceInfoMessage Surface = new(1440, 3088, 500f, 500f, SurfaceOrientation.Portrait, SurfaceCapabilities.FingerFallback);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var parsed = new Args(args);
        if (parsed.Command is null or "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return parsed.Command is null ? 1 : 0;
        }

        using var loggers = new ConsoleLoggerFactory(parsed.Has("--verbose") ? LogLevel.Debug : LogLevel.Information);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return parsed.Command switch
            {
                "pair" => await PairAsync(parsed, loggers, cts.Token),
                "draw" => await DrawAsync(parsed, loggers, cts.Token),
                "serve" => await ServeAsync(parsed, loggers, cts.Token),
                "scripts" => ListScripts(),
                _ => Fail("Unknown command '" + parsed.Command + "'.\n\n" + Usage),
            };
        }
        catch (PenBridgeConnectException ex)
        {
            string detail = ex.Failure == ConnectFailure.PairingFailed ? $" ({ex.AttemptsLeft} attempts left)" : string.Empty;
            return Fail($"{ex.Failure}: {ex.Message}{detail}");
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int ListScripts()
    {
        foreach (string name in DrawScripts.Names)
        {
            Console.WriteLine(name);
        }

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static FileSecretStore OpenStore() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".penbridge", "simclient"));

    private static PenBridgeClient CreateClient(ISecretStore store, ILoggerFactory loggers, DatagramFilter? filter = null) =>
        new(new PenBridgeClientOptions
        {
            SecretStore = store,
            Surface = Surface,
            DeviceName = "PenBridge SimClient",
            DatagramFilter = filter,
            LoggerFactory = loggers,
        });

    private static async Task<int> PairAsync(Args args, ILoggerFactory loggers, CancellationToken ct)
    {
        if (args.Has("--in-process-server"))
        {
            await using InProcessServer local = InProcessServer.Start(loggers, args.Port, fingerTouches: args.Has("--finger-touches"));
            await using PenBridgeClient demo = CreateClient(new InMemorySecretStore(), loggers);
            string pin = local.Server.CurrentPin!;
            Console.WriteLine($"In-process server on port {local.Server.Port}, PIN {pin}");
            await demo.PairWithPinAsync("127.0.0.1", local.Server.Port, pin, ct);
            Console.WriteLine($"Paired and authenticated with '{PeerText.Sanitize(demo.ServerName)}' over {demo.NegotiatedTlsProtocol}.");
            await demo.DisconnectAsync();
            return 0;
        }

        FileSecretStore store = OpenStore();
        await using PenBridgeClient client = CreateClient(store, loggers);
        string host;
        int port;
        if (args.Value("--qr") is { } qrText)
        {
            if (!PairingQrPayload.TryParse(qrText, out PairingQrPayload? qr))
            {
                return Fail("Not a valid PenBridge QR payload.");
            }

            host = qr!.Host;
            port = qr.Port;
            await client.PairWithQrAsync(qr, ct);
        }
        else
        {
            host = args.Value("--host") ?? throw new ArgumentException("pair needs --host and --pin, or --qr.");
            port = args.Port ?? ProtocolInfo.DefaultPort;
            string pin = args.Value("--pin") ?? throw new ArgumentException("pair needs --pin.");
            await client.PairWithPinAsync(host, port, pin, ct);
        }

        byte[] fingerprint = client.ServerFingerprint!;
        store.Set(LastServerKey, Encoding.UTF8.GetBytes($"{host}\n{port.ToString(CultureInfo.InvariantCulture)}\n{CertificateFingerprint.ToBase64Url(fingerprint)}"));
        Console.WriteLine($"Paired with '{PeerText.Sanitize(client.ServerName)}' ({host}:{port}, fp {CertificateFingerprint.ToBase64Url(fingerprint)}).");
        await client.DisconnectAsync();
        return 0;
    }

    private static async Task<int> DrawAsync(Args args, ILoggerFactory loggers, CancellationToken ct)
    {
        string? script = args.Positional;
        if (script is null || !DrawScripts.TryGet(script, out IEnumerable<ScriptFrame> frames))
        {
            return Fail("draw needs a script: " + string.Join(", ", DrawScripts.Names));
        }

        int repeat = args.Value("--repeat") is { } r
            ? int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and <= 10_000
                ? n
                : throw new ArgumentException("--repeat must be 1-10000.")
            : 1;
        DatagramFilter? filter = script == DrawScripts.PacketLoss ? static _ => Random.Shared.NextDouble() >= 0.2 : null;
        InProcessServer? local = null;
        PenBridgeClient client;
        try
        {
            if (args.Has("--in-process-server"))
            {
                local = InProcessServer.Start(loggers, args.Port, fingerTouches: args.Has("--finger-touches"));
                client = CreateClient(new InMemorySecretStore(), loggers, filter);
                await client.PairWithPinAsync("127.0.0.1", local.Server.Port, local.Server.CurrentPin!, ct);
            }
            else
            {
                FileSecretStore store = OpenStore();
                if (!store.TryGet(LastServerKey, out byte[]? saved))
                {
                    return Fail("No paired server: run 'pair' first.");
                }

                string[] parts = Encoding.UTF8.GetString(saved).Split('\n');
                byte[] fingerprint = new byte[CertificateFingerprint.Length];
                if (parts.Length != 3 || !CertificateFingerprint.TryParseBase64Url(parts[2], fingerprint))
                {
                    return Fail("The saved server record is corrupt: run 'pair' again.");
                }

                string host = args.Value("--host") ?? parts[0];
                int port = args.Port ?? int.Parse(parts[1], CultureInfo.InvariantCulture);
                client = CreateClient(store, loggers, filter);
                await client.ConnectAsync(host, port, fingerprint, ct);
            }

            await using (client)
            {
                Console.WriteLine($"Drawing '{script}' to '{PeerText.Sanitize(client.ServerName)}' (negotiated: {client.NegotiatedCapabilities})...");
                int sent = 0;
                for (int pass = 0; pass < repeat; pass++)
                {
                    foreach (ScriptFrame frame in frames)
                    {
                        if (!client.SendState(frame.Pen, frame.Touches, frame.History))
                        {
                            return Fail("The connection closed while drawing.");
                        }

                        sent++;
                        await Task.Delay(frame.Delay, ct);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300), ct); // let release repeats and the server catch up
                Console.WriteLine($"Sent {sent} frames over {client.Transport}; RTT {client.RoundTripTime?.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} ms.");
                if (local is not null)
                {
                    PenBridgeServerStats stats = local.Server.GetStats();
                    Console.WriteLine($"Server: transport {stats.Transport}, {stats.Counters}, {local.Sink.PenEvents} pen / {local.Sink.GestureEvents} gesture events.");
                }

                await client.DisconnectAsync();
            }

            return 0;
        }
        finally
        {
            if (local is not null)
            {
                await local.DisposeAsync();
            }
        }
    }

    private static async Task<int> ServeAsync(Args args, ILoggerFactory loggers, CancellationToken ct)
    {
        await using InProcessServer local = InProcessServer.Start(loggers, args.Port, persistent: true, fingerTouches: args.Has("--finger-touches"));
        PenBridgeServer server = local.Server;
        Console.WriteLine($"Serving on port {server.Port} (fp {server.FingerprintBase64Url}). Ctrl+C to stop.");
        server.PairingWindowChanged += (_, _) =>
        {
            if (server.CurrentPin is { } pin)
            {
                Console.WriteLine($"Pairing window open: PIN {pin}; QR {server.CreateQrPayload()?.Format()}");
            }
        };
        server.OpenPairingWindow();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>A server in this process with a printing sink. Its identity is in memory unless persistent.</summary>
    private sealed class InProcessServer : IAsyncDisposable
    {
        private readonly ServerIdentity _identity;

        private readonly LatencyCapture? _latency;

        private InProcessServer(ServerIdentity identity, PenBridgeServer server, LoggingInputSink sink, LatencyCapture? latency)
        {
            _identity = identity;
            Server = server;
            Sink = sink;
            _latency = latency;
            if (latency is not null)
            {
                server.SessionEnded += (_, _) => latency.Dump("session end");
            }
        }

        public PenBridgeServer Server { get; }

        public LoggingInputSink Sink { get; }

        public static InProcessServer Start(ILoggerFactory loggers, int? port, bool persistent = false, bool fingerTouches = false)
        {
            ISecretStore store = persistent
                ? new FileSecretStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".penbridge", "simserver"))
                : new InMemorySecretStore();
            ServerIdentity identity = ServerIdentity.LoadOrCreate(store);
            var sink = new LoggingInputSink(Console.Out);
            LatencyCapture? latency = LatencyCapture.CreateIfEnabled();
            var server = new PenBridgeServer(new PenBridgeServerOptions
            {
                Identity = identity,
                Registry = new PairedDeviceRegistry(store),
                InputSink = sink,
                ServerName = "PenBridge SimServer",
                Port = port ?? ProtocolInfo.DefaultPort,
                TargetDisplay = new DisplayRect(0, 0, 1512, 982),
                LoggerFactory = loggers,
                LatencyRecorder = latency?.Recorder,
                FingerTouches = fingerTouches,
            });
            server.Start();
            if (!persistent)
            {
                server.OpenPairingWindow();
            }

            return new InProcessServer(identity, server, sink, latency);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _latency?.Dump("server stopped");
            _identity.Dispose();
        }
    }

    /// <summary>Tiny parser: the first token is the command, the first bare token after it the positional argument.</summary>
    private sealed class Args
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

        public Args(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
                {
                    bool isFlag = a is "--in-process-server" or "--verbose" or "--help" or "--finger-touches";
                    _options[a] = !isFlag && i + 1 < args.Length ? args[++i] : null;
                }
                else if (Command is null)
                {
                    Command = a;
                }
                else
                {
                    Positional ??= a;
                }
            }
        }

        public string? Command { get; }

        public string? Positional { get; }

        public int? Port => Value("--port") is { } p
            ? int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535
                ? port
                : throw new ArgumentException("--port must be 1-65535.")
            : null;

        public bool Has(string option) => _options.ContainsKey(option);

        public string? Value(string option) => _options.TryGetValue(option, out string? v) ? v : null;
    }
}
