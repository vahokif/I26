using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Math;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// T11's gun emplacements stand at their file y (the loader creates type 1, 8 and 9 without the ground flag):
/// their collider must be where the model is, not on the terrain beneath it (mg2turr at 3407.5, 48327.5 is placed 2 m above the ground).
/// </summary>
public class EmplacementPlacementTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static MissionSetup T11()
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        return MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t11.msn")!), asm, script: false);
    }

    [Fact]
    public void EveryT11GunsColliderIsWhereItsModelStands()
    {
        if (InstallDir is null) return;
        var s = T11();
        var guns = s.Cars.Select(c => c.Item3).Where(v => v.IsEmplacement).ToList();
        Assert.Equal(20, guns.Count);
        foreach (var g in guns)
        {
            var so = g.Emplacement!.Collider!;
            Assert.InRange(so.Centre.Y, (float)g.Pos.Y, (float)g.Pos.Y + 2.5f);
        }
    }

    [Fact]
    public void EveryT11GunTakesDamageFromAMachineGunRoundAimedAtItsModel()
    {
        if (InstallDir is null) return;
        var s = T11();
        var player = s.World.Player!;
        var mg = player.Armory!.Weapons.Single(w => w.Def.Name == "50cal MG");
        var guns = s.Cars.Select(c => c.Item3).Where(v => v.IsEmplacement).ToList();
        foreach (var g in guns)
        {
            int before = g.Emplacement!.Hp;
            var at = g.Pos.ToVector3() + new Vector3(0, 1f, 0);
            // from the four sides: terrain in front of the muzzle may swallow one approach, never all
            foreach (var f in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX })
            {
                if (g.Emplacement.Hp < before) break;
                var right = Vector3.Cross(Vector3.UnitY, f);
                var start = at - f * 10f;
                var r = new Round
                {
                    Kind = mg.Def.Kind, Shooter = player, Def = mg.Def, Ord = mg.Def.Ordnance!, Pos = start, PrevPos = start,
                    Rot = new Rows(right, Vector3.Cross(f, right), f), LocalVel = new Vector3(0, 0, mg.Def.Speed), Life = 5f, Damage = mg.Damage,
                };
                s.Combat.Rounds.Add(r);
                for (int i = 0; i < 20 && s.Combat.Rounds.Contains(r); i++) s.Combat.Frame(0.036f);
            }
            Assert.True(g.Emplacement.Hp < before, $"gun at {g.Pos.X:F1},{g.Pos.Y:F1},{g.Pos.Z:F1} took nothing");
        }
    }
}
