using I76.Sim.Damage;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Structo Bumper (5), Curb Feelers (6) and Mud Flaps (7) in the armour step, and a dead car's specials in the field
/// salvage. The numbers are the original's, measured by running its own armour step on armour and chassis pools of
/// 400 and tops of 100.
/// </summary>
public class PassiveSpecialsTests
{
    private const int Structo = 5, Curb = 6, Mud = 7;
    private const int Ballistic = 0, Explosive = 1, Impact = 3;

    private static VehicleDamage Car(params int[] specials)
    {
        var v = new Vehicle();
        var d = new VehicleDamage(v, false);
        v.Health = d;
        for (int z = 0; z < 4; z++) d.Armour[z] = d.ArmourMirror[z] = d.Chassis[z] = d.ChassisMirror[z] = 400;
        d.ArmourMirror[4] = d.ChassisMirror[4] = 100;
        for (int i = 0; i < specials.Length; i++) d.Slots[17 + i] = new SpecialPart(specials[i]);
        return d;
    }

    private static DamagePacket Packet(int channel, int amount)
    {
        var p = new DamagePacket { Mask = (ushort)(1 << channel) };
        p.Ch[channel] = amount;
        return p;
    }

    /// <summary>The pool the channel wears (armour for ballistic and explosive, chassis for impacts), worn and passed.</summary>
    private static (int Worn, int Passes) Hit(VehicleDamage d, int zone, int channel, int amount)
    {
        int[] pool = zone == 4 ? (channel == Impact ? d.ChassisMirror : d.ArmourMirror) : channel == Impact ? d.Chassis : d.Armour;
        int before = pool[zone];
        int passes = d.ArmourStep(zone, Packet(channel, amount));
        return (before - pool[zone], passes);
    }

    [Theory]
    [InlineData(new int[0], 1, Ballistic, 11, 11, 0)]
    [InlineData(new[] { Curb }, 1, Ballistic, 11, 9, 0)]
    [InlineData(new[] { Curb }, 1, Ballistic, 22, 19, 0)]
    [InlineData(new[] { Curb }, 1, Ballistic, 100, 90, 0)]
    [InlineData(new[] { Curb }, 1, Ballistic, 440, 399, 0)]
    [InlineData(new[] { Curb }, 1, Ballistic, 441, 400, 1)]
    [InlineData(new[] { Curb }, 1, Ballistic, 500, 400, 60)]
    [InlineData(new[] { Curb }, 4, Ballistic, 55, 49, 55)]
    [InlineData(new[] { Curb }, 1, Explosive, 500, 400, 60)]
    [InlineData(new[] { Curb }, 1, Impact, 500, 400, 100)]
    [InlineData(new int[0], 0, Impact, 500, 400, 100)]
    [InlineData(new[] { Structo }, 0, Impact, 500, 250, 0)]
    [InlineData(new[] { Structo }, 0, Impact, 900, 400, 100)]
    [InlineData(new[] { Structo }, 3, Impact, 900, 400, 100)]
    [InlineData(new[] { Structo }, 1, Impact, 900, 400, 500)]
    [InlineData(new[] { Structo }, 0, Ballistic, 500, 400, 100)]
    [InlineData(new[] { Mud }, 1, Impact, 11, 9, 0)]
    [InlineData(new[] { Mud }, 1, Impact, 22, 19, 0)]
    [InlineData(new[] { Mud }, 1, Impact, 500, 400, 60)]
    [InlineData(new[] { Mud }, 1, Ballistic, 500, 400, 100)]
    [InlineData(new[] { Structo, Mud }, 0, Impact, 21, 10, 0)]
    [InlineData(new[] { Structo, Mud }, 0, Impact, 900, 400, 61)]
    [InlineData(new[] { Mud, Mud }, 1, Impact, 500, 400, 60)]
    public void TheArmourStepWearsWhatTheOriginalWears(int[] specials, int zone, int channel, int amount, int worn, int passes)
    {
        Assert.Equal((worn, passes), Hit(Car(specials), zone, channel, amount));
    }

    [Fact]
    public void ADestroyedCurbFeelerAbsorbsNothingAndOnlyTheFirstActiveOneCounts()
    {
        var dead = Car(Curb);
        ((SpecialPart)dead.Slots[17]!).Charges = 0;
        Assert.Equal((400, 100), Hit(dead, 1, Ballistic, 500));
        var both = Car(Curb, Curb);
        ((SpecialPart)both.Slots[17]!).Charges = 0;
        Assert.Equal((400, 60), Hit(both, 1, Ballistic, 500));
    }

    /// <summary>The original's formula: the single-precision multiplier, the product and quotient in double, truncated.</summary>
    private static (float Chassis, float Armour) Multipliers(int zone, int[] specials)
    {
        float mc = (zone is 0 or 3) && specials.Contains(Structo) ? 2f : 1f;
        if (specials.Contains(Mud)) mc = (float)(mc + (double)0.1f);
        return (mc, specials.Contains(Curb) ? 1.1f : 1f);
    }

    public static TheoryData<int[], int> Combinations()
    {
        var data = new TheoryData<int[], int>();
        int[][] sets = [[], [Structo], [Curb], [Mud], [Structo, Mud], [Structo, Curb], [Curb, Mud], [Structo, Curb, Mud]];
        foreach (var s in sets) foreach (int zone in new[] { 0, 1, 4 }) data.Add(s, zone);
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryValueUpTo3000MatchesTheOriginalsFormula(int[] specials, int zone)
    {
        var (mc, ma) = Multipliers(zone, specials);
        foreach (int channel in new[] { Ballistic, Impact })
        {
            double m = channel == Impact ? mc : ma;
            for (int v = 0; v <= 3000; v++)
            {
                // the wear of a hit that the pool takes whole
                var d = Car(specials);
                int[] pool = zone == 4 ? (channel == Impact ? d.ChassisMirror : d.ArmourMirror) : channel == Impact ? d.Chassis : d.Armour;
                pool[zone] = 100000;
                int passes = d.ArmourStep(zone, Packet(channel, v));
                Assert.Equal((int)System.Math.Truncate(v / m), 100000 - pool[zone]);
                Assert.Equal(zone == 4 ? v : 0, passes);
                // the cap of a pool of v against a hit it cannot take
                if (zone == 4) continue;
                d = Car(specials);
                pool = channel == Impact ? d.Chassis : d.Armour;
                pool[zone] = v;
                passes = d.ArmourStep(zone, Packet(channel, 100000));
                Assert.Equal(100000 - (int)System.Math.Truncate(v * m), passes);
                Assert.Equal(0, pool[zone]);
            }
        }
    }

    [Fact]
    public void SinglePrecisionWouldWearOneMoreOnEveryMultipleOfElevenAndCapSomePoolsOneHigher()
    {
        // the counts the measurement gives for 0..3000, as a check that the sweep above tests the difference
        int wear = Enumerable.Range(0, 3001).Count(v => (int)(v / 1.1f) != VehicleDamage.Wear(v, 1.1f));
        float structoMud = 2f - -0.1f;
        int cap = Enumerable.Range(0, 3001).Count(v => (int)(v * structoMud) != VehicleDamage.Cap(v, structoMud));
        Assert.Equal(272, wear);
        Assert.Equal(125, cap);
        Assert.Equal(20, VehicleDamage.Cap(10, structoMud));
        Assert.Equal(839, VehicleDamage.Cap(400, structoMud));
    }

    [Fact]
    public void ADeadCarsSpecialsAreAllSalvagedAtHealthOneEvenOnesShotToZero()
    {
        var v = new Vehicle { Loadout = new Loadout(["null", "null", "null"], 0, 0, 0, [Structo, 2]) };
        var d = new VehicleDamage(v, false);
        v.Health = d;
        var shot = new SpecialPart(Structo);
        shot.Apply(Packet(Ballistic, 1000));
        Assert.Equal(0, shot.Hp);
        d.Slots[17] = shot;
        d.Slots[18] = new SpecialPart(2) { Hp = 7 };
        var pool = new SalvagePool("t03");
        pool.AddDeadCar(v);
        var specials = pool.Items.Where(i => i.Name.StartsWith("spc")).OrderBy(i => i.Name).ToList();
        Assert.Equal([new SalvageItem("spc02", 1, false), new SalvageItem("spc05", 1, false)], specials);
    }
}
