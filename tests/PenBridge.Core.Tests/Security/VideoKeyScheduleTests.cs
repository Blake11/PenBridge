using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

/// <summary>K_vs / K_vc (protocol 1.3). The vectors were computed with an independent HKDF (Python hmac/hashlib).</summary>
public class VideoKeyScheduleTests
{
    private static readonly byte[] DeviceKey = KeyScheduleTests.Fill(32, 1);
    private static readonly byte[] ClientNonce = KeyScheduleTests.Fill(32, 2);
    private static readonly byte[] ServerNonce = KeyScheduleTests.Fill(32, 3);

    [Theory]
    [InlineData(VideoKeyDirection.ServerToClient, "ac91a6d07a3ba93dbce3ee016c9debd4b4a2d86d56e06bcc6692852901ce7249")]
    [InlineData(VideoKeyDirection.ClientToServer, "f85082fbbc2e9b405166d039f84bea28c8af6a422859c4c3ad631f046c30d7fe")]
    public void Video_keys_match_known_answers(VideoKeyDirection direction, string expected)
    {
        byte[] key = new byte[32];
        KeySchedule.DeriveVideoKey(DeviceKey, ClientNonce, ServerNonce, 0x01020304, direction, key);
        Assert.Equal(expected, Convert.ToHexStringLower(key));
    }

    [Fact]
    public void Input_key_is_unchanged_by_the_shared_derivation()
    {
        byte[] key = new byte[32];
        KeySchedule.DeriveInputKey(DeviceKey, ClientNonce, ServerNonce, 0x01020304, key);
        Assert.Equal("0bb550b4648a001f6c5d68cb4b7bd70a25f03cb67edd92705a3c05588a7046ea", Convert.ToHexStringLower(key));
    }

    [Fact]
    public void Video_keys_match_the_spec_construction_and_bind_every_input()
    {
        byte[] actual = new byte[32];
        KeySchedule.DeriveVideoKey(DeviceKey, ClientNonce, ServerNonce, 0xA0B0C0D0, VideoKeyDirection.ServerToClient, actual);
        byte[] expected = new byte[32];
        KeySchedule.Hkdf(DeviceKey, [.. ClientNonce, .. ServerNonce], [.. "pb-video-s2c-v1"u8, 0xA0, 0xB0, 0xC0, 0xD0], expected);
        Assert.Equal(expected, actual);

        byte[] other = new byte[32];
        KeySchedule.DeriveVideoKey(DeviceKey, ClientNonce, ServerNonce, 0xA0B0C0D1, VideoKeyDirection.ServerToClient, other);
        Assert.NotEqual(actual, other);
        KeySchedule.DeriveVideoKey(DeviceKey, ServerNonce, ClientNonce, 0xA0B0C0D0, VideoKeyDirection.ServerToClient, other);
        Assert.NotEqual(actual, other);
        KeySchedule.DeriveVideoKey(KeyScheduleTests.Fill(32, 9), ClientNonce, ServerNonce, 0xA0B0C0D0, VideoKeyDirection.ServerToClient, other);
        Assert.NotEqual(actual, other);
    }

    [Fact]
    public void The_three_session_keys_differ()
    {
        byte[] input = new byte[32];
        byte[] s2c = new byte[32];
        byte[] c2s = new byte[32];
        KeySchedule.DeriveInputKey(DeviceKey, ClientNonce, ServerNonce, 7, input);
        KeySchedule.DeriveVideoKey(DeviceKey, ClientNonce, ServerNonce, 7, VideoKeyDirection.ServerToClient, s2c);
        KeySchedule.DeriveVideoKey(DeviceKey, ClientNonce, ServerNonce, 7, VideoKeyDirection.ClientToServer, c2s);
        Assert.NotEqual(input, s2c);
        Assert.NotEqual(input, c2s);
        Assert.NotEqual(s2c, c2s);
    }

    [Fact]
    public void Rejects_wrong_lengths_and_directions()
    {
        byte[] ok = new byte[32];
        Assert.Equal("deviceKey", Assert.Throws<ArgumentException>(() => KeySchedule.DeriveVideoKey(new byte[31], ok, ok, 1, VideoKeyDirection.ServerToClient, new byte[32])).ParamName);
        Assert.Equal("clientNonce", Assert.Throws<ArgumentException>(() => KeySchedule.DeriveVideoKey(ok, new byte[16], ok, 1, VideoKeyDirection.ServerToClient, new byte[32])).ParamName);
        Assert.Equal("serverNonce", Assert.Throws<ArgumentException>(() => KeySchedule.DeriveVideoKey(ok, ok, new byte[33], 1, VideoKeyDirection.ServerToClient, new byte[32])).ParamName);
        Assert.Throws<ArgumentException>(() => KeySchedule.DeriveVideoKey(ok, ok, ok, 1, VideoKeyDirection.ServerToClient, new byte[16]));
        Assert.Equal("direction", Assert.Throws<ArgumentOutOfRangeException>(() => KeySchedule.DeriveVideoKey(ok, ok, ok, 1, (VideoKeyDirection)3, new byte[32])).ParamName);
    }
}
