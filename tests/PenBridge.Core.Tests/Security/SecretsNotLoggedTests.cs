using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Net;

namespace PenBridge.Core.Tests.Security;

/// <summary>
/// No log record, neither its rendered message nor its structured state (the <c>[LoggerMessage]</c> arguments, caught
/// even when the template doesn't print them), its scopes or its exception, carries a pairing secret or key: the PIN
/// (right and wrong), the QR token or text, or a device key, as hex, base64 or base64url.
/// </summary>
public class SecretsNotLoggedTests
{
    [Fact]
    public async Task Pin_and_qr_pairing_reconnect_and_sessions_never_log_a_secret()
    {
        var collector = new FakeLogCollector();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(collector)));
        var secrets = new List<string>();
        var pins = new List<string>();

        // The harness is disposed before the logs are read, so the session-end and disconnect records are in.
        await RunAsync(factory, secrets, pins);
        IReadOnlyList<FakeLogRecord> records = collector.GetSnapshot();
        AssertLogged(records, "PenBridge.Server", 7, "a pairing attempt");
        AssertLogged(records, "PenBridge.Server", 8, "a pairing");
        AssertLogged(records, "PenBridge.Server", 9, "the failed PIN");
        AssertLogged(records, "PenBridge.Server", 11, "a session start");
        Assert.Contains(records, r => r.Category == "PenBridge.Client");
        Assert.True(records.Any(r => Texts(r).Any(t => t.Contains("Test Phone", StringComparison.Ordinal))), "the scan never saw the logged device name");

        var pinPattern = new Regex($"(?<![0-9])({string.Join('|', pins)})(?![0-9])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (FakeLogRecord record in records)
        {
            foreach (string text in Texts(record))
            {
                Assert.False(pinPattern.IsMatch(text), $"a PIN in record {record.Id} ({record.Category}): {text}");
                foreach (string secret in secrets)
                {
                    Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"a secret in record {record.Id} ({record.Category}): {text}");
                }
            }
        }
    }

    /// <summary>PIN pairing (a wrong guess first), reconnect auth and QR pairing, each with a stroke; collects every secret seen.</summary>
    private static async Task RunAsync(ILoggerFactory factory, List<string> secrets, List<string> pins)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            ServerName = o.ServerName,
            BindAddress = o.BindAddress,
            Port = 0,
            TargetDisplay = o.TargetDisplay,
            TimeProvider = o.TimeProvider,
            LoggerFactory = factory,
        });
        Func<PenBridgeClientOptions, PenBridgeClientOptions> withLogger = o => new PenBridgeClientOptions
        {
            SecretStore = o.SecretStore,
            Surface = o.Surface,
            DeviceName = o.DeviceName,
            DatagramFilter = o.DatagramFilter,
            TimeProvider = o.TimeProvider,
            LoggerFactory = factory,
        };

        // PIN pairing: one wrong guess (a guess is as secret as the PIN), then the right PIN, then a stroke.
        h.Server.OpenPairingWindow();
        string pin = h.Server.CurrentPin!;
        string wrong = pin == "000000" ? "000001" : "000000";
        pins.Add(pin);
        pins.Add(wrong);
        var pinStore = new InMemorySecretStore();
        await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient(pinStore, configure: withLogger).PairWithPinAsync("127.0.0.1", h.Port, wrong, ct).Bounded());
        await Wait.Until(() => h.Server.PairingAttemptsLeft, n => n == PinIssuer.MaxFailedAttempts - 1, "the server counted the wrong PIN");
        PenBridgeClient pinClient = h.CreateClient(pinStore, configure: withLogger);
        await pinClient.PairWithPinAsync("127.0.0.1", h.Port, pin, ct).Bounded();
        await StrokeAsync(h, pinClient);
        AddKeyForms(secrets, DeviceKey(pinStore, h));
        AddKeyForms(secrets, ServerKey(h, pinClient.DeviceId));

        // Reconnect with the stored key (reconnect auth), then a stroke.
        await pinClient.DisconnectAsync().Bounded();
        await Wait.Until(() => !h.Server.GetStats().HasSession, "server freed the PIN session");
        PenBridgeClient again = h.CreateClient(pinStore, configure: withLogger);
        await again.ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray(), ct).Bounded();
        await StrokeAsync(h, again);
        await again.DisconnectAsync().Bounded();
        await Wait.Until(() => !h.Server.GetStats().HasSession, "server freed the reconnected session");

        // QR pairing on a second phone, then a stroke.
        h.Server.OpenPairingWindow();
        pins.Add(h.Server.CurrentPin!);
        PairingQrPayload qr = h.Server.CreateQrPayload("127.0.0.1")!;
        string qrText = qr.Format();
        secrets.Add(qrText);
        AddKeyForms(secrets, qr.Token.ToArray());
        var qrStore = new InMemorySecretStore();
        PenBridgeClient qrClient = h.CreateClient(qrStore, configure: withLogger);
        await qrClient.PairWithQrAsync(qrText, ct).Bounded();
        await StrokeAsync(h, qrClient);
        AddKeyForms(secrets, DeviceKey(qrStore, h));
        AddKeyForms(secrets, ServerKey(h, qrClient.DeviceId));
    }


    private static async Task StrokeAsync(NetHarness h, PenBridgeClient client)
    {
        await h.WaitForSurfaceAsync();
        int before = h.Sink.Pen().Length;
        EndToEndTests.DrawStroke(client);
        await Wait.Until(() => h.Sink.Pen().Length > before, "the stroke reached the sink");
        await h.SettleAsync();
    }

    private static byte[] DeviceKey(InMemorySecretStore store, NetHarness h)
    {
        var key = new byte[KeySchedule.KeyLength];
        Assert.True(new ClientCredentialStore(store).TryGetDeviceKey(h.Identity.Fingerprint, key), "the phone stored no device key");
        return key;
    }

    private static byte[] ServerKey(NetHarness h, Guid deviceId)
    {
        var key = new byte[KeySchedule.KeyLength];
        Assert.True(h.Registry.TryGetDeviceKey(deviceId, key), "the Mac stored no device key");
        return key;
    }

    /// <summary>The key as hex, base64 and base64url, whole and in 8-byte windows (a logged key id or prefix).</summary>
    private static void AddKeyForms(List<string> secrets, byte[] key)
    {
        secrets.Add(Convert.ToHexString(key));
        secrets.Add(Convert.ToBase64String(key));
        secrets.Add(Base64UrlBytes.Encode(key));
        for (int i = 0; i + 8 <= key.Length; i += 4)
        {
            secrets.Add(Convert.ToHexString(key, i, 8));
        }
    }

    private static IEnumerable<string> Texts(FakeLogRecord record)
    {
        yield return record.Message;
        if (record.Exception is { } exception)
        {
            yield return exception.ToString();
        }

        foreach (KeyValuePair<string, string?> pair in record.StructuredState ?? [])
        {
            yield return pair.Key;
            if (pair.Value is { } value)
            {
                yield return value;
            }
        }

        foreach (object? scope in record.Scopes)
        {
            if (scope?.ToString() is { } text)
            {
                yield return text;
            }
        }
    }

    private static void AssertLogged(IReadOnlyList<FakeLogRecord> records, string category, int id, string what) =>
        Assert.True(records.Any(r => r.Category == category && r.Id.Id == id), $"no record of {what} (event {id}): the check would be vacuous");
}
