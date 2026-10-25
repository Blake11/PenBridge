#if DEBUG
namespace PenBridge.Android.Mirror;

/// <summary>
/// Debug only: splits an Annex-B elementary stream (H.264 or HEVC) into access units, for the mirror test player. A new
/// access unit starts at the first non-VCL NAL (AUD, parameter sets, prefix SEI) after a picture's slices, or at a slice
/// that is the first of its picture. Keyframe = an IDR (H.264) or IRAP (HEVC) slice.
/// </summary>
internal static class AnnexBAccessUnits
{
    public readonly record struct Unit(int Offset, int Length, bool Keyframe);

    public static List<Unit> Split(ReadOnlySpan<byte> data, MirrorCodec codec)
    {
        var units = new List<Unit>();
        int auStart = -1;
        bool auHasVcl = false;
        bool auKey = false;
        int pos = NextStartCode(data, 0, out int scLen);
        while (pos >= 0)
        {
            int nal = pos + scLen;
            int next = NextStartCode(data, nal, out int nextScLen);
            if (nal + 2 >= data.Length)
            {
                break;
            }

            Classify(data[nal..], codec, out bool vcl, out bool firstSlice, out bool key);
            bool startsNew = auStart < 0 || (auHasVcl && (!vcl || firstSlice));
            if (startsNew)
            {
                if (auStart >= 0)
                {
                    units.Add(new Unit(auStart, pos - auStart, auKey));
                }

                auStart = pos;
                auHasVcl = false;
                auKey = false;
            }

            auHasVcl |= vcl;
            auKey |= key;
            pos = next;
            scLen = nextScLen;
        }

        if (auStart >= 0 && auHasVcl)
        {
            units.Add(new Unit(auStart, data.Length - auStart, auKey));
        }

        return units;
    }

    private static void Classify(ReadOnlySpan<byte> nal, MirrorCodec codec, out bool vcl, out bool firstSlice, out bool key)
    {
        if (codec == MirrorCodec.Hevc)
        {
            int type = (nal[0] >> 1) & 0x3F;
            vcl = type < 32;
            firstSlice = vcl && (nal[2] & 0x80) != 0; // first_slice_segment_in_pic_flag
            key = type is >= 16 and <= 21;
        }
        else
        {
            int type = nal[0] & 0x1F;
            vcl = type is >= 1 and <= 5;
            firstSlice = vcl && (nal[1] & 0x80) != 0; // first_mb_in_slice == 0 (ue(v) "1")
            key = type == 5;
        }
    }

    /// <summary>Index of the next 00 00 01 or 00 00 00 01 at or after <paramref name="from"/>, or -1.</summary>
    private static int NextStartCode(ReadOnlySpan<byte> data, int from, out int length)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    bool four = i > from && data[i - 1] == 0;
                    length = four ? 4 : 3;
                    return four ? i - 1 : i;
                }
            }
        }

        length = 0;
        return -1;
    }
}
#endif
