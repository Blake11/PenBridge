using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

public class KeyScheduleTests
{
    [Fact]
    public void Hkdf_matches_rfc5869_test_case_1()
    {
        byte[] ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        byte[] salt = Convert.FromHexString("000102030405060708090a0b0c");
        byte[] info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        byte[] okm = new byte[42];

        KeySchedule.Hkdf(ikm, salt, info, okm);

        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexStringLower(okm));
    }

    [Fact]
    public void Hkdf_matches_rfc5869_test_case_3_empty_salt_and_info()
    {
        byte[] ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        byte[] okm = new byte[42];

        KeySchedule.Hkdf(ikm, [], [], okm);

        Assert.Equal(
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8",
            Convert.ToHexStringLower(okm));
    }

    [Fact]
    public void Input_key_matches_spec_construction_and_binds_every_input()
    {
        byte[] kDev = Fill(32, 1);
        byte[] cn = Fill(32, 2);
        byte[] sn = Fill(32, 3);
        byte[] actual = new byte[32];
        KeySchedule.DeriveInputKey(kDev, cn, sn, 0x01020304, actual);

        byte[] expected = new byte[32];
        KeySchedule.Hkdf(kDev, [.. cn, .. sn], [.. "pb-input-v1"u8, 1, 2, 3, 4], expected);
        Assert.Equal(expected, actual);

        byte[] other = new byte[32];
        KeySchedule.DeriveInputKey(kDev, cn, sn, 0x01020305, other);
        Assert.NotEqual(actual, other);
        KeySchedule.DeriveInputKey(kDev, sn, cn, 0x01020304, other);
        Assert.NotEqual(actual, other);
    }

    [Fact]
    public void Device_key_matches_spec_construction()
    {
        byte[] shared = Fill(384, 9);
        byte[] th = Fill(32, 8);
        byte[] actual = new byte[32];
        KeySchedule.DeriveDeviceKey(shared, th, actual);

        byte[] expected = new byte[32];
        KeySchedule.Hkdf(shared, th, "pb-device-key-v1"u8, expected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Rejects_wrong_lengths()
    {
        byte[] ok = new byte[32];
        Assert.Throws<ArgumentException>(() => KeySchedule.DeriveInputKey(new byte[31], ok, ok, 1, new byte[32]));
        Assert.Throws<ArgumentException>(() => KeySchedule.DeriveInputKey(ok, new byte[16], ok, 1, new byte[32]));
        Assert.Throws<ArgumentException>(() => KeySchedule.DeriveInputKey(ok, ok, ok, 1, new byte[16]));
    }

    internal static byte[] Fill(int length, byte seed)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(seed + (i * 7));
        }

        return b;
    }
}
