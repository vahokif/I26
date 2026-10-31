using System.Buffers.Binary;
using System.Text;

namespace I76.Formats;

/// <summary>Little-endian field helpers shared by the readers.</summary>
public static class Bin
{
    /// <summary>A fixed-width string field, cut at the first NUL. A full-width field has none.</summary>
    public static string CStr(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.Latin1.GetString(n < 0 ? b : b[..n]);
    }

    public static string CStr(byte[] d, int at, int width) => CStr(d.AsSpan(at, Math.Min(width, d.Length - at)));

    public static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
    public static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
    public static short I16(byte[] d, int at) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at));
    public static ushort U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
    public static float F32(byte[] d, int at) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at));

    public static float[] F32s(byte[] d, int at, int n)
    {
        var r = new float[n];
        for (int i = 0; i < n; i++) r[i] = F32(d, at + 4 * i);
        return r;
    }

    public static string Latin1(ReadOnlySpan<byte> b) => Encoding.Latin1.GetString(b);
}
