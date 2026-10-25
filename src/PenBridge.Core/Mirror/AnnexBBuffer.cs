namespace PenBridge.Core.Mirror;

/// <summary>
/// Builds one Annex-B access unit in a reused buffer: parameter sets and the encoder's length-prefixed (AVCC/HVCC) NAL
/// units, each behind a 4-byte start code. Grows only when a frame is larger than any before, so steady state allocates
/// nothing.
/// </summary>
/// <remarks>Not thread-safe; one per encoder session (the Mac head uses it from the VideoToolbox output callback).</remarks>
public sealed class AnnexBBuffer
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    private byte[] _buffer;
    private int _length;

    /// <param name="initialCapacity">Bytes to reserve up front (at least 1).</param>
    public AnnexBBuffer(int initialCapacity = 256 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _buffer = new byte[initialCapacity];
    }

    /// <summary>The access unit built since <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public void Reset() => _length = 0;

    /// <summary>Appends a start code and <paramref name="nal"/>.</summary>
    public void AppendNal(ReadOnlySpan<byte> nal)
    {
        Ensure(StartCode.Length + nal.Length);
        StartCode.CopyTo(_buffer.AsSpan(_length));
        nal.CopyTo(_buffer.AsSpan(_length + StartCode.Length));
        _length += StartCode.Length + nal.Length;
    }

    /// <summary>
    /// Appends every NAL unit of <paramref name="data"/>, a sequence of big-endian <paramref name="lengthSize"/>-byte
    /// lengths each followed by that many bytes, behind start codes.
    /// </summary>
    /// <returns>False when a length runs past the end (the frame is malformed; the buffer keeps what was appended).</returns>
    public bool AppendLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize)
    {
        if (lengthSize is not (1 or 2 or 4))
        {
            return false;
        }

        while (!data.IsEmpty)
        {
            if (data.Length < lengthSize)
            {
                return false;
            }

            uint length = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                length = (length << 8) | data[i];
            }

            data = data[lengthSize..];
            if (length == 0)
            {
                continue;
            }

            if (length > (uint)data.Length)
            {
                return false;
            }

            AppendNal(data[..(int)length]);
            data = data[(int)length..];
        }

        return true;
    }

    private void Ensure(int extra)
    {
        long needed = (long)_length + extra;
        if (needed <= _buffer.Length)
        {
            return;
        }

        if (needed > Array.MaxLength)
        {
            throw new InvalidOperationException("The access unit is too large.");
        }

        Array.Resize(ref _buffer, (int)Math.Min(Math.Max(needed, 2L * _buffer.Length), Array.MaxLength));
    }
}
