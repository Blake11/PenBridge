namespace PenBridge.Core.Tests.Protocol;

internal static class Hex
{
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    public static byte[] Range(int start, int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
