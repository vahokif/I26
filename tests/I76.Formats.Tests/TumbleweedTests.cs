using System.Numerics;
using I76.Formats.Vegetation;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The modern look's rolling tumbleweeds (docs/modern-vegetation.md, Tumbleweeds): where, how many, how they roll, start and burst.</summary>
public class TumbleweedTests
{
    /// <summary>A plane rising <paramref name="slope"/> along the wind; blocked past <paramref name="wall"/> metres downwind; a shrub at <paramref name="shrub"/>.</summary>
    private sealed class Land(float slope = 0, float wall = float.MaxValue, Vector2? shrub = null) : ITumbleGround
    {
        public bool Has(float x, float z) => true;
        public float Height(float x, float z) => 10 + slope * Vector2.Dot(new Vector2(x, z), Wind.Direction);
        public bool Blocked(Vector2 p, float radius) => Vector2.Dot(p, Wind.Direction) + radius > wall;
        public (Vector2 Centre, float Radius)? Shrub(Vector2 p) => shrub is { } s ? (s, 0.8f) : null;
    }

    private static TumbleweedField Field(ITumbleGround land, float wind, int count = 1, ulong seed = 7) =>
        new(land, seed, [0.35f, 0.3f, 0.4f]) { Count = count, WindSpeed = (_, _) => wind };

    private static void Run(TumbleweedField f, float seconds, Vector3 eye, Action? each = null)
    {
        for (int i = 0; i < seconds * 60; i++) { f.Step(1 / 60f, eye, []); each?.Invoke(); }
    }

    [Theory]
    [InlineData("t01", true)] [InlineData("t02", true)] [InlineData("t10", true)] [InlineData("t11", true)] [InlineData("m03", true)] [InlineData("m06", true)]
    [InlineData("t04", false)] [InlineData("t15", false)] [InlineData("t16", false)] [InlineData("m10", false)] [InlineData("m13", false)]
    public void OnlyTheOpenDryFlatsHaveThem(string mission, bool some)
    {
        var r = Biomes.For(mission);
        Assert.NotNull(r);
        Assert.Equal(some, r!.Biome.Tumbleweeds > 0);
        // nowhere without plants either: M15's parking lot (asphalt) and M14's snow
        Assert.Null(Biomes.For("m15", "tp091as6.map"));
        Assert.Null(Biomes.For("m14", "tp101sw6.map"));
    }

    [Fact]
    public void FewOnLowAFewMoreOnHighNoneOff()
    {
        foreach (var b in Biomes.Every)
        {
            Assert.InRange(b.Tumbleweeds, 0, 1);
            Assert.Equal(0, TumbleweedField.CountFor(b.Tumbleweeds, 0));
            Assert.InRange(TumbleweedField.CountFor(b.Tumbleweeds, 1), 0, 4);
            Assert.InRange(TumbleweedField.CountFor(b.Tumbleweeds, 2), TumbleweedField.CountFor(b.Tumbleweeds, 1), 7);
        }
        Assert.Equal(2, TumbleweedField.CountFor(Biomes.SouthPlains.Tumbleweeds, 1));
        Assert.Equal(4, TumbleweedField.CountFor(Biomes.SouthPlainsWeedy.Tumbleweeds, 1));
        Assert.Equal(0, TumbleweedField.CountFor(Biomes.Dream.Tumbleweeds, 2));
    }

    [Fact]
    public void TheyStartUpwindOnTheRingAndTheCountHolds()
    {
        var f = Field(new Land(), 6f, count: 5);
        var eye = new Vector3(100, 12, 200);
        f.Step(1 / 60f, eye, []);   // the first call scatters them round the eye
        Assert.Equal(5, f.Weeds.ToArray().Count(w => w.Active));
        // fresh ones on the ring: Range - 8 .. Range from the eye, within 75 degrees of upwind, drawn (they are past the fade)
        for (int i = 0; i < 200; i++)
        {
            var t = new Tumble();
            Assert.True(f.Spawn(ref t, eye, ring: true));
            var d = new Vector2(t.Position.X - eye.X, t.Position.Z - eye.Z);
            Assert.InRange(d.Length(), f.Range - 8.01f, f.Range + 0.01f);
            Assert.True(Vector2.Dot(Vector2.Normalize(d), -Wind.Direction) > MathF.Cos(1.31f), $"spawn {d} not upwind");
            Assert.InRange(t.Radius, 0.25f, 0.5f);
        }
        // minutes of wind: they roll through and out, and come again; never more or fewer, never far out of range
        int maxOut = 0;
        Run(f, 240, eye, () =>
        {
            foreach (var w in f.Weeds)
                if (w.Active) maxOut = Math.Max(maxOut, (int)new Vector2(w.Position.X - eye.X, w.Position.Z - eye.Z).Length());
        });
        Assert.True(f.Respawns >= 5, $"respawns {f.Respawns}");
        Assert.Equal(5, f.Weeds.ToArray().Count(w => w.Active));
        Assert.True(maxOut <= f.Range + 10, $"one got {maxOut} m out");
        // a camera cut scatters them round the new eye
        var far = eye + new Vector3(5000, 0, 0);
        f.Step(1 / 60f, far, []);
        Assert.All(f.Weeds.ToArray(), w => Assert.True(new Vector2(w.Position.X - far.X, w.Position.Z - far.Z).Length() <= f.Range));
    }

    [Fact]
    public void ItRollsDownwindWithRollingContact()
    {
        var f = Field(new Land(), 6f);
        var eye = new Vector3(0, 10, 0);
        f.Step(1 / 60f, eye, []);
        var start = f.Weeds[0].Position;
        int grounded = 0;
        Run(f, 8, eye, () =>
        {
            var w = f.Weeds[0];
            if (!w.Grounded) return;
            grounded++;
            // the contact point is still: v + ω × (−r n) = 0, in the drawn frame
            var v = TumbleweedField.Drawn(new Vector3(w.Velocity.X, 0, w.Velocity.Z));
            var contact = v + Vector3.Cross(w.Spin, new Vector3(0, -w.Radius, 0));
            Assert.True(contact.Length() < 1e-3f, $"contact slips at {contact.Length()} m/s");
        });
        Assert.True(grounded > 100);
        var w = f.Weeds[0];
        var moved = new Vector2(w.Position.X - start.X, w.Position.Z - start.Z);
        Assert.True(Vector2.Dot(Vector2.Normalize(moved), Wind.Direction) > 0.95f, $"moved {moved}");
        float speed = new Vector2(w.Velocity.X, w.Velocity.Z).Length();
        Assert.InRange(speed, 2f, 6f);   // a few m/s in a 6 m/s wind, slipping behind it
    }

    [Fact]
    public void TheSpinTurnsTheTopForward()
    {
        // rolling toward +x in the drawn frame turns the top toward +x
        var t = new Tumble { Orientation = Quaternion.Identity, Spin = Vector3.Cross(Vector3.UnitY, Vector3.UnitX) / 0.5f };
        var dq = Quaternion.CreateFromAxisAngle(Vector3.Normalize(t.Spin), 0.1f);
        var top = TumbleweedField.Rotate(TumbleweedField.Mul(dq, t.Orientation), Vector3.UnitY);
        Assert.True(top.X > 0.09f, $"top went to {top}");
    }

    [Fact]
    public void ItSlowsUphillAndStopsInALull()
    {
        float Mean(float slope, float wind)
        {
            var f = Field(new Land(slope), wind);
            f.Step(1 / 60f, Vector3.Zero, []);
            float sum = 0; int n = 0;
            Run(f, 10, Vector3.Zero, () => { var w = f.Weeds[0]; sum += new Vector2(w.Velocity.X, w.Velocity.Z).Length(); n++; });
            return sum / n;
        }
        float flat = Mean(0, 6), up = Mean(0.3f, 6), calm = Mean(0, 1.5f);
        Assert.True(up < flat * 0.9f, $"uphill {up} against flat {flat}");
        Assert.True(calm < 0.25f, $"in a lull it still rolls at {calm} m/s");
        var still = Field(new Land(), 1.5f);
        Run(still, 10, Vector3.Zero);
        Assert.Equal(0, new Vector2(still.Weeds[0].Velocity.X, still.Weeds[0].Velocity.Z).Length());
    }

    [Fact]
    public void ItHopsAndLands()
    {
        var f = Field(new Land(), 7f);
        f.Step(1 / 60f, Vector3.Zero, []);
        int airborne = 0; float highest = 0;
        Run(f, 20, Vector3.Zero, () =>
        {
            var w = f.Weeds[0];
            if (!w.Grounded) airborne++;
            highest = MathF.Max(highest, w.Position.Y - w.Radius - w.GroundY);
            Assert.True(w.Position.Y - w.Radius >= w.GroundY - 1e-3f, "below the ground");
        });
        Assert.True(airborne > 10, $"never hopped ({airborne} frames up)");
        Assert.InRange(highest, 0.03f, 1.5f);
    }

    [Fact]
    public void AWallStopsOrTurnsIt()
    {
        var f = Field(new Land(wall: 20), 7f);
        f.Range = 400;
        f.Step(1 / 60f, Vector3.Zero, []);
        Run(f, 60, Vector3.Zero, () =>
        {
            var w = f.Weeds[0];
            if (w.Active && w.Fade > 0) Assert.True(Vector2.Dot(new Vector2(w.Position.X, w.Position.Z), Wind.Direction) <= 20, "went through the wall");
        });
    }

    [Fact]
    public void AShrubCatchesItForAMoment()
    {
        // a shrub straight downwind of where it starts: in a steady wind it is caught (or glances off) and goes on
        var f = Field(new Land(), 6f, seed: 3);
        f.Range = 1000;
        f.Step(1 / 60f, Vector3.Zero, []);
        var p = f.Weeds[0].Position;
        var shrub = new Vector2(p.X, p.Z) + Wind.Direction * 6;
        var g = Field(new Land(shrub: shrub), 6f, seed: 3);
        g.Range = 1000;
        g.Step(1 / 60f, Vector3.Zero, []);
        bool caught = false;
        Run(g, 20, Vector3.Zero, () => caught |= g.Weeds[0].Snag > 0);
        var w = g.Weeds[0];
        float past = Vector2.Dot(new Vector2(w.Position.X, w.Position.Z) - shrub, Wind.Direction);
        Assert.True(past > 5, $"still at the shrub ({past} m past it, caught {caught})");
    }

    [Fact]
    public void ACarBurstsItIntoPiecesThatFade()
    {
        var f = Field(new Land(), 0f);
        f.Step(1 / 60f, Vector3.Zero, []);
        var w = f.Weeds[0].Position;
        // a car at 20 m/s driving through it
        var vel = new Vector3(20, 0, 0);
        var car = new TumbleCar(new Vector3(w.X + 0.1f, w.Y, w.Z), vel);
        f.Step(1 / 60f, Vector3.Zero, [car]);
        Assert.Equal(1, f.Bursts);
        var pieces = f.Fragments.ToArray().Where(x => x.Active).ToArray();
        Assert.InRange(pieces.Length, 3, 4);
        Assert.All(pieces, x => { Assert.True(x.Velocity.Y > 2); Assert.True(x.Spin.Length() > 5); Assert.True(x.Velocity.X > 5, "not carried along"); });
        // the slot comes back at once (upwind), the pieces fade out within a few seconds
        Assert.False(f.Weeds[0].Active);
        f.Step(1 / 60f, Vector3.Zero, []);
        Assert.True(f.Weeds[0].Active);
        Run(f, 3, Vector3.Zero);
        Assert.DoesNotContain(f.Fragments.ToArray(), x => x.Active);
        // a car well clear of it does nothing
        var before = f.Bursts;
        var w2 = f.Weeds[0].Position;
        f.Step(1 / 60f, Vector3.Zero, [new TumbleCar(new Vector3(w2.X + 10, w2.Y, w2.Z), vel)]);
        Assert.Equal(before, f.Bursts);
        // nor does one standing on it: it holds it off
        f.Step(1 / 60f, Vector3.Zero, [new TumbleCar(new Vector3(w2.X + 0.5f, w2.Y, w2.Z), Vector3.Zero)]);
        Assert.Equal(before, f.Bursts);
        var w3 = f.Weeds[0].Position;
        Assert.True(new Vector2(w3.X - (w2.X + 0.5f), w3.Z - w2.Z).Length() >= TumbleweedField.CarRadius);
    }

    [Fact]
    public void ARunRepeats()
    {
        Vector3[] Positions(ulong seed)
        {
            var f = Field(new Land(0.05f), 6f, count: 4, seed: seed);
            Run(f, 30, new Vector3(50, 10, 50));
            return [.. f.Weeds.ToArray().Select(w => w.Position)];
        }
        Assert.Equal(Positions(11), Positions(11));
        Assert.NotEqual(Positions(11), Positions(12));
    }
}
