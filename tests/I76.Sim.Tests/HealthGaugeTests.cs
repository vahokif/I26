using I76.Sim.Damage;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The target box's bar: the health percentage by object type.</summary>
public class HealthGaugeTests
{
    [Fact]
    public void AHelicoptersBarFollowsItsPool()
    {
        // the AI's helicopter: no car damage state at all; it read whole while it took hits (#38)
        var v = new Vehicle { Heli = new HeliState() };
        v.Heli.SetPool(6000);
        Assert.Equal(1f, HealthGauge.Fraction(v));
        v.Heli.Hp = 1584;
        Assert.Equal(1584f / 6000f, HealthGauge.Fraction(v), 4);
    }

    [Fact]
    public void ThePlayersHelicopterReadsItsPoolNotItsSpecialsOnlyDamageState()
    {
        var v = new Vehicle { Heli = new HeliState(), Health = new VehicleDamage(new Vehicle(), true) };
        v.Heli.SetPool(3000);
        v.Heli.Hp = 1500;
        Assert.Equal(0.5f, HealthGauge.Fraction(v), 4);
    }

    [Fact]
    public void AnythingWithoutAStateReadsWhole() => Assert.Equal(1f, HealthGauge.Fraction(new Vehicle()));
}
