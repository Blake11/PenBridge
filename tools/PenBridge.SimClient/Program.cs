using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PenBridge.Core;
using PenBridge.Core.Input;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Settings;

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
          penbridge-sim settings [--set <Key>=<value>]... [--host <host>] [--port <port>]
                                                                          Print the Mac's settings and displays (protocol
                                                                          1.2) and optionally change some, e.g.
                                                                          --set StylusMode=Mouse --set MouseSpeed=1.5
          penbridge-sim mirror [--seconds <n>] [--out <file.h264|file.hevc>] [--codecs h264,hevc]
                               [--draw <script>] [--host <host>] [--port <port>]
                                                                          Ask the last paired Mac for its screen
                                                                          (protocol 1.3) for n seconds (default 10),
                                                                          write the Annex-B stream to --out, and print
                                                                          frames, drops, keyframe requests and latency.
                                                                          --draw loops a draw script meanwhile, to
                                                                          check the input path while video streams
          penbridge-sim serve [--port <port>]                             Run a server here that prints its input
          penbridge-sim scripts                                           List the draw scripts

        Options:
          --in-process-server   pair/draw/settings/mirror against a server started in this process (no Mac app
                                needed). Its synthetic encoder sends fake (undecodable) access units at the
                                planned fps and bitrate, and it prints pen and video latency at the end
          --finger-touches      the in-process/serve server offers FingerTouches (ADR-0026: one finger is a
                                relative cursor; try the one-finger script)
          --settings            draw: print the settings, displays and active area the Mac sends
          --verbose             debug logging

        Pen latency with vs without video, in-process: compare 'draw circle --repeat 20' with
        'mirror --draw circle --seconds 10' (both print pen send->inject p50/p95; video needs mirror).
        Against a real Mac the latency is in the Mac's latency log (scripts/latency-report.sh).

        PENBRIDGE_LATENCY_LOG=1 also dumps the in-process/serve server's latency capture (as
        latency-sim-*.tsv next to the Mac app's dumps; scripts/latency-report.sh prints them). Pings run every
        2 s, so draw for a few seconds (--repeat) to get a clock-offset estimate.

        Scripts: line, circle, pressure-ramp, tilt-sweep, hover, button-click, two-finger-scroll (ends in a
                 flick), pinch, rotate, two-finger-tap (right-click), smart-zoom (two-finger double tap),
                 three-finger-drag, swipe-up, swipe-down, swipe-left, swipe-right, four-finger-pinch-in,
                 four-finger-pinch-out, page-swipe-left, page-swipe-right (two-finger horizontal flick:
                 back/forward), one-finger (relative cursor: move, click, double click, tap-and-a-half drag;
                 needs a server offering FingerTouches), packet-loss (a circle with 20% of datagrams dropped)
        """;

    // What the SimClient's "decoder" accepts (MirrorStart limits): a 4K-class decoder on a 120 Hz panel.
    private const int MirrorMaxSize = 4096;
    private const int MirrorMaxFps = 120;

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
                "settings" => await SettingsAsync(parsed, loggers, cts.Token),
                "mirror" => await MirrorAsync(parsed, loggers, cts.Token),
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
        try
        {
            (PenBridgeClient? client, local, string? error) = await OpenSessionAsync(args, loggers, filter, args.Has("--settings") ? PrintRemote : null, ct);
            if (client is null)
            {
                return Fail(error!);
            }

            await using (client)
            {
                Console.WriteLine($"Drawing '{script}' to '{PeerText.Sanitize(client.ServerName)}' (negotiated: {client.NegotiatedCapabilities})...");
                int sent = await DrawLoopAsync(client, frames, repeat, ct);
                if (sent < 0)
                {
                    return Fail("The connection closed while drawing.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300), ct); // let release repeats and the server catch up
                Console.WriteLine($"Sent {sent} frames over {client.Transport}; RTT {client.RoundTripTime?.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} ms.");
                if (local is not null)
                {
                    PenBridgeServerStats stats = local.Server.GetStats();
                    Console.WriteLine($"Server: transport {stats.Transport}, {stats.Counters}, {local.Sink.PenEvents} pen / {local.Sink.GestureEvents} gesture events.");
                    Console.WriteLine(local.Latency.Summarize());
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

    /// <summary>
    /// Sends the script's frames <paramref name="repeat"/> times (0: until <paramref name="ct"/> is cancelled, which then
    /// ends the loop normally). Returns the frames sent, or -1 when the connection closed.
    /// </summary>
    private static async Task<int> DrawLoopAsync(PenBridgeClient client, IEnumerable<ScriptFrame> frames, int repeat, CancellationToken ct)
    {
        int sent = 0;
        try
        {
            for (int pass = 0; repeat == 0 || pass < repeat; pass++)
            {
                foreach (ScriptFrame frame in frames)
                {
                    if (!client.SendState(frame.Pen, frame.Touches, frame.History))
                    {
                        return -1;
                    }

                    sent++;
                    await Task.Delay(frame.Delay, ct);
                }
            }
        }
        catch (OperationCanceledException) when (repeat == 0)
        {
            client.SendState(null, [], []); // lift the pen if the time ran out mid-stroke
        }

        return sent;
    }

    private static async Task<int> MirrorAsync(Args args, ILoggerFactory loggers, CancellationToken ct)
    {
        int seconds = args.Value("--seconds") is { } sv
            ? int.TryParse(sv, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and <= 3600
                ? n
                : throw new ArgumentException("--seconds must be 1-3600.")
            : 10;
        MirrorCodecs codecs = ParseCodecs(args.Value("--codecs"));
        IEnumerable<ScriptFrame>? frames = null;
        string? script = args.Value("--draw");
        if (script is not null && !DrawScripts.TryGet(script, out frames))
        {
            return Fail("--draw needs a script: " + string.Join(", ", DrawScripts.Names));
        }

        DatagramFilter? filter = script == DrawScripts.PacketLoss ? static _ => Random.Shared.NextDouble() >= 0.2 : null;
        await using var receiver = new MirrorReceiver(args.Value("--out"));
        InProcessServer? local = null;
        try
        {
            (PenBridgeClient? client, local, string? error) = await OpenSessionAsync(args, loggers, filter, receiver.Attach, ct);
            if (client is null)
            {
                return Fail(error!);
            }

            await using (client)
            {
                if ((client.NegotiatedCapabilities & ProtocolCapabilities.ScreenMirror) == 0)
                {
                    return Fail($"'{PeerText.Sanitize(client.ServerName)}' doesn't offer screen mirroring (negotiated: {client.NegotiatedCapabilities}).");
                }

                Console.WriteLine($"Mirroring '{PeerText.Sanitize(client.ServerName)}' for {seconds} s (codecs {codecs}){(script is null ? string.Empty : ", drawing '" + script + "'")}...");
                if (!client.StartMirror(new MirrorRequest(codecs, MirrorMaxSize, MirrorMaxSize, MirrorMaxFps)))
                {
                    return Fail("The connection closed before mirroring started.");
                }

                int sent = 0;
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    window.CancelAfter(TimeSpan.FromSeconds(seconds));
                    if (frames is not null)
                    {
                        sent = await DrawLoopAsync(client, frames, repeat: 0, window.Token);
                        if (sent < 0)
                        {
                            return Fail("The connection closed while drawing.");
                        }
                    }
                    else
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, window.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct); // the last MirrorReport, and the server catching up
                client.StopMirror();
                MirrorClientStats stats = client.MirrorStats;
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Phone: {receiver.Frames} frames ({receiver.Keyframes} keyframes, {receiver.Bytes / 1024} KiB), {receiver.Gaps} frame-number gaps; {stats.FramesDropped} dropped, {stats.FragmentsLost} fragments lost, {stats.KeyframeRequestsSent} keyframe requests, {stats.ProbesSent} probes, {stats.ReportsSent} reports, {stats.Epochs} epochs, {stats.AuthFailures} auth failures."));
                if (frames is not null)
                {
                    Console.WriteLine($"Sent {sent} input frames over {client.Transport}; RTT {client.RoundTripTime?.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} ms.");
                }

                if (local is not null)
                {
                    SyntheticEncoder encoder = local.Encoder;
                    Console.WriteLine($"Server: {encoder.FramesSubmitted} access units submitted ({encoder.KeyframesSubmitted} keyframes), {encoder.KeyframeRequests} keyframe requests raised, {encoder.Configs} configs; {local.Sink.PenEvents} pen events.");
                    Console.WriteLine(local.Latency.Summarize());
                }
                else
                {
                    Console.WriteLine("Mac-side counters and latency: the Mac's log and latency dump (scripts/latency-report.sh).");
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

    private static MirrorCodecs ParseCodecs(string? value)
    {
        if (value is null)
        {
            return MirrorCodecs.H264 | MirrorCodecs.Hevc;
        }

        MirrorCodecs codecs = MirrorCodecs.None;
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            codecs |= part.ToUpperInvariant() switch
            {
                "H264" => MirrorCodecs.H264,
                "HEVC" or "H265" => MirrorCodecs.Hevc,
                _ => throw new ArgumentException($"Unknown codec '{part}': use h264, hevc."),
            };
        }

        return codecs == MirrorCodecs.None ? throw new ArgumentException("--codecs needs h264 and/or hevc.") : codecs;
    }

    /// <summary>
    /// Connects to the last paired Mac (or pairs with an in-process server), subscribing <paramref name="subscribe"/>
    /// before the session starts so nothing sent right after SessionStart is missed.
    /// </summary>
    private static async Task<(PenBridgeClient? Client, InProcessServer? Local, string? Error)> OpenSessionAsync(
        Args args, ILoggerFactory loggers, DatagramFilter? filter, Action<PenBridgeClient>? subscribe, CancellationToken ct)
    {
        if (args.Has("--in-process-server"))
        {
            InProcessServer local = InProcessServer.Start(loggers, args.Port, fingerTouches: args.Has("--finger-touches"));
            PenBridgeClient demo = CreateClient(new InMemorySecretStore(), loggers, filter);
            subscribe?.Invoke(demo);
            await demo.PairWithPinAsync("127.0.0.1", local.Server.Port, local.Server.CurrentPin!, ct);
            return (demo, local, null);
        }

        FileSecretStore store = OpenStore();
        if (!store.TryGet(LastServerKey, out byte[]? saved))
        {
            return (null, null, "No paired server: run 'pair' first.");
        }

        string[] parts = Encoding.UTF8.GetString(saved).Split('\n');
        byte[] fingerprint = new byte[CertificateFingerprint.Length];
        if (parts.Length != 3 || !CertificateFingerprint.TryParseBase64Url(parts[2], fingerprint))
        {
            return (null, null, "The saved server record is corrupt: run 'pair' again.");
        }

        string host = args.Value("--host") ?? parts[0];
        int port = args.Port ?? int.Parse(parts[1], CultureInfo.InvariantCulture);
        PenBridgeClient client = CreateClient(store, loggers, filter);
        subscribe?.Invoke(client);
        await client.ConnectAsync(host, port, fingerprint, ct);
        return (client, null, null);
    }

    private static async Task<int> SettingsAsync(Args args, ILoggerFactory loggers, CancellationToken ct)
    {
        var updates = new List<SettingEntry>();
        foreach (string assignment in args.Values("--set"))
        {
            if (!MacSettings.TryParseAssignment(assignment, out SettingEntry entry))
            {
                return Fail($"Not a Mac setting assignment: '{assignment}'. Keys: {string.Join(", ", MacSettings.All.Select(d => d.Name))}.");
            }

            updates.Add(entry);
        }

        if (updates.Count > SettingsWire.MaxEntries)
        {
            return Fail($"At most {SettingsWire.MaxEntries} --set values.");
        }

        var snapshots = new SemaphoreSlim(0);
        (PenBridgeClient? client, InProcessServer? local, string? error) = await OpenSessionAsync(
            args, loggers, null, c => c.SettingsSnapshotReceived += (_, _) => snapshots.Release(), ct);
        if (client is null)
        {
            return Fail(error!);
        }

        try
        {
            await using (client)
            {
                if ((client.NegotiatedCapabilities & ProtocolCapabilities.RemoteSettings) == 0)
                {
                    return Fail($"'{PeerText.Sanitize(client.ServerName)}' doesn't offer remote settings (negotiated: {client.NegotiatedCapabilities}).");
                }

                TimeSpan wait = TimeSpan.FromSeconds(5);
                if (!await snapshots.WaitAsync(wait, ct))
                {
                    return Fail("No settings snapshot from the Mac.");
                }

                PrintSettings(client);
                if (updates.Count > 0)
                {
                    Console.WriteLine("Sending: " + string.Join(", ", updates.Select(e => MacSettings.Format(e))));

                    // Drop snapshots that arrived meanwhile (e.g. a change on the Mac), so the wait below is for the reply.
                    while (snapshots.Wait(0, ct))
                    {
                    }

                    if (!await client.UpdateSettingsAsync(updates) || !await snapshots.WaitAsync(wait, ct))
                    {
                        return Fail("The Mac didn't answer the update.");
                    }

                    Console.WriteLine("After the update (invalid values are ignored by the Mac):");
                    PrintSettings(client);
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

    private static void PrintRemote(PenBridgeClient client)
    {
        client.SettingsSnapshotReceived += (_, e) => Console.WriteLine("Settings: " + string.Join(", ", e.Entries.Select(Describe)));
        client.DisplayListReceived += (_, e) => Console.WriteLine("Displays: " + string.Join("; ", e.Displays.Select(Describe)));
        client.ActiveAreaReceived += (_, e) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Active area: {e.Area.X:F3},{e.Area.Y:F3} {e.Area.Width:F3}x{e.Area.Height:F3}"));
    }

    private static void PrintSettings(PenBridgeClient client)
    {
        foreach (DisplayListEntry d in client.LastDisplayList ?? [])
        {
            Console.WriteLine("  display " + Describe(d));
        }

        foreach (SettingEntry entry in client.LastSettingsSnapshot ?? [])
        {
            Console.WriteLine("  " + Describe(entry));
        }
    }

    private static string Describe(SettingEntry entry) => PeerText.Sanitize(MacSettings.Format(entry));

    private static string Describe(DisplayListEntry d) => string.Create(
        CultureInfo.InvariantCulture,
        $"{PeerText.Sanitize(d.Id)} '{PeerText.Sanitize(d.Name)}' {d.WidthPt}x{d.HeightPt}{(d.IsMain ? " (main)" : string.Empty)}");

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

        private InProcessServer(ServerIdentity identity, PenBridgeServer server, LoggingInputSink sink, LatencyCapture latency, SyntheticEncoder encoder)
        {
            _identity = identity;
            Server = server;
            Sink = sink;
            Latency = latency;
            Encoder = encoder;
            server.SessionEnded += (_, _) => latency.Dump("session end");
        }

        public PenBridgeServer Server { get; }

        public LoggingInputSink Sink { get; }

        public LatencyCapture Latency { get; }

        /// <summary>Stands in for capture and encoding when the phone mirrors (protocol 1.3).</summary>
        public SyntheticEncoder Encoder { get; }

        public static InProcessServer Start(ILoggerFactory loggers, int? port, bool persistent = false, bool fingerTouches = false)
        {
            ISecretStore store = persistent
                ? new FileSecretStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".penbridge", "simserver"))
                : new InMemorySecretStore();
            ServerIdentity identity = ServerIdentity.LoadOrCreate(store);
            var sink = new LoggingInputSink(Console.Out);
            LatencyCapture latency = LatencyCapture.Create();
            var server = new PenBridgeServer(new PenBridgeServerOptions
            {
                Identity = identity,
                Registry = new PairedDeviceRegistry(store),
                InputSink = sink,
                ServerName = "PenBridge SimServer",
                Port = port ?? ProtocolInfo.DefaultPort,
                TargetDisplay = new DisplayRect(0, 0, 1512, 982),
                LoggerFactory = loggers,
                LatencyRecorder = latency.Recorder,
                FingerTouches = fingerTouches,
                RemoteSettings = true,
                ActiveArea = true,
                ScreenMirror = !persistent, // serve hosts real phones: fake video would only feed their decoders garbage
            });
            SimSettings.Attach(server);
            var encoder = new SyntheticEncoder(server);
            server.Start();
            if (!persistent)
            {
                server.OpenPairingWindow();
            }

            return new InProcessServer(identity, server, sink, latency, encoder);
        }

        public async ValueTask DisposeAsync()
        {
            Encoder.Dispose();
            await Server.DisposeAsync();
            Latency.Dump("server stopped");
            _identity.Dispose();
        }
    }

    /// <summary>The in-process server's Mac settings: in memory, validated like the Mac app, two fake displays, mirroring on.</summary>
    private static class SimSettings
    {
        private static readonly DisplayListEntry[] Displays =
        [
            new("sim-main", "Sim Display", true, 1512, 982),
            new("sim-side", "Sim Side Display", false, 1920, 1080),
        ];

        // Global layout of the fake displays: the side display to the right of the main one.
        private static readonly DisplayRect[] Rects = [new(0, 0, 1512, 982), new(1512, 0, 1920, 1080)];

        private static MappingTarget TargetFor(MacSettingValues values)
        {
            if (values.TargetDisplay == MacSettings.AllDisplays)
            {
                return MappingTarget.ForAllDisplays(Rects);
            }

            DisplayRect display = values.TargetDisplay == Displays[1].Id ? Rects[1] : Rects[0];
            return MappingTarget.ForDisplay(display, values.Region);
        }

        public static void Attach(PenBridgeServer server)
        {
            var values = new MacSettingValues();
            if (!MacSettings.TryParseAssignment("MirrorEnabled=true", out SettingEntry mirrorOn) || !values.TryApply(mirrorOn, Displays))
            {
                throw new InvalidOperationException("MirrorEnabled must be a settable Mac setting.");
            }

            _ = server.SendDisplayListAsync(Displays);
            _ = server.SendSettingsSnapshotAsync(values.ToEntries());
            server.SettingsUpdateRequested += (_, e) =>
            {
                IReadOnlyList<SettingEntry> reply;
                lock (values)
                {
                    IReadOnlyList<SettingEntry> changed = values.ApplyUpdate(e.Entries, Displays);
                    Console.WriteLine($"[server] settings update: {e.Entries.Count} entries, changed: {(changed.Count == 0 ? "nothing" : string.Join(", ", changed.Select(c => PeerText.Sanitize(MacSettings.Format(c)))))}");
                    reply = values.ToEntries();
                    server.SetInputSettings(values.ToInputSettings());
                    server.SetTarget(TargetFor(values));
                }

                _ = server.SendSettingsSnapshotAsync(reply);
            };
        }
    }

    /// <summary>Tiny parser: the first token is the command, the first bare token after it the positional argument.</summary>
    private sealed class Args
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _all = new(StringComparer.Ordinal);

        public Args(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
                {
                    bool isFlag = a is "--in-process-server" or "--verbose" or "--help" or "--finger-touches" or "--settings";
                    string? value = !isFlag && i + 1 < args.Length ? args[++i] : null;
                    _options[a] = value;
                    if (value is not null)
                    {
                        if (!_all.TryGetValue(a, out List<string>? list))
                        {
                            _all[a] = list = [];
                        }

                        list.Add(value);
                    }
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

        /// <summary>Every value of a repeatable option, in order.</summary>
        public List<string> Values(string option) => _all.TryGetValue(option, out List<string>? v) ? v : [];
    }
}
