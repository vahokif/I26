using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The modern look's round wheel (SmoothWheel) against the .wdf mesh it stands in for, on every stock car of the base
/// game and the Nitro Pack: about the same hub, no larger than the original reaches, and its rim on the texture's
/// own tyre.
/// </summary>
[Collection("AddonFiles")]
public class SmoothWheelTests
{
    private static IEnumerable<AssetVfs> Installs()
    {
        if (TestInstall.Dir is not null) yield return TestInstall.Vfs;
        if (NitroFactAttribute.Dir is not null) yield return new AssetVfs(new GameInstall(NitroFactAttribute.Dir));
    }

    [InstallFact]
    public void EveryRoundWheelIsFittedToItsHubAndTexture()
    {
        int fitted = 0, blown = 0;
        foreach (var vfs in Installs())
        {
            var res = new TextureResolver(vfs);
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                AssembledModel? m; try { m = new ModelAssembler(vfs).Assemble(n); } catch { continue; }
                if (m is null) continue;
                foreach (var (slot, w) in m.WheelGeos)
                    foreach (var geo in new[] { w.Intact, w.Blown })
                    {
                        if (geo is null || !Wheels.RollsAboutHub(geo, w.Node)) continue;
                        var face = geo.Faces.FirstOrDefault(f => f.Texture.Length > 0);
                        if (face is null || res.ResolveFace(face.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is not { } mem || res.Load(mem) is not { } tex) continue;
                        if (SmoothWheel.Build(geo, w.Node, tex) is not { } s) continue;
                        var vs = geo.Vertices.Select(w.Node.Apply).ToArray();
                        float y0 = vs.Min(p => p.Y), y1 = vs.Max(p => p.Y), z0 = vs.Min(p => p.Z), z1 = vs.Max(p => p.Z);
                        string who = $"{n} slot {slot} {mem}";
                        // the rim lies on the original's own outer face, inside the box the original spans (a little over for the texture's edge)
                        foreach (var p in s.Rim)
                        {
                            Assert.InRange(p.Y, y0 - 0.02f, y1 + 0.02f);
                            Assert.InRange(p.Z, z0 - 0.02f, z1 + 0.02f);
                        }
                        // every rim point is on an opaque texel; a circle's is near the keyed edge (keyed 4 texels further out) and inside
                        // what round_key keeps solid, so the face is not half covered at its rim
                        for (int k = 0; k < s.Rim.Length; k++)
                        {
                            var uv = s.RimUv[k]; var c = s.CentreUv;
                            var d = Vector2Norm(uv - c);
                            float px = uv.X * tex.Width, py = uv.Y * tex.Height;
                            Assert.True(TextureReader255 != tex.Indices[(int)py * tex.Width + (int)px], $"{who} (circle {s.Circle}): rim {k} of {s.Rim.Length} at {px:F1},{py:F1} is keyed");
                            float fromMid = (uv - new System.Numerics.Vector2(0.5f)).Length();
                            if (s.Circle) Assert.True(fromMid <= SmoothWheel.RoundKeySolid + 1e-4f, $"{who}: rim {k} reads at uv radius {fromMid:F3}, in round_key's fade");
                            float ox = px + d.X * 4f, oy = py + d.Y * 4f;
                            if (s.Circle && ox >= 0 && oy >= 0 && ox < tex.Width && oy < tex.Height)
                                Assert.True(TextureReader255 == tex.Indices[(int)oy * tex.Width + (int)ox], $"{who} (circle {s.Circle}): rim {k} at {px:F1},{py:F1} has no edge outside");
                        }
                        if (s.Circle)
                        {
                            // a circle about the hub, within 3 cm of the original's reach (the ground contact) and not past it
                            Assert.True(Math.Abs(s.Centre.Y) < 0.03f && Math.Abs(s.Centre.Z) < 0.03f, $"{who}: off the hub");
                            float reach = Math.Min(Math.Min(-y0, y1), Math.Min(-z0, z1));
                            Assert.True(s.Radius <= reach + 1e-3f && s.Radius > reach - 0.03f - 0.05f * reach, $"{who}: radius {s.Radius} against {reach}");
                            foreach (var p in s.Rim)
                                Assert.InRange(Math.Sqrt((p.Y - s.Centre.Y) * (p.Y - s.Centre.Y) + (p.Z - s.Centre.Z) * (p.Z - s.Centre.Z)), s.Radius - 1e-3, s.Radius + 1e-3);
                            fitted++;
                        }
                        else blown++;
                        Assert.True(Math.Abs(s.InnerX - s.OuterX) > 0.05f, who);
                    }
            }
        }
        Assert.True(fitted > 500, $"{fitted} round wheels fitted");
        Assert.True(blown > 100, $"{blown} blown outlines fitted");
    }

    private const byte TextureReader255 = 255;
    private static System.Numerics.Vector2 Vector2Norm(System.Numerics.Vector2 v) => v.LengthSquared() < 1e-12f ? System.Numerics.Vector2.UnitX : System.Numerics.Vector2.Normalize(v);
}
