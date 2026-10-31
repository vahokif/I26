using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A bridge's drive-on surface is its type-0xd deck node's, also when the SDF lists every node as a
/// root of its own (bebridg2, T05's bridge over the cliff): the placement's own root alone takes the placement's type.
/// </summary>
public class BridgeDeckTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void T05BridgeCarriesTheCar()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var s = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t05.msn")!), asm, script: false);
        var bridge = s.Statics.Objects.Single(o => o.Name == "bebridg2");
        Assert.NotEmpty(bridge.Surfaces);
        var deck = bridge.Deck!;
        // walk along the deck's long axis: the ground a car would drive on is the deck, well above the cliff's floor
        var axis = (deck.Max.X - deck.Min.X) > (deck.Max.Z - deck.Min.Z) ? Vector3.UnitX : Vector3.UnitZ;
        var mid = deck.Origin + deck.Rot.ToWorld((deck.Min + deck.Max) * 0.5f);
        var along = deck.Rot.ToWorld(axis);
        float half = System.Math.Abs(Vector3.Dot(deck.Max - deck.Min, axis)) * 0.5f;
        int carried = 0;
        for (float t = -half * 0.8f; t <= half * 0.8f; t += 2f)
        {
            var p = mid + along * t;
            var (h, _) = s.Ground.Query(p.X, p.Y + 1.5f, p.Z);
            var bare = s.Ground.BareHeight(p.X, p.Z);
            if (h > bare + 3f && System.Math.Abs(h - mid.Y) < 4f) carried++;
        }
        Assert.True(carried > 5, $"only {carried} samples on the deck");
    }
}
