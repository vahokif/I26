using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Input;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// #165: in every car's interior (both games), the free head held to <see cref="CockpitReach"/> never brings the cut-off lower
/// edge of an arm's quad (GUNL, GUNR: the .45s, raised or at rest) or of the steering wheel's (SWHL, with its hands) above the
/// view's lower border, at the original's height of view, in windows from 4:3 to 32:9, at every degree of the free look's yaw.
/// The eye is the cockpit's (the VLOC the game seats it at); the head's roll leans 2° into a full side turn as the cockpit's does.
/// </summary>
public class CockpitReachTests
{
    private static IEnumerable<AssetVfs> Installs()
    {
        if (TestInstall.Dir is not null) yield return TestInstall.Vfs;
        if (NitroFactAttribute.Dir is not null) yield return new AssetVfs(new GameInstall(NitroFactAttribute.Dir));
    }

    /// <summary>The cut edges to keep out of view, relative to the eye (the game's axes: x right, y up, z forward).</summary>
    private static List<Vector3> CutEdges(AssetVfs vfs, string car)
    {
        var model = new ModelAssembler(vfs) { SkipParts = [] }.Assemble(car);
        var interior = new ModelAssembler(vfs) { Detail = '5' }.Assemble(car);
        if (model is null || interior is null) return [];
        var vloc = model.Locations.FirstOrDefault(l => l.Tag == "VLOC" && l.Index == 40) ?? model.Locations.FirstOrDefault(l => l.Tag == "VLOC");
        var eye = vloc is null ? new Vector3(-0.4f, 1.25f, 0) : new Vector3(vloc.Transform.Translation[0], vloc.Transform.Translation[1], vloc.Transform.Translation[2]);
        var pts = new List<Vector3>();
        foreach (var p in interior.Parts.Where(p => p.NodeName.Length >= 8 && p.NodeName[4..8].ToUpperInvariant() is "GUNL" or "GUNR" or "SWHL"))
        {
            var v = p.Mesh.Vertices.Select(p.ToModel.Apply).Select(x => x - eye).ToArray();
            if (v.Length == 0) continue;
            bool wheel = p.NodeName[4..8].Equals("SWHL", StringComparison.OrdinalIgnoreCase);
            if (!wheel && v.Average(x => x.Length()) > 0.6f) continue;   // a helicopter's crew: not arms beside the eye
            float y = v.Min(x => x.Y);
            // the arms' quads stand along the car (their lower edge runs fore and aft), the wheel's across it
            var (a, b) = wheel
                ? (new Vector3(v.Min(x => x.X), y, v.Average(x => x.Z)), new Vector3(v.Max(x => x.X), y, v.Average(x => x.Z)))
                : (new Vector3(v.Average(x => x.X), y, v.Min(x => x.Z)), new Vector3(v.Average(x => x.X), y, v.Max(x => x.Z)));
            for (int i = 0; i <= 20; i++) pts.Add(Vector3.Lerp(a, b, i / 20f));
        }
        return pts;
    }

    private static bool InView(Vector3 q, float yawDeg, float pitchDeg, float tanV, float tanH)
    {
        float y = yawDeg * MathF.PI / 180, p = pitchDeg * MathF.PI / 180, r = Math.Clamp(yawDeg / 90f, -1, 1) * 2 * MathF.PI / 180;
        var fwd = new Vector3(MathF.Sin(y) * MathF.Cos(p), -MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
        var right0 = new Vector3(MathF.Cos(y), 0, -MathF.Sin(y));
        var up0 = Vector3.Cross(fwd, right0);
        var right = right0 * MathF.Cos(r) + up0 * MathF.Sin(r);
        var up = up0 * MathF.Cos(r) - right0 * MathF.Sin(r);
        float d = Vector3.Dot(q, fwd);
        if (d < 0.05f) return false;
        float u = Vector3.Dot(q, up) / d;
        return MathF.Abs(Vector3.Dot(q, right)) / d <= tanH && u >= -tanV && u <= tanV;
    }

    [InstallFact]
    public void No_cars_arm_or_wheel_edge_comes_into_view()
    {
        float tanV = MathF.Tan(CockpitReach.OriginalHeight * MathF.PI / 360);
        int cars = 0;
        var seen = new List<string>();
        foreach (var vfs in Installs())
            foreach (var car in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) && !n.Contains("turr", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var pts = CutEdges(vfs, car);
                if (pts.Count == 0) continue;
                cars++;
                for (int yaw = -160; yaw <= 160; yaw++)
                {
                    float pitch = CockpitReach.MaxPitch(yaw, CockpitReach.OriginalHeight);
                    foreach (var aspect in new[] { 4f / 3, 16f / 9, 43f / 18, 32f / 9 })
                        if (pts.FirstOrDefault(q => InView(q, yaw, pitch, tanV, tanV * aspect)) is var q && q != default)
                        {
                            seen.Add($"{car} yaw {yaw} pitch {pitch:0.0} aspect {aspect:0.00}: {q}");
                            break;
                        }
                }
            }
        Assert.True(cars > 40, $"only {cars} cars with arms or a wheel");
        Assert.Empty(seen);
    }

    /// <summary>The check above sees what it should: the old reach (25° under the −13° rest at every yaw) shows the Piranha's arms and wheel.</summary>
    [InstallFact]
    public void The_old_reach_showed_them()
    {
        var pts = CutEdges(TestInstall.Vfs, "vppirna1.vcf");
        float tanV = MathF.Tan(CockpitReach.OriginalHeight * MathF.PI / 360);
        foreach (var yaw in new[] { -60, -45, 0, 45, 90, 120 })
            Assert.Contains(pts, q => InView(q, yaw, FreeLook.Rest + FreeLook.MaxDown, tanV, tanV * 43 / 18));
    }
}
