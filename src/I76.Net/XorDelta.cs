namespace I76.Net;

/// <summary>
/// A byte string against a baseline the receiver already has, exactly (protocol 13's delta snapshots): the length, then
/// the bytes XORed with the baseline's (a missing baseline byte is 0) as runs: a token byte (high nibble: bytes the
/// same, low nibble: bytes that differ; 15 is followed by a varint of the rest), then the differing bytes' XORs; a 0
/// token ends it and the rest is the baseline's. The same bytes cost 2 bytes; a float whose low bytes moved costs
/// about one more than those bytes. Knows nothing of what the bytes mean, so a field added to an encoding needs nothing here.
/// </summary>
public static class XorDelta
{
    public static void Write(NetWriter w, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> baseline)
    {
        Span<byte> x = cur.Length <= 1024 ? stackalloc byte[cur.Length] : new byte[cur.Length];
        for (int k = 0; k < cur.Length; k++) x[k] = (byte)(cur[k] ^ (k < baseline.Length ? baseline[k] : 0));
        w.VarU((uint)cur.Length);
        int n = cur.Length, i = 0;
        while (true)
        {
            int same = 0;
            while (i + same < n && x[i + same] == 0) same++;
            if (i + same >= n) break;
            // a literal runs until two same bytes in a row (one inside a literal costs less than a new token)
            int lit = 0;
            while (i + same + lit < n && (x[i + same + lit] != 0 || (i + same + lit + 1 < n && x[i + same + lit + 1] != 0))) lit++;
            w.U8((byte)(Math.Min(same, 15) << 4 | Math.Min(lit, 15)));
            if (same >= 15) w.VarU((uint)(same - 15));
            if (lit >= 15) w.VarU((uint)(lit - 15));
            i += same;
            for (int k = 0; k < lit; k++) w.U8(x[i + k]);
            i += lit;
        }
        w.U8(0);
    }

    /// <summary>The bytes <see cref="Write"/> wrote against <paramref name="baseline"/>.</summary>
    public static byte[] Read(ref NetReader r, ReadOnlySpan<byte> baseline, int max = 4096)
    {
        uint n = r.VarU();
        if (n > max) throw new WireException($"delta of {n} bytes");
        var cur = new byte[n];
        int copy = Math.Min(baseline.Length, (int)n);
        baseline[..copy].CopyTo(cur);
        int i = 0;
        while (true)
        {
            byte t = r.U8();
            if (t == 0) break;
            int same = t >> 4, lit = t & 15;
            if (same == 15) same += (int)Math.Min(r.VarU(), int.MaxValue / 2);
            if (lit == 15) lit += (int)Math.Min(r.VarU(), int.MaxValue / 2);
            if (lit == 0 || (long)i + same + lit > n) throw new WireException("delta runs past its length");
            i += same;
            var x = r.Bytes(lit);
            for (int k = 0; k < lit; k++) cur[i + k] ^= x[k];
            i += lit;
        }
        return cur;
    }
}
