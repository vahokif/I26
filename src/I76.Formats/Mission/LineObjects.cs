using System.Numerics;

namespace I76.Formats.Mission;

/// <summary>An LDEF polyline: a definition strung along points (fences, tyre walls).</summary>
public sealed record LineObject(string Name, byte[] Header, List<Vector3> Points);

/// <summary>
/// LDEF: LREV, then OBJ chunks. An OBJ payload is
/// a 0x64-byte header (name at 0, masked like ODEF names), a u32 point count
/// at 0x64, then xyz points. The engine sets each point onto the terrain
/// and gives every link a copy of the model's part, its local +Z along the link (the third row of the
/// object matrix). The part's own offset in its file is replaced: its centred mesh stands at the link's
/// midpoint, raised by the part box's half height so that its foot is on the line (afence4's 5 m barrier
/// on a 5.6 m link leaves 0.3 m at each end, and stands on the line, not beside it).
/// </summary>
public static class LineObjects
{
    public static List<LineObject> Read(MissionFile msn)
    {
        var o = new List<LineObject>();
        foreach (var c in msn.File.Chunks.Where(c => c.Tag == "LDEF"))
        {
            var body = msn.Data.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            foreach (var sc in MissionFile.SubChunks(body))
            {
                if (sc.Tag != "OBJ\0" || sc.PayloadSize < 0x68) continue;
                var p = body.AsSpan(sc.PayloadOffset, sc.PayloadSize).ToArray();
                var name = new byte[8];
                for (int i = 0; i < 8; i++) name[i] = (byte)(p[i] & MissionFile.NameMask);
                int n = (int)Bin.U32(p, 0x64);
                var pts = new List<Vector3>();
                for (int i = 0; i < n && 0x68 + 12 * i + 12 <= p.Length; i++)
                    pts.Add(new Vector3(Bin.F32(p, 0x68 + 12 * i), Bin.F32(p, 0x6c + 12 * i), Bin.F32(p, 0x70 + 12 * i)));
                o.Add(new LineObject(Bin.CStr(name), p[..0x64], pts));
            }
        }
        return o;
    }

    /// <summary>
    /// One placement per link, in the game frame: rotation as the engine stores
    /// it (rows are the object's axes, +Z along the link) and the model's origin, with
    /// points on <paramref name="height"/>. <paramref name="partOffset"/> is the model's part offset (the
    /// assembled model draws its mesh there) and <paramref name="lift"/> the part box's depth below its centre:
    /// the mesh's centre goes to the link's midpoint, raised by <paramref name="lift"/> in world y.
    /// </summary>
    public static IEnumerable<(float[] Rotation, Vector3 Position)> Placements(LineObject line, Func<double, double, double> height,
        Vector3 partOffset = default, float lift = 0)
    {
        var pts = line.Points.Select(p => new Vector3(p.X, (float)height(p.X, p.Z), p.Z)).ToList();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var f = pts[i + 1] - pts[i];
            if (f.LengthSquared() < 1e-8f) continue;
            var z = Vector3.Normalize(f);
            var x = Vector3.Cross(Vector3.UnitY, z);
            if (x.LengthSquared() < 1e-8f) x = Vector3.UnitX;
            x = Vector3.Normalize(x);
            var y = Vector3.Cross(z, x);
            var at = (pts[i] + pts[i + 1]) * 0.5f + new Vector3(0, lift, 0) - (x * partOffset.X + y * partOffset.Y + z * partOffset.Z);
            yield return (new[] { x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z }, at);
        }
    }
}
