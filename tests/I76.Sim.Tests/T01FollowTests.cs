using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// T01: once the fireworks stand is down the script cuts to a shot of the road and orders the player's car to follow Taurus
/// (30 m behind) while the player has no control. A tester found the car left standing and the player far behind Taurus when the
/// control came back. Played with a driver that follows Taurus to Seagraves and has the stand destroyed as a round would.
/// </summary>
public class T01FollowTests
{
    [Theory]
    [InlineData(null, 20)]
    [InlineData(null, 28)]
    [InlineData(null, 30)]
    [InlineData(null, 60)]
    [InlineData("vppt01", 20)]
    public void ThePlayerFollowsTaurusThroughTheSceneAfterTheFireworksStand(string? car, int hz)
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t01.msn")!), asm, car, rand: new I76.Sim.Rng.CrtRand(1));
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(hz));
        s.AddDriver(setup.World.Player!);
        var h = setup.Script!.Host;
        var fireworks = setup.Entities.First(e => e.Name == "bcfirew1");
        var taurus = setup.Entities.First(e => e.Name == "t01js01");
        var player = setup.World.Player!;
        var dm = (I76.Sim.Damage.DamageModel)setup.Damage!;
        var stands = (I76.Sim.Collision.IHasStatics)setup.Ground;
        bool was = false;
        var phases = new HashSet<int>();
        Vector3 landed = default; float teleportedFrom = 0; Vector3 landedTaurus = default; float landedSpeed = 0;
        Vector3 prev = default;
        float deadAt = -1, sceneAt = -1, moved = 0, endGap = -1, endSpeed = 0;
        Vector3 from = default;
        while (setup.Script.Result is null && h.Now < 400 && endGap < 0)
        {
            var hd = player.Health!;
            Array.Copy(hd.ArmourMax, hd.Armour, 4); Array.Copy(hd.ChassisMax, hd.Chassis, 4);
            var input = new CarInput();
            if (h.Now >= 21.3f && !h.View.Active)
            {
                // objective 2 open: drive at the stand and have it blown up from 55 m; then 15 m behind Taurus
                bool stand = !fireworks.Dead && (h.Objectives.Flags[1] & (Objectives.Hidden | Objectives.Succeeded)) == 0;
                var goal = stand ? fireworks.Static!.Centre : taurus.Position - taurus.Rot.Forward * 15;
                var to = goal - player.Pos.ToVector3();
                float err = MathF.Atan2(to.X, to.Z) - MathF.Atan2(player.Rot.Forward.X, player.Rot.Forward.Z);
                while (err > MathF.PI) err -= 2 * MathF.PI;
                while (err < -MathF.PI) err += 2 * MathF.PI;
                float dist = new Vector2(to.X, to.Z).Length();
                float want = stand ? (dist > 45 ? 8 : 0) : taurus.Vehicle!.Speed + System.Math.Clamp(dist * 0.5f, -15f, 20f);
                input.Buttons = CarButtons.Analog;
                input.Steer = (short)(System.Math.Clamp(-err * 1.5f, -1f, 1f) * 32767);
                input.Throttle = (short)(System.Math.Clamp((want - player.Speed) * 0.3f, -1f, 1f) * 32767);
                if (stand && dist < 55 && stands.World.Objects.FirstOrDefault(x => x.Name == "bcfirew1") is { } so) dm.DestroyStructure(so);
            }
            s.Tick([input]);
            setup.Combat.Events.Clear(); setup.Damage!.Events.Clear();
            if (deadAt < 0 && fireworks.Dead) deadAt = h.Now;
            bool on = h.View.Active;
            if (deadAt >= 0 && on && !was) { sceneAt = h.Now; from = player.Pos.ToVector3(); }
            if (sceneAt >= 0 && !on && was) { moved = (player.Pos.ToVector3() - from).Length(); endGap = (player.Pos.ToVector3() - taurus.Position).Length(); endSpeed = player.Speed; }
            was = on;
            // the routine at instruction 2296..2475 (T01.fsm): A goto 2346, B follow -10 2372, T waits for the stand and the radio 2404, C follow -30 2431
            foreach (var th in setup.Script.Vm!.Threads) if (!th.Finished && th.Pc is 2346 or 2372 or 2404 or 2431) phases.Add(th.Pc);
            if (deadAt >= 0 && landed == default && (player.Pos.ToVector3() - prev).Length() > 25) { landed = player.Pos.ToVector3(); teleportedFrom = (player.Pos.ToVector3() - prev).Length(); landedTaurus = taurus.Position; landedSpeed = player.Speed; }
            prev = player.Pos.ToVector3();
        }
        Assert.True(phases.SetEquals([2346, 2372, 2404, 2431]), "the script routine went through its goto, follow, wait and follow-behind phases: " + string.Join(",", phases.Order()));
        Assert.True(teleportedFrom > 25 && (landed - new Vector3(3890, landed.Y, 38755)).Length() < 10, $"the scene teleported the player to its path point (landed {landed.X:0},{landed.Z:0} from {teleportedFrom:0} m away)");
        Assert.True(deadAt > 0, "the stand went");
        Assert.True(sceneAt > 0, $"a scene followed the stand (mission second {h.Now:0}, result {setup.Script.Result})");
        Assert.True(endGap >= 0, "the scene ended");
        Assert.True(moved > 150, $"the player moved {moved:0} m in the scene");
        // measured in the original (a driver that follows Taurus to the diner and fires at the stand from 56 m):
        // the scene teleports Taurus to (3885, 38775) at 23 m/s and the player to (3890, 38755) at 20 m/s, both the script's own numbers;
        // ten seconds on the player is 47 to 65 m behind Taurus (two runs, the trip car and the stock one) at 28 m/s against his 29
        Assert.True((landedTaurus - new Vector3(3885, landedTaurus.Y, 38775)).Length() < 6, $"Taurus landed at {landedTaurus.X:0},{landedTaurus.Z:0}");
        Assert.InRange(landedSpeed, 19f, 21f);
        Assert.InRange(endSpeed, 24f, 33f);
        Assert.InRange(endGap, 25f, 90f);
    }
}
