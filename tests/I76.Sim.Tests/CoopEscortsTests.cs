using System.Numerics;
using I76.Sim.Fsm;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Co-op Escorts (docs/multiplayer.md "Co-op Scaling"): T09's Callisto, a car the mission is about, is not copied; under Co-op
/// Scaling he gets escort cars instead, which keep station on him, attack a human who comes near, never target or hurt him, and
/// leave the mission's rules as they are.
/// </summary>
public class CoopEscortsTests(ITestOutputHelper output)
{
    private static (SimSession S, MissionSetup Setup) Make(SnapshotRig.Loaded l, int humans, int extra) =>
        SnapshotRig.Make(l, SnapshotRig.BuildOf(l.Dir, "t09", coop: CoopSetup.Of(humans) with { ExtraEnemies = extra }), SimRate.R20, 1);

    /// <summary>Ticks with nobody at the wheel; with <paramref name="skip"/> the lead presses the skip key every second (the intro ends).</summary>
    private static void Idle(SimSession s, int ticks, bool skip = false)
    {
        for (int i = 0; i < ticks; i++)
        {
            var inputs = new CarInput[s.Drivers.Count];
            if (skip && i % 20 == 0) inputs[0].Commands = CarCommands.Skip;
            s.Tick(inputs);
            SnapshotRig.TakeEvents(s);
        }
    }

    private static Entity Callisto(ScriptHost h) => h.Entities.Single(e => e.Name.Equals("t09dl01", StringComparison.OrdinalIgnoreCase) && e.ExtraOf is null);

    /// <summary>A round from <paramref name="from"/> into <paramref name="to"/>.</summary>
    private static void Shoot(MissionSetup setup, Vehicle from, Vehicle to, int amount)
    {
        var p = new DamagePacket { Attacker = from, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
        p.Ch[1] = amount;
        setup.Damage.Apply(to, p, Vector3.Zero);
    }

    /// <summary>
    /// T09, three humans, the cap at 3: one Callisto and two escorts (min(3 − 1, 3)), made beside him when the script first orders
    /// him; each the table's car with its turrets, on his team, out of the script's sight. Four humans: three.
    /// </summary>
    [Fact]
    public void CallistoGetsEscortsWithTurretsInsteadOfCopies()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        foreach (var (humans, want) in new[] { (3, 2), (4, 3) })
        {
            var (s, setup) = Make(l, humans, CoopSetup.MaxExtraEnemies);
            var h = setup.Script!.Host;
            int cars = setup.World.Vehicles.Count;
            Idle(s, 20);
            var cal = Callisto(h);
            Assert.Single(h.Entities, e => e.Vehicle is not null && e.Team == cal.Team && e.ExtraOf is null);
            Assert.True(h.Escorted.ContainsKey(cal));
            var escorts = h.EscortsOf(cal).ToList();
            Assert.Equal(want, escorts.Count);
            Assert.Equal(cars + want, setup.World.Vehicles.Count);
            Assert.DoesNotContain(h.Entities, e => e.ExtraOf == cal && e.Vehicle!.Definition == cal.Vehicle!.Definition);   // no copy of him
            foreach (var x in escorts)
            {
                Assert.Equal(CoopEscorts.For("t09", "t09dl01")!.Car, x.Vehicle!.Definition, ignoreCase: true);
                Assert.Contains(x.Vehicle.Armory!.Weapons, w => w.Style == 3);   // turrets
                Assert.Equal(cal.Team, x.Team);
                Assert.Empty(x.Id);
                Assert.DoesNotContain(x, h.Vehicles);
                Assert.DoesNotContain(x, h.AiVehicles);
                Assert.InRange(Vector3.Distance(x.Position, cal.Position), 3f, 40f);
            }
            output.WriteLine($"{humans} humans: {escorts.Count} escorts ({escorts[0].Vehicle!.Definition}: {string.Join(", ", escorts[0].Vehicle!.Armory!.Weapons.Select(w => w.Def.Name))})");
        }
    }

    /// <summary>One human, or the option off: no escorts, nothing made.</summary>
    [Fact]
    public void NoEscortsAloneOrWithTheOptionOff()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        foreach (var (humans, extra) in new[] { (1, 3), (3, 0) })
        {
            var (s, setup) = Make(l, humans, extra);
            int cars = setup.World.Vehicles.Count;
            Idle(s, 40);
            Assert.DoesNotContain(setup.Script!.Host.Entities, e => e.ExtraOf is not null);
            Assert.Equal(cars, setup.World.Vehicles.Count);
            if (extra == 0) Assert.Null(setup.Damage.Spared);
        }
    }

    /// <summary>
    /// Through the intro (the script holds the camera, the humans far behind) the escorts keep station on Callisto as he drives
    /// off, holding their fire; once the intro is over a human who drives up beside one is attacked by it. No escort ever
    /// targets Callisto, its turrets never track him, and nothing reaches him from them.
    /// </summary>
    [Fact]
    public void EscortsKeepStationAttackANearHumanAndNeverTargetCallisto()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), 3, 3);
        var h = setup.Script!.Host;
        setup.Damage.UnlimitedArmour = setup.Damage.UnlimitedChassis = true;
        var cal = Callisto(h);
        int fromEscorts = 0;
        setup.Damage.Damaging += (v, p) => { if (v == cal.Vehicle && p.Attacker is Vehicle a && h.IsEscort(h.Of(a))) fromEscorts++; };
        void Check()
        {
            foreach (var x in h.EscortsOf(cal))
            {
                Assert.False(x.Order == AiOrder.Attack && x.OrderTarget == cal);
                Assert.NotEqual(cal.Vehicle, setup.Combat.RadarTarget.GetValueOrDefault(x.Vehicle!));
            }
        }
        // the intro: Callisto drives his opening path, the escorts at their stations
        float worst = 0;
        for (int i = 0; i < 140; i++)
        {
            Idle(s, 1);
            Check();
            if (i < 40) continue;
            Assert.True(h.View.Active);
            int k = 0;
            foreach (var x in h.EscortsOf(cal))
            {
                Assert.Equal(AiOrder.Follow, x.Order);
                Assert.Equal(cal, x.OrderTarget);
                Assert.Equal(0, x.FollowFlag);   // holding fire in the cut-scene
                worst = MathF.Max(worst, Vector3.Distance(cal.Position + cal.Rot.ToWorld(CoopEscorts.Stations[k++]), x.Position));
            }
        }
        output.WriteLine($"intro: worst distance from a station {worst:0.0} m, Callisto at {cal.Vehicle!.Speed:0} m/s");
        Assert.True(cal.Vehicle!.Speed > 10);
        Assert.InRange(worst, 0, 20);
        // the intro skipped; a human parked beside the first escort, the others well away
        Idle(s, 60, skip: true);
        Assert.False(h.View.Active);
        var escort = h.EscortsOf(cal).First();
        var seats = h.Coop!.Seats;
        bool attacked = false;
        for (int i = 0; i < 100 && !attacked; i++)
        {
            for (int k = 0; k < seats.Count; k++)
            {
                var v = seats[k].Car;
                var at = k == 1 ? escort.Position + escort.Rot.ToWorld(new Vector3(25, 0, 0)) : cal.Position + new Vector3(0, 0, 300 + 20 * k);
                v.Pos = new I76.Sim.Math.Double3(at.X, setup.Ground.BareHeight(at.X, at.Z) + 1, at.Z);
                v.Vel = default; v.AngVel = default;
            }
            Idle(s, 1);
            Check();
            attacked = escort.Order == AiOrder.Attack && escort.OrderTarget == seats[1].Entity;
        }
        Assert.True(attacked);
        // let the human go: the escort goes back to Callisto
        for (int i = 0; i < 200; i++)
        {
            foreach (var seat in seats)
            {
                var at = cal.Position + new Vector3(0, 0, 400);
                seat.Car.Pos = new I76.Sim.Math.Double3(at.X, setup.Ground.BareHeight(at.X, at.Z) + 1, at.Z);
                seat.Car.Vel = default;
            }
            Idle(s, 1);
            Check();
        }
        Assert.NotEqual(AiOrder.Attack, escort.Order);
        Assert.Equal(0, fromEscorts);
    }

    /// <summary>The damage rule: an escort's round into Callisto is dropped whole; a human's still hurts him, and an escort's hurts a human.</summary>
    [Fact]
    public void AnEscortsRoundsDoNotHurtCallisto()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), 3, 3);
        var h = setup.Script!.Host;
        Idle(s, 20);
        var cal = Callisto(h);
        var escort = h.EscortsOf(cal).First();
        var human = h.Coop!.Seats[1].Entity;
        float hp = cal.Health;
        for (int i = 0; i < 20; i++) Shoot(setup, escort.Vehicle!, cal.Vehicle!, 50);
        Assert.Equal(hp, cal.Health);
        Assert.Null(cal.LastShooter);
        Shoot(setup, human.Vehicle!, cal.Vehicle!, 50);
        Assert.True(cal.Health < hp);
        float humanHp = human.Health;
        Shoot(setup, escort.Vehicle!, human.Vehicle!, 50);
        Assert.True(human.Health < humanHp);
    }

    /// <summary>
    /// The mission's rules stand: with escorts as without, Callisto brought below a third of his health (by a human) wins it,
    /// and Callisto destroyed fails it (objective 8); the escorts destroyed change nothing.
    /// </summary>
    [Fact]
    public void WinAndFailRulesAreThoseOfTheMission()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        (int[] Flags, (bool Success, float At, int FailMessage)? End) Run(int extra, bool kill, bool killEscorts)
        {
            var (s, setup) = Make(l, 3, extra);
            var h = setup.Script!.Host;
            setup.Damage.UnlimitedArmour = setup.Damage.UnlimitedChassis = true;
            Idle(s, 40); Idle(s, 40, skip: true);
            var cal = Callisto(h);
            if (killEscorts)
            {
                foreach (var x in h.EscortsOf(cal).ToList()) h.Destroy(x);
                Idle(s, 100);
                Assert.Null(h.End);
                Assert.All(h.Objectives.Flags, f => Assert.Equal(0, f));
            }
            var human = h.Coop!.Seats[0].Car;
            if (kill) h.Destroy(cal);
            else
                while (cal.Health >= 30) Shoot(setup, human, cal.Vehicle!, 20);
            for (int i = 0; i < 20 * 30 && h.End is null; i++) Idle(s, 1);
            return ([.. h.Objectives.Flags], h.End);
        }
        var winPlain = Run(0, kill: false, killEscorts: false);
        var winEscorted = Run(3, kill: false, killEscorts: false);
        var failPlain = Run(0, kill: true, killEscorts: false);
        var failEscorted = Run(3, kill: true, killEscorts: true);
        output.WriteLine($"win {string.Join(",", winPlain.Flags)} {winPlain.End} / {string.Join(",", winEscorted.Flags)} {winEscorted.End}");
        output.WriteLine($"fail {string.Join(",", failPlain.Flags)} {failPlain.End} / {string.Join(",", failEscorted.Flags)} {failEscorted.End}");
        Assert.True(winPlain.End is { Success: true });
        Assert.True(winEscorted.End is { Success: true });
        Assert.Equal(winPlain.Flags, winEscorted.Flags);
        Assert.True(failPlain.End is { Success: false });
        Assert.True(failEscorted.End is { Success: false });
        Assert.Equal(failPlain.Flags, failEscorted.Flags);
        Assert.Equal(failPlain.End!.Value.FailMessage, failEscorted.End!.Value.FailMessage);
    }

    /// <summary>The escorts are in the savestate: a snapshot restored into a fresh build carries on the same, escorting or catching up.</summary>
    [Fact]
    public void EscortsAreSavedAndRestored()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (a, sa) = Make(l, 3, 3);
        Idle(a, 40); Idle(a, 160, skip: true);
        var snap = a.Snapshot();
        var (b, sb) = Make(l, 3, 3);
        b.Restore(snap);
        var ha = sa.Script!.Host; var hb = sb.Script!.Host;
        Assert.Equal(ha.EscortsOf(Callisto(ha)).Count(), hb.EscortsOf(Callisto(hb)).Count());
        Assert.NotNull(sb.Damage.Spared);
        Idle(a, 200); Idle(b, 200);
        Assert.Equal(a.StateHash(), b.StateHash());
    }
}
