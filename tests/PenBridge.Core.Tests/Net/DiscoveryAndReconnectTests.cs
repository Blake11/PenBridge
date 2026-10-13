using System.Text;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Net;

/// <summary><see cref="DiscoveredServer"/> (TXT → model) and <see cref="ReconnectPolicy"/>.</summary>
public class DiscoveryAndReconnectTests
{
    private static readonly byte[] Fingerprint = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherFingerprint = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private const string FingerprintText = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    private static Dictionary<string, byte[]?> Txt(string? v = "1.0", string? fp = FingerprintText, string? name = "Studio Mac")
    {
        var txt = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (v is not null)
        {
            txt["v"] = Encoding.UTF8.GetBytes(v);
        }

        if (fp is not null)
        {
            txt["fp"] = Encoding.UTF8.GetBytes(fp);
        }

        if (name is not null)
        {
            txt["name"] = Encoding.UTF8.GetBytes(name);
        }

        return txt;
    }

    private static DiscoveredServer Create(string? v = "1.0", byte[]? fp = null, string name = "Mac")
    {
        string fpText = Base64UrlBytes.Encode(fp ?? Fingerprint);
        Assert.True(DiscoveredServer.TryCreate("Mac service", "192.168.1.20", 47820, Txt(v, fpText, name), out DiscoveredServer? s));
        return s!;
    }

    [Fact]
    public void Maps_a_valid_resolved_service()
    {
        Dictionary<string, byte[]?> txt = Txt();
        txt["future"] = null;
        txt["blob"] = [0xFF, 0xFE];
        Assert.True(DiscoveredServer.TryCreate("Studio Mac (2)", "fe80::1", 47821, txt, out DiscoveredServer? server));
        Assert.Equal("Studio Mac (2)", server!.ServiceName);
        Assert.Equal("Studio Mac", server.Name);
        Assert.Equal("fe80::1", server.Host);
        Assert.Equal(47821, server.Port);
        Assert.Equal(Fingerprint, server.Fingerprint.ToArray());
        Assert.Equal(new ProtocolVersion(1, 0), server.Version);
        Assert.True(server.IsCompatible);
        Assert.True(server.HasFingerprint(Fingerprint));
        Assert.False(server.HasFingerprint(OtherFingerprint));
    }

    [Fact]
    public void Keys_are_case_insensitive_and_a_future_minor_is_compatible()
    {
        var txt = new Dictionary<string, byte[]?>
        {
            ["V"] = "1.7"u8.ToArray(),
            ["FP"] = Encoding.ASCII.GetBytes(FingerprintText),
            ["Name"] = "Mac"u8.ToArray(),
        };
        Assert.True(DiscoveredServer.TryCreate("svc", "mac.local", 47820, txt, out DiscoveredServer? server));
        Assert.True(server!.IsCompatible);
    }

    [Fact]
    public void Another_major_is_listed_but_incompatible()
    {
        DiscoveredServer server = Create(v: "2.0");
        Assert.False(server.IsCompatible);
    }

    [Fact]
    public void Name_is_sanitized_and_falls_back_to_the_service_name()
    {
        Assert.True(DiscoveredServer.TryCreate("Svc‮", "10.0.0.2", 1, Txt(name: "Evil‮gpj.exe\n"), out DiscoveredServer? server));
        Assert.Equal("Evil?gpj.exe?", server!.Name);
        Assert.Equal("Svc?", server.ServiceName);

        Assert.True(DiscoveredServer.TryCreate("Svc", "10.0.0.2", 1, Txt(name: string.Empty), out server));
        Assert.Equal("Svc", server!.Name);
    }

    [Theory]
    [InlineData(null, "10.0.0.2", 47820)]
    [InlineData("", "10.0.0.2", 47820)]
    [InlineData("svc", null, 47820)]
    [InlineData("svc", "", 47820)]
    [InlineData("svc", "bad host!", 47820)]
    [InlineData("svc", "10.0.0.2", 0)]
    [InlineData("svc", "10.0.0.2", 65536)]
    [InlineData("svc", "10.0.0.2", -1)]
    public void Rejects_invalid_service_or_address(string? service, string? host, int port) =>
        Assert.False(DiscoveredServer.TryCreate(service, host, port, Txt(), out _));

    [Fact]
    public void Rejects_missing_null_or_invalid_txt_values()
    {
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, null, out _));
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, Txt(v: null), out _));
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, Txt(fp: null), out _));
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, Txt(fp: "short"), out _));
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, Txt(v: "x"), out _));

        Dictionary<string, byte[]?> nullValue = Txt();
        nullValue["fp"] = null;
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, nullValue, out _));

        Dictionary<string, byte[]?> badUtf8 = Txt();
        badUtf8["name"] = [0x4D, 0xC3, 0x28];
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, badUtf8, out _));

        Dictionary<string, byte[]?> duplicate = Txt();
        duplicate["FP"] = Encoding.ASCII.GetBytes(FingerprintText);
        Assert.False(DiscoveredServer.TryCreate("svc", "10.0.0.2", 1, duplicate, out _));
    }

    [Fact]
    public void Backoff_doubles_from_one_second_to_a_thirty_second_cap()
    {
        int[] expected = [1, 1, 2, 4, 8, 16, 30, 30, 30];
        int[] attempts = [-3, 0, 1, 2, 3, 4, 5, 6, int.MaxValue];
        for (int i = 0; i < attempts.Length; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(expected[i]), ReconnectPolicy.DelayForAttempt(attempts[i]));
        }
    }

    [Theory]
    [InlineData(ConnectFailure.ConnectionFailed, true)]
    [InlineData(ConnectFailure.ConnectionClosed, true)]
    [InlineData(ConnectFailure.Timeout, true)]
    [InlineData(ConnectFailure.Busy, true)]
    [InlineData(ConnectFailure.ProtocolError, false)]
    [InlineData(ConnectFailure.IncompatibleVersion, false)]
    [InlineData(ConnectFailure.ServerIdentityMismatch, false)]
    [InlineData(ConnectFailure.NotPaired, false)]
    [InlineData(ConnectFailure.PairingFailed, false)]
    [InlineData(ConnectFailure.PairingExpired, false)]
    [InlineData(ConnectFailure.PairingUnavailable, false)]
    [InlineData(ConnectFailure.AuthFailed, false)]
    [InlineData(ConnectFailure.PeerError, false)]
    public void Retries_only_transient_connect_failures(ConnectFailure failure, bool retry) =>
        Assert.Equal(retry, ReconnectPolicy.ShouldRetry(failure));

    [Theory]
    [InlineData(SessionEndReason.ConnectionLost, true)]
    [InlineData(SessionEndReason.Timeout, true)]
    [InlineData(SessionEndReason.PeerClosed, true)]
    [InlineData(SessionEndReason.Shutdown, true)]
    [InlineData(SessionEndReason.LocalDisconnect, false)]
    [InlineData(SessionEndReason.TakenOver, false)]
    [InlineData(SessionEndReason.Unpaired, false)]
    [InlineData(SessionEndReason.ProtocolError, false)]
    [InlineData(SessionEndReason.PeerError, false)]
    [InlineData(SessionEndReason.InternalError, false)]
    public void Reconnects_only_after_recoverable_session_ends(SessionEndReason reason, bool reconnect) =>
        Assert.Equal(reconnect, ReconnectPolicy.ShouldReconnect(reason));

    [Fact]
    public void Auto_connect_picks_the_paired_last_used_compatible_server()
    {
        var credentials = new ClientCredentialStore(new InMemorySecretStore());
        DiscoveredServer other = Create(fp: OtherFingerprint);
        DiscoveredServer last = Create();
        DiscoveredServer[] list = [other, last];

        // Not paired yet.
        Assert.Null(ReconnectPolicy.SelectAutoConnectTarget(list, Fingerprint, credentials));

        credentials.SaveDeviceKey(Fingerprint, new byte[32]);
        Assert.Same(last, ReconnectPolicy.SelectAutoConnectTarget(list, Fingerprint, credentials));

        // No last-used server, or it isn't on the network.
        Assert.Null(ReconnectPolicy.SelectAutoConnectTarget(list, default, credentials));
        Assert.Null(ReconnectPolicy.SelectAutoConnectTarget([other], Fingerprint, credentials));

        // Incompatible version.
        Assert.Null(ReconnectPolicy.SelectAutoConnectTarget([Create(v: "2.0")], Fingerprint, credentials));

        // Forgotten.
        credentials.Forget(Fingerprint);
        Assert.Null(ReconnectPolicy.SelectAutoConnectTarget(list, Fingerprint, credentials));
    }

    [Fact]
    public void Auto_connect_with_a_custom_paired_check_asks_only_for_the_match()
    {
        DiscoveredServer other = Create(fp: OtherFingerprint);
        DiscoveredServer last = Create();
        var asked = new List<DiscoveredServer>();
        Assert.Same(last, ReconnectPolicy.SelectAutoConnectTarget([other, last], Fingerprint, s => { asked.Add(s); return true; }));
        Assert.Equal([last], asked);
        Assert.Null(ReconnectPolicy.SelectAutoConnectTarget([other, last], Fingerprint, _ => false));
    }
}
