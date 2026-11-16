using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A stock object placed with another class id (the ODEF type a map gives it: Struct1 2, Struct2 3, Sign 4, Ramp 0xb,
/// Bridge 0xc): the original decides collisions and damage by each struck node's own type (its SGEO record's), and only
/// the root, the placement itself, takes the placement's type. Measured in the original game where a stock map does it
/// (T15's barn placed as type 3, M10's billboards placed as type 4); the other placements are the same object in a
/// training map, placed by the test.
/// </summary>
public class ClassIdTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>A placement of a stock object with the given class id, its own raw record as the ODEF loader reads it.</summary>
    internal static Placement Place(ModelAssembler asm, string name, uint type, float x, float z, float yawDeg = 0)
    {
        var raw = new byte[0x78];
        BitConverter.GetBytes(type).CopyTo(raw, 0x5c);
        float a = yawDeg * MathF.PI / 180;
        float[] rot = [MathF.Cos(a), 0, -MathF.Sin(a), 0, 1, 0, MathF.Sin(a), 0, MathF.Cos(a)];
        return new Placement(new PlacedObject(name, 0, rot, new Vector3(x, 0, z), raw), asm.ResolveDefinition(name));
    }

    internal static (MissionSetup Setup, SimSession Session) Build(string mission, params (string Name, uint Type, float X, float Z, float Yaw)[] extra)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", mission)!);
        foreach (var (name, type, x, z, yaw) in extra) w.Placements.Add(Place(asm, name, type, x, z, yaw));
        var setup = MissionSetup.Build(vfs, w, asm, script: false, rand: new I76.Sim.Rng.CrtRand(1));
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        return (setup, s);
    }

    /// <summary>
    /// A run-up as the measurement in the original made it: the player's car put down at <paramref name="from"/> facing <paramref name="dir"/>, held at
    /// <paramref name="speed"/> along it until its first contact with scenery, then left alone for 2 s. Returns the
    /// first object hit (null for none), the farthest it got along dir from the start, and the armour it lost.
    /// </summary>
    internal static (string? Hit, float Reached, int ArmourLost) Ram(MissionSetup setup, SimSession s, Vector2 from, Vector2 dir, float speed = 15, int ticks = 120)
    {
        var player = setup.World.Player!;
        var hp = player.Health!;
        var fwd = Vector3.Normalize(new Vector3(dir.X, 0, dir.Y));
        player.Pos = new Double3(from.X, setup.Ground.BareHeight(from.X, from.Y) + 0.5f, from.Y);
        player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        player.Vel = default;
        player.AngVel = default;
        for (int i = 0; i < 10; i++) s.Tick([default]);   // set down
        int armour = hp.Armour.Sum() + hp.Chassis.Sum();
        string? hit = null;
        int left = 40;
        float reached = 0;
        for (int i = 0; i < ticks && left > 0; i++)
        {
            if (hit is null)
            {
                player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
                player.Vel = new Vector3(fwd.X * speed, player.Vel.Y, fwd.Z * speed);
                player.Speed = speed;
                player.AngVel = default;
            }
            else left--;
            s.Tick([default]);
            foreach (var h in setup.World.Step.ObjectHits) if (h.Self == player) hit ??= h.Name;
            var p = player.Pos.ToVector3();
            reached = MathF.Max(reached, Vector2.Dot(new Vector2(p.X, p.Z) - from, Vector2.Normalize(dir)));
        }
        return (hit, reached, armour - hp.Armour.Sum() - hp.Chassis.Sum());
    }

    /// <summary>The placement of that name standing nearest the point (the map may place the same object elsewhere).</summary>
    internal static StaticObject At(MissionSetup setup, string name, float x, float z) =>
        setup.Statics.Objects.Where(o => o.Name == name).MinBy(o => new Vector2(o.Centre.X - x, o.Centre.Z - z).Length())!;

    private static void Kill(MissionSetup setup, StaticObject o, int node)
    {
        var p = new DamagePacket { Mask = 8 };
        p.Ch[3] = 1_000_000;
        setup.Damage.StructureSlot(o, node, p);
    }

    /// <summary>
    /// T15's barn is placed as type 3 (Struct2); its node is type 2. In the original (a run-up at 15 u/s from 35 m east,
    /// the node killed by a 1,000,000-point impact on the first contact, then the same run-up again) the dead node HB1_BLD2 (flags 0x200) still meets the car at the same place and throws it back at about
    /// 4 u/s: the wreck stays an obstacle.
    /// </summary>
    [Fact]
    public void T15sBarnPlacedAsType3LeavesASolidWreck()
    {
        if (InstallDir is null) return;
        var (setup, s) = Build("t15.msn");
        var barn = setup.Statics.Objects.Single(o => o.Name == "bhbarn2");
        Assert.Equal(3u, barn.Type);
        int bld = barn.Nodes.FindIndex(n => n.Name.EndsWith("BLD2", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2u, barn.Nodes[bld].Type);
        Kill(setup, barn, bld);
        Assert.True(barn.Nodes[bld].Destroyed);
        var (hit, reached, _) = Ram(setup, s, new Vector2(3465, 43705), new Vector2(-1, 0));
        Assert.Equal("bhbarn2", hit);
        Assert.InRange(reached, 20f, 37f);   // stopped at the wreck: its far side is 41 m off
    }

    /// <summary>
    /// The same building placed with each class id that keeps it a solid structure, destroyed: the wreck (its type-2 'A'
    /// node) stops the car whether the placement is Struct1 or Struct2.
    /// </summary>
    [Theory]
    [InlineData("bcbank1", 2u)]
    [InlineData("bcbank1", 3u)]
    [InlineData("bhbarn1", 3u)]
    [InlineData("bcgarag1", 3u)]
    public void ADestroyedBuildingsWreckIsSolidWhateverItsStructClass(string name, uint type)
    {
        if (InstallDir is null) return;
        var (setup, s) = Build("t01.msn", (name, type, 4100, 36700, 0));
        var o = At(setup, name, 4100, 36700);
        setup.Damage.DestroyStructure(o);
        Assert.True(o.Solid);
        var (hit, reached, _) = Ram(setup, s, new Vector2(o.Centre.X + 45, o.Centre.Z), new Vector2(-1, 0));
        Assert.Equal(name, hit);
        Assert.True(reached < 45, $"{name} as type {type}: the car got {reached:0.0} m, past the wreck's centre");
    }

    /// <summary>
    /// A type-3 node's remains give the car no response (the car's collide callback skips a dead type-3 node): a
    /// saguaro's, whose nodes are all type 3, are driven through. They still stop rounds.
    /// </summary>
    [Fact]
    public void ADeadType3NodeIsUnfelt()
    {
        if (InstallDir is null) return;
        var (setup, _) = Build("t01.msn", ("nsaguar1", 3u, 4100, 36700, 0), ("bcbank1", 3u, 4200, 36700, 0));
        var cactus = At(setup, "nsaguar1", 4100, 36700);
        var bank = At(setup, "bcbank1", 4200, 36700);
        setup.Damage.DestroyStructure(cactus);
        setup.Damage.DestroyStructure(bank);
        Assert.All(Enumerable.Range(0, cactus.Nodes.Count).Where(i => cactus.Nodes[i].Destroyed), i => Assert.True(cactus.Unfelt(i)));
        Assert.All(Enumerable.Range(0, bank.Nodes.Count).Where(i => bank.Nodes[i].Type == 2), i => Assert.False(bank.Unfelt(i)));
    }

    /// <summary>
    /// The car's own damage goes by the struck node's type: ramming a type-2 node costs armour even when the building is
    /// placed as type 3, ramming a type-3 node (a saguaro's trunk) never does.
    /// </summary>
    [Fact]
    public void TheCarIsDamagedByAType2NodeOfAType3Placement()
    {
        if (InstallDir is null) return;
        var (setup, s) = Build("t01.msn", ("bcbank1", 3u, 4100, 36700, 0));
        var (hit, _, lost) = Ram(setup, s, new Vector2(4100 + 45, 36700), new Vector2(-1, 0), speed: 25);
        Assert.Equal("bcbank1", hit);
        Assert.True(lost > 0, "the ram cost no armour");
    }
}

/// <summary>
/// The Sign class id (type 4) on something that is not a sign. The root takes the placement's type 4, but its nodes keep
/// their own: a billboard's or a building's are type 2. Measured in the original on M10, whose
/// billboards are placed as type 4: a car run into aobilbd2 at 15 u/s meets node AO1_SGN2 (type 2, its class the root
/// type's default sphere), is thrown back at 4 u/s, and the node goes to the structure slot (HP 50 to 43: the impact of
/// 29 quartered as type 2's are); the sign slot is never called and nothing is thrown. A splash reaches the root, the
/// sign's slot, which throws the whole placement.
/// </summary>
public class SignClassTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void M10sBillboardPlacedAsASignStopsTheCarAndStands()
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("m10.msn");
        var board = ClassIdTests.At(setup, "aobilbd2", 2025, 47980);
        Assert.Equal(4u, board.Type);
        Assert.False(board.SignPlacement);
        Assert.All(board.Nodes, n => Assert.Equal(2u, n.Type));
        // the measured run-up: from 30 m in front of the board's face, along its normal
        var (hit, reached, _) = ClassIdTests.Ram(setup, s, new Vector2(2000.9f, 47961.5f), new Vector2(0.69f, 0.72f));
        Assert.Equal("aobilbd2", hit);
        Assert.True(reached < 30, $"the car got {reached:0.0} m: through the board");
        Assert.False(board.Destroyed);
        Assert.True(board.Solid);
        Assert.DoesNotContain(setup.Damage.Events, e => e is I76.Sim.Damage.SignKnocked);
        Assert.Contains(board.Nodes, n => n.Hp < n.MaxHp);
    }

    [Fact]
    public void ASplashThrowsABillboardPlacedAsASign()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("m10.msn");
        var board = ClassIdTests.At(setup, "aobilbd2", 2025, 47980);
        var p = new DamagePacket { Mask = 2 };
        p.Ch[1] = 15;
        setup.Damage.Apply(board, p, Vector3.Zero, -1);
        Assert.True(board.Knocked);
        Assert.False(board.Solid);
        Assert.Contains(setup.Damage.Events, e => e is I76.Sim.Damage.SignKnocked k && k.Object == board);
    }

    /// <summary>A building given the Sign class id is no paper: the car meets it as a building, and it is not thrown.</summary>
    [Fact]
    public void ABuildingPlacedAsASignIsSolid()
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("t01.msn", ("bcbank1", 4u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcbank1", 4100, 36700);
        Assert.Equal(4u, o.Type);
        var (hit, reached, _) = ClassIdTests.Ram(setup, s, new Vector2(o.Centre.X + 45, o.Centre.Z), new Vector2(-1, 0));
        Assert.Equal("bcbank1", hit);
        Assert.True(reached < 45, $"the car got {reached:0.0} m, past the bank's centre");
        Assert.False(o.Knocked);
        Assert.True(o.Solid);
    }

    /// <summary>A road sign is still the paper it always was: driven through, knocked off.</summary>
    [Fact]
    public void ARoadSignIsStillKnockedOff()
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("t01.msn", ("sstop_1", 4u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sstop_1", 4100, 36700);
        Assert.True(o.SignPlacement);
        var (hit, reached, _) = ClassIdTests.Ram(setup, s, new Vector2(o.Centre.X + 30, o.Centre.Z), new Vector2(-1, 0));
        Assert.Equal("sstop_1", hit);
        Assert.True(reached > 35, $"the car got {reached:0.0} m: stopped by a sign");
        Assert.True(o.Destroyed);
        Assert.False(o.Solid);
    }
}

/// <summary>
/// A road sign (every node its own type 4) given another class id: Struct1, Struct2, Ramp or Bridge (a community mission
/// maker's report: ten of ten signs so placed stood through any number of mortar rounds). Measured in the original (stock
/// sdxin_1 and sl50x_1 placed as types 2, 3, 0xb and 0xc in a test map, explosions set off beside them): a splash reaches
/// the root, whose type is the placement's, so the structure slot takes it from the root's 400 (25 a blast: sixteen leave
/// 0, the seventeenth destroys it); every node is marked dead and swapped to its destroyed slot's record, which for a sign
/// is empty, so nothing of it is drawn. Placed as a Sign, the first splash throws it. Placed as an Intersection it has no
/// collider at all. A sign node's own hits go to the sign's slot whatever the placement: an explosive round or a car's
/// impact throws it, a bullet does nothing.
/// </summary>
public class SignObjectClassTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>What is drawn of the placement: each dead node's destroyed geometry, the live ones' own.</summary>
    private static List<string> Drawn(StaticObject o) =>
        new ModelAssembler(new AssetVfs(new GameInstall(InstallDir!)))
        {
            DestroyedNode = name => o.Nodes.FindIndex(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? o.Nodes[i].Destroyed : o.Destroyed,
        }.Assemble(o.Name + ".sdf")?.Parts.Select(p => p.NodeName).ToList() ?? [];

    /// <summary>The HE mortar's blast (x1_gren1: 95 over 25 m): four leave a sign of any structure class standing, the fifth takes it all.</summary>
    [Theory]
    [InlineData("sdxin_1", 2u)]
    [InlineData("sdxin_1", 3u)]
    [InlineData("sdxin_1", 0xbu)]
    [InlineData("sdxin_1", 0xcu)]
    [InlineData("seair_1", 2u)]
    [InlineData("seair_1", 0xcu)]
    public void FiveMortarBlastsTakeARoadSignPlacedAsAStructure(string name, uint type)
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", (name, type, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, name, 4100, 36700);
        Assert.Equal(type, o.Type);
        Assert.All(o.Nodes, n => Assert.Equal(4u, n.Type));
        int parts = Drawn(o).Count;
        Assert.True(parts > 0);
        var at = o.Centre + new Vector3(10, 0, 0);
        for (int i = 0; i < 4; i++) setup.Combat.Explode("x1_gren1", at, null);
        Assert.False(o.Destroyed);
        Assert.Equal(400 - 4 * 95, o.Hp);
        Assert.Equal(parts, Drawn(o).Count);
        setup.Combat.Explode("x1_gren1", at, null);
        Assert.True(o.Destroyed);
        Assert.All(o.Nodes, n => Assert.True(n.Destroyed));
        Assert.Empty(Drawn(o));
        Assert.False(o.Solid);
        Assert.DoesNotContain(setup.Damage.Events, e => e is I76.Sim.Damage.SignKnocked);
    }

    [Fact]
    public void OneMortarBlastThrowsARoadSignPlacedAsASign()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("sdxin_1", 4u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sdxin_1", 4100, 36700);
        setup.Combat.Explode("x1_gren1", o.Centre + new Vector3(10, 0, 0), null);
        Assert.True(o.Knocked);
        Assert.Contains(setup.Damage.Events, e => e is I76.Sim.Damage.SignKnocked k && k.Object == o);
    }

    [Fact]
    public void ARoadSignPlacedAsAnIntersectionHasNoCollider()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("sdxin_1", 0x50u, 4100, 36700, 0));
        Assert.DoesNotContain(setup.Statics.Objects, x => x.Name == "sdxin_1" && new Vector2(x.Centre.X - 4100, x.Centre.Z - 36700).Length() < 10);
    }

    /// <summary>
    /// A car run at a sign's face. Struct1's default collider is a box per node: the car meets the post, is let through
    /// and throws the sign (measured: SD1XINP1 met at 15 u/s, the sign's slot given the impact, the whole placement thrown).
    /// Struct2's, Ramp's and Bridge's is the nodes' mesh, which only the car's four corners are tested against: a car
    /// square on to a sign passes between them and nothing happens (measured: no contact for any of the three).
    /// </summary>
    [Theory]
    [InlineData(2u, true)]
    [InlineData(3u, false)]
    [InlineData(0xbu, false)]
    [InlineData(0xcu, false)]
    public void ACarAtARoadSignPlacedAsAStructure(uint type, bool thrown)
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("t01.msn", ("sdxin_1", type, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sdxin_1", 4100, 36700);
        // at the board's face (the sign stands in the x-y plane of its placement)
        var (hit, reached, _) = ClassIdTests.Ram(setup, s, new Vector2(o.Centre.X, o.Centre.Z + 30), new Vector2(0, -1));
        Assert.True(reached > 35, $"the car got {reached:0.0} m: stopped by the sign");
        Assert.Equal(thrown ? "sdxin_1" : null, hit);
        Assert.Equal(thrown, o.Destroyed);
        Assert.Equal(!thrown, o.Solid);
        Assert.Equal(thrown, setup.Damage.Events.Any(e => e is I76.Sim.Damage.SignKnocked k && k.Object == o));
    }

    /// <summary>A round on a sign node of any structure class: a bullet leaves it, an explosive round throws it.</summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(0xbu)]
    [InlineData(0xcu)]
    public void ARoundOnASignNodeGoesToTheSignsSlot(uint type)
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("sdxin_1", type, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sdxin_1", 4100, 36700);
        var bullet = new DamagePacket { Mask = 1 };
        bullet.Ch[0] = 50;
        setup.Damage.Apply(o, bullet, Vector3.Zero, 0);
        Assert.False(o.Destroyed);
        var rocket = new DamagePacket { Mask = 2 };
        rocket.Ch[1] = 120;
        setup.Damage.Apply(o, rocket, Vector3.Zero, 0);
        Assert.True(o.Destroyed);
        Assert.Contains(setup.Damage.Events, e => e is I76.Sim.Damage.SignKnocked k && k.Object == o);
    }
}
