using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>A bullet round's forward start offset (0..0.6 m) belongs to the shots that take the spread branch.</summary>
public class RoundStartOffsetTests
{
    private const float Dt = 102 / 2048f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>Where the first round of a single shot from a gun at the origin starts, before its first step.</summary>
    private static Vector3 Start(float spread, uint seed)
    {
        var world = new SimWorld(new Flat(), new CrtRand(seed));
        var car = new Vehicle { Pos = new(0, 2, 0) };
        world.Vehicles.Add(car);
        world.Player = car;
        var def = Armory.HandgunDef(1) with
        {
            Class = 2, Tier = 0, Kind = 1, Rate = 10, Burst = 1, Delay = 0, Speed = 300, Life = 2, Spread = spread, Ammo = 100,
            Ordnance = new OrdnanceDef(1, 300, 1, 0, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = 1, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        car.Armory = a;
        var c = new Combat(world, _ => null);
        w.Trigger = true;
        c.Frame(Dt);
        Assert.Single(c.Rounds);
        return c.Rounds[0].PrevPos - new Vector3(0, 2, 0);
    }

    [Fact]
    public void WithoutSpreadTheRoundStartsAtTheMuzzle()
    {
        for (uint seed = 1; seed <= 50; seed++) Assert.Equal(Vector3.Zero, Start(0, seed));
    }

    [Fact]
    public void OnlyTheShotsWithSpreadStartAheadOfTheMuzzle()
    {
        int ahead = 0, n = 400;
        for (uint seed = 1; seed <= n; seed++)
        {
            var d = Start(8, seed * 2654435761u);
            Assert.InRange(d.Length(), 0f, 0.6001f);
            if (d != Vector3.Zero) ahead++;
        }
        Assert.InRange(ahead / (float)n, 0.15f, 0.35f);   // both draws positive: about a quarter
    }
}
