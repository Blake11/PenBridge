using System.Security.Cryptography;
using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

public class ReconnectAuthTests
{
    private readonly byte[] _key = KeyScheduleTests.Fill(32, 1);
    private readonly byte[] _challenge = KeyScheduleTests.Fill(32, 2);
    private readonly byte[] _nonce = KeyScheduleTests.Fill(32, 3);
    private readonly byte[] _fp = KeyScheduleTests.Fill(32, 4);

    [Fact]
    public void Proof_matches_spec_construction_and_verifies()
    {
        byte[] proof = new byte[32];
        ReconnectAuth.ComputeProof(_key, _challenge, _nonce, _fp, proof);

        byte[] expected = HMACSHA256.HashData(_key, (byte[])[.. "pb-auth-v1"u8, .. _challenge, .. _nonce, .. _fp]);
        Assert.Equal(expected, proof);
        Assert.True(ReconnectAuth.Verify(_key, _challenge, _nonce, _fp, proof));
    }

    [Fact]
    public void Verify_fails_on_any_mismatch()
    {
        byte[] proof = new byte[32];
        ReconnectAuth.ComputeProof(_key, _challenge, _nonce, _fp, proof);

        Assert.False(ReconnectAuth.Verify(Flip(_key), _challenge, _nonce, _fp, proof));
        Assert.False(ReconnectAuth.Verify(_key, Flip(_challenge), _nonce, _fp, proof));
        Assert.False(ReconnectAuth.Verify(_key, _challenge, Flip(_nonce), _fp, proof));
        Assert.False(ReconnectAuth.Verify(_key, _challenge, _nonce, Flip(_fp), proof)); // relay: different cert
        Assert.False(ReconnectAuth.Verify(_key, _challenge, _nonce, _fp, Flip(proof)));
        Assert.False(ReconnectAuth.Verify(_key, _challenge, _nonce, _fp, proof.AsSpan(0, 31)));
        Assert.False(ReconnectAuth.Verify(_key, _challenge, new byte[5], _fp, proof));
        Assert.False(ReconnectAuth.Verify(_key, _challenge, _nonce, _fp, []));
    }

    private static byte[] Flip(byte[] b)
    {
        byte[] c = (byte[])b.Clone();
        c[^1] ^= 0x80;
        return c;
    }
}
