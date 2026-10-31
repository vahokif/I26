using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// T16's Fort Davis gate tanks (t16st01, hidden until the player is within 200, then `guard`). Their cannons are turrets,
/// as on the 1997 CD, so a tank fires at the player off its hull too. The 1998 build (GOG, Nitro)
/// left them fixed: measured in that game, the player left standing 100 m ahead of the west tank died in about 5 s, and
/// 140 m off its line nothing happened in 35 s.
/// </summary>
public class FortTankTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (MissionSetup Setup, SimSession Session) T16(float x, float z, float yawDeg)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t16.msn")!), asm);
        var s = new SimSession(setup);
        var p = setup.World.Player!;
        s.AddDriver(p);
        setup.World.Ai.Remove(p);   // nobody else at the wheel (the intro's goto)
        float yaw = yawDeg * MathF.PI / 180f;
        var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        p.Pos = new Double3(x, setup.Ground.BareHeight(x, z) + 0.5f, z);
        p.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        p.Vel = default; p.AngVel = default;
        return (setup, s);
    }

    private static int TankShots(MissionSetup setup, SimSession s, float seconds)
    {
        int shots = 0;
        var input = new CarInput();
        // the intro's goto drives the player's car while the camera is the script's: it is held where it was put
        var p = setup.World.Player!;
        var (pos, rot) = (p.Pos, p.Rot);
        for (float t = 0; t < seconds; t += s.TickSeconds)
        {
            s.Tick([input]);
            (p.Pos, p.Rot, p.Vel, p.AngVel, p.Speed) = (pos, rot, default, default, 0);
            shots += setup.Combat.Events.OfType<ShotFired>().Count(e => setup.Entities.FirstOrDefault(en => en.Vehicle == e.Shooter)?.Name == "t16st01");
            setup.Combat.Events.Clear();
        }
        return shots;
    }

    [Fact]
    public void ATankFiresAtAPlayerAheadOfItsHullWithTheEngineOn()
    {
        if (InstallDir is null) return;
        var (setup, s) = T16(2914, 49531, 90);   // 100 m ahead of the west tank (2997, 49587), whose hull points at it
        Assert.True(TankShots(setup, s, 15) >= 3);
    }

    [Fact]
    public void ATankTurnsItsCannonOntoAPlayerOffItsHull()
    {
        if (InstallDir is null) return;
        var (setup, s) = T16(3000, 49450, 0);    // 140 m south of both, 55 degrees off the west tank's line (GOG: no shot in 35 s)
        Assert.True(TankShots(setup, s, 30) >= 3);
    }
}
