namespace PenBridge.Core.Protocol;

/// <summary>Fixed field sizes used by control messages.</summary>
public static class WireSizes
{
    /// <summary>Phone device id.</summary>
    public const int DeviceId = 16;

    /// <summary>HelloAck server challenge.</summary>
    public const int Challenge = 32;

    /// <summary>Client and server nonces.</summary>
    public const int Nonce = 32;

    /// <summary>Auth HMAC-SHA256.</summary>
    public const int AuthMac = 32;

    /// <summary>Largest opaque blob (u16 length prefix).</summary>
    public const int MaxBlob = ushort.MaxValue;

    internal static void RequireLength(ReadOnlyMemory<byte> value, int length, string paramName)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"Expected exactly {length} bytes.", paramName);
        }
    }

    internal static int BlobSize(ReadOnlyMemory<byte> value, string paramName)
    {
        if (value.Length > MaxBlob)
        {
            throw new ArgumentException($"Blob exceeds {MaxBlob} bytes.", paramName);
        }

        return 2 + value.Length;
    }
}
