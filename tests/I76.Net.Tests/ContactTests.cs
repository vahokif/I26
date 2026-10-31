using System.Numerics;
using I76.Server;
using I76.Sim.Math;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// Two cars meeting in a network melee, as the client of one of them draws them: the gap between the drawn cars when the
/// client's prediction feels the contact and when the server has it (docs/multiplayer.md, "Remote cars near the own car").
/// </summary>
public class ContactTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// A client (seat a) and another player (seat b, driven on the server), the snapshots <paramref name="late"/> ticks
    /// late, three frames a tick on a deterministic clock. Returns the drawn gaps (metres, between the cars' boxes)
    /// at the first tick the client's prediction met b and at the first tick the server did, and the server's own gap then.
    /// </summary>
    private (double Felt, double Server, double ServerOwn, double Overlap) Meet(string who, float speedA, float speedB, int late, bool near = true)
    {
        var install = HostTests.InstallDir!;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var ca = a.Player.Car;
        var cb = b.Player.Car;
        // face to face 40 m apart along a's heading, on open ground
        var f = ca.Rot.Forward;
        var flat = Vector3.Normalize(new Vector3(f.X, 0, f.Z));
        var at = ca.Pos;
        var ground = host.Setup.World.Ground;
        Double3 Put(double along) { double x = at.X + flat.X * along, z = at.Z + flat.Z * along; return new Double3(x, ground.BareHeight(x, z) + 0.6, z); }
        ca.Pos = Put(0);
        cb.Pos = Put(40);
        ca.Rot = new Rows(Vector3.Cross(Vector3.UnitY, flat), Vector3.UnitY, flat);
        cb.Rot = new Rows(Vector3.Cross(Vector3.UnitY, -flat), Vector3.UnitY, -flat);
        ca.Vel = cb.Vel = default;
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(a));
        var client = new MeleeClient();
        double now = 0;
        client.TestClock = () => now;
        var predictor = new OwnCarPredictor(mirror, client, predictFire: false);
        mirror.NearAtPresent = near;
        float dt = host.Session.TickSeconds;
        var wire = new Queue<Snapshot>();
        double felt = double.NaN, server = double.NaN, serverOwn = double.NaN;
        bool serverMet = false;
        double overlap = 0;
        uint metAt = 0;
        CarInput Drive(Vehicle v, float want) => new() { Buttons = v.Speed < want ? CarButtons.ThrottleUp : 0 };
        for (uint t = 1; t <= 400 && (metAt == 0 || t < metAt + 30); t++)
        {
            // settle first, then drive at each other
            bool go = t > 40;
            var ia = go ? Drive(mirror.Own?.Vehicle ?? ca, speedA) : default;
            var ib = go ? Drive(cb, speedB) : default;
            a.Inputs.Add(new InputMessage(t, [ia]));
            b.Inputs.Add(new InputMessage(t, [ib]));
            predictor.Step(t, ia);
            bool feltNow = go && mirror.Setup.World.Step.CarHits.Count > 0;
            // the server's gap at the start of its tick (the cars as it then draws them)
            double before = Gap(ca, ca.Pos, ca.Rot, cb, cb.Pos, cb.Rot);
            host.Step();
            host.TakeEvents();
            bool serverNow = go && host.Setup.World.Step.CarHits.Count > 0;
            if (serverNow && !serverMet) { serverMet = true; serverOwn = before; metAt = t; }
            wire.Enqueue(PredictionTests.Through(host, a));
            while (wire.Count > late) client.Inject(wire.Dequeue());
            for (int k = 0; k < 3; k++)
            {
                float alpha = k / 3f;
                mirror.InputAlpha = alpha;
                mirror.Advance(dt / 3, client);
                predictor.Reconcile();
                predictor.Smooth(dt / 3);
                if (!predictor.Ready || mirror.Car(b.CarSerial) is not { } rb || mirror.Cars[b.CarSerial].Drawn is not { } db) continue;
                var (ra, pa) = predictor.Pose(alpha);
                double gap = Gap(mirror.Own!.Vehicle, pa, ra, rb, db.Pos, db.Rot);
                if (k == 0 && feltNow && double.IsNaN(felt)) felt = gap;
                // the server's contact reaches the client's view `late` ticks and the view's delay later; measured at
                // the server's tick, as a player feels it on its own car (the push comes back with the next snapshot)
                if (k == 0 && serverNow && double.IsNaN(server)) server = gap;
                // how far the drawn cars go into each other afterwards (b is drawn as it came, its bounce a round trip late)
                if (metAt != 0) overlap = Math.Min(overlap, gap);
            }
            now += dt;
        }
        output.WriteLine($"{who}: a {speedA} m/s, b {speedB} m/s, {late} ticks late: drawn gap when the prediction met b {felt:0.00} m, " +
            $"when the server did {server:0.00} m (the server's own boxes then {serverOwn:0.00} m); then drawn {-overlap:0.00} m into each other" + (near ? "" : " (on b's own clock)"));
        return (felt, server, serverOwn, overlap);
    }

    /// <summary>The gap between two cars' boxes in the ground plane (the most any separating axis of either shows; 0 or less when they overlap).</summary>
    internal static double Gap(Vehicle va, Double3 pa, Rows ra, Vehicle vb, Double3 pb, Rows rb)
    {
        var d = new Vector2((float)(pb.X - pa.X), (float)(pb.Z - pa.Z));
        double best = double.NegativeInfinity;
        var axes = new[] { Flat(ra.Right), Flat(ra.Forward), Flat(rb.Right), Flat(rb.Forward) };
        foreach (var ax in axes)
        {
            var (a0, a1) = Extent(va, ra, ax);
            var (b0, b1) = Extent(vb, rb, ax);
            float o = Vector2.Dot(d, ax);
            best = Math.Max(best, Math.Max(b0 + o - a1, a0 - (b1 + o)));
        }
        return best;
        static Vector2 Flat(Vector3 v) => Vector2.Normalize(new Vector2(v.X, v.Z));
        static (float, float) Extent(Vehicle v, Rows r, Vector2 ax)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var x in new[] { v.BoxX[0], v.BoxX[3] })
                foreach (var z in new[] { v.BoxZ[0], v.BoxZ[3] })
                {
                    var w = r.ToWorld(new Vector3(x, 0, z));
                    float p = Vector2.Dot(new Vector2(w.X, w.Z), ax);
                    lo = Math.Min(lo, p); hi = Math.Max(hi, p);
                }
            return (lo, hi);
        }
    }

    /// <summary>
    /// b weaves round a parked a, within 15 m, the snapshots 4 ticks late: how far b is drawn from where the server has it
    /// at the same moment (the present for a's prediction; the mean, within <see cref="CarTrack.FarMetres"/>), and how jerky its
    /// drawn path is (the 99th percentile of the change of its drawn velocity from one frame to the next, m/s), on its own clock
    /// and at the present.
    /// </summary>
    private (double Error, double Jerk) Weave(bool near)
    {
        var install = HostTests.InstallDir!;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var ca = a.Player.Car;
        var cb = b.Player.Car;
        var f = ca.Rot.Forward;
        var flat = Vector3.Normalize(new Vector3(f.X, 0, f.Z));
        var side = Vector3.Cross(Vector3.UnitY, flat);
        var ground = host.Setup.World.Ground;
        double bx = ca.Pos.X + side.X * 14, bz = ca.Pos.Z + side.Z * 14;
        cb.Pos = new Double3(bx, ground.BareHeight(bx, bz) + 0.6, bz);
        cb.Rot = ca.Rot;
        ca.Vel = cb.Vel = default;
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(a));
        var client = new MeleeClient();
        double now = 0;
        client.TestClock = () => now;
        var predictor = new OwnCarPredictor(mirror, client, predictFire: false);
        mirror.NearAtPresent = near;
        float dt = host.Session.TickSeconds;
        var wire = new Queue<Snapshot>();
        var truth = new Dictionary<uint, Double3>();
        double err = 0, sum = 0;
        var jerks = new List<double>();
        int hits = 0, impacts = 0;
        int n = 0, m = 0;
        Double3? p1 = null, p2 = null;
        for (uint t = 1; t <= 300; t++)
        {
            // round and round, speeding up and slowing down, the steer let go now and then
            float want = t % 80 < 40 ? 11 : 5;
            var ib = new CarInput { Buttons = (cb.Speed < want ? CarButtons.ThrottleUp : cb.Speed > want + 2 ? CarButtons.ThrottleDown : 0) | (t % 50 < 40 ? CarButtons.SteerLeft : 0) };
            a.Inputs.Add(new InputMessage(t, [default]));
            b.Inputs.Add(new InputMessage(t, [ib]));
            predictor.Step(t, default);
            host.Step();
            host.TakeEvents();
            truth[host.Tick] = cb.Pos;
            hits += host.Setup.World.Step.CarHits.Count;
            impacts += host.Setup.World.Step.Impacts.Count;
            wire.Enqueue(PredictionTests.Through(host, a));
            while (wire.Count > 4) client.Inject(wire.Dequeue());
            for (int k = 0; k < 3; k++)
            {
                float alpha = k / 3f;
                mirror.InputAlpha = alpha;
                mirror.Advance(dt / 3, client);
                predictor.Reconcile();
                predictor.Smooth(dt / 3);
                if (t < 60 || mirror.Cars[b.CarSerial].Drawn is not { } db || !predictor.Ready) continue;
                // the server's b at the present, between its ticks
                double pt = predictor.PresentTick(alpha);
                uint lo = (uint)Math.Floor(pt);
                if (truth.TryGetValue(lo, out var q0) && truth.TryGetValue(lo + 1, out var q1))
                {
                    double u = pt - lo;
                    var q = new Double3(q0.X + (q1.X - q0.X) * u, q0.Y + (q1.Y - q0.Y) * u, q0.Z + (q1.Z - q0.Z) * u);
                    double e = Math.Sqrt(Math.Pow(db.Pos.X - q.X, 2) + Math.Pow(db.Pos.Z - q.Z, 2));
                    if (Math.Sqrt(Math.Pow(q.X - ca.Pos.X, 2) + Math.Pow(q.Z - ca.Pos.Z, 2)) < CarTrack.FarMetres) { err = Math.Max(err, e); sum += e; m++; }
                }
                if (p1 is { } x1 && p2 is { } x2)
                {
                    double ax = db.Pos.X - 2 * x1.X + x2.X, az = db.Pos.Z - 2 * x1.Z + x2.Z;
                    jerks.Add(Math.Sqrt(ax * ax + az * az) / (dt / 3));
                }
                (p2, p1) = (p1, db.Pos);
                n++;
            }
            now += dt;
        }
        jerks.Sort();
        double jerk = jerks[(int)(jerks.Count * 0.99)];
        output.WriteLine($"({hits} car hits, {impacts} impacts; max jerk {jerks[^1]:0.00})");
        output.WriteLine($"weave, {(near ? "near cars at the present" : "on its own clock")}: within {CarTrack.FarMetres} m of a ({m} frames), b drawn up to {err:0.00} m (mean {sum / Math.Max(1, m):0.00} m) from the server's b at the present; 99th percentile of the frame-to-frame change of its drawn velocity {jerk:0.00} m/s");
        return (sum / Math.Max(1, m), jerk);
    }

    [Fact]
    public void ANearCarIsDrawnNearWhereItIsAndStaysSmooth()
    {
        if (HostTests.InstallDir is null) return;
        var (errOwn, jerkOwn) = Weave(near: false);
        var (errNear, jerkNear) = Weave(near: true);
        Assert.True(errNear < errOwn / 2, $"mean {errNear:0.00} m from the present at the present, {errOwn:0.00} m on its own clock");
        Assert.True(jerkNear < 1.5 * jerkOwn + 0.5, $"jerk {jerkNear:0.00} at the present, {jerkOwn:0.00} on its own clock");
    }

    [Theory]
    [InlineData(8f, 0f, 3)]
    [InlineData(0f, 8f, 3)]
    [InlineData(8f, 8f, 3)]
    [InlineData(15f, 15f, 4)]
    public void TheDrawnCarsTouchWhenTheyMeet(float speedA, float speedB, int late)
    {
        if (HostTests.InstallDir is null) return;
        Meet("before", speedA, speedB, late, near: false);
        var (felt, server, serverOwn, _) = Meet("meet", speedA, speedB, late);
        Assert.False(double.IsNaN(felt), "the prediction never met b");
        Assert.False(double.IsNaN(server), "the server never met b");
        // the drawn cars touch when the prediction feels b: no more apart than the server's own cars are when it meets them
        // (its contact test looks a frame ahead), give or take the tick's travel
        Assert.True(felt < serverOwn + 0.25, $"drawn {felt:0.00} m apart when the prediction met b; the server's {serverOwn:0.00} m");
    }
}

public class CarBoxTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// The car-car box (the .vdf's COLP, read as the original reads it) is the drawn body, within a few centimetres: two
    /// cars meeting are not held apart by their boxes (the Piranha's: x ±0.99 against ±0.97, z −2.37..2.29 against −2.35..2.40).
    /// </summary>
    [Theory]
    [InlineData("vppirna1.vcf")]
    public void TheBoxIsTheBody(string vcf)
    {
        if (HostTests.InstallDir is not { } install) return;
        var vfs = new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(install));
        if (vfs.TryRead(vcf) is null) return;
        var car = I76.Sim.Vehicles.VehicleFactory.Create(vfs, vcf);
        var m = new I76.Formats.Assembly.ModelAssembler(vfs).Assemble(vcf)!;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in m.Parts)
            foreach (var v in p.Mesh.Vertices) { var w = p.ToModel.Apply(v); lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w); }
        output.WriteLine($"{vcf}: box x {car.BoxX[0]:0.00}..{car.BoxX[3]:0.00} z {car.BoxZ[0]:0.00}..{car.BoxZ[3]:0.00} y {car.BoxY[1]:0.00}..{car.BoxY[3]:0.00}; " +
            $"body x {lo.X:0.00}..{hi.X:0.00} z {lo.Z:0.00}..{hi.Z:0.00} y {lo.Y:0.00}..{hi.Y:0.00}");
        foreach (var (box, body) in new[] { (car.BoxX[0], lo.X), (car.BoxX[3], hi.X), (car.BoxZ[0], lo.Z), (car.BoxZ[3], hi.Z) })
            Assert.True(Math.Abs(box - body) < 0.15f, $"box {box:0.00} against the body's {body:0.00}");
    }
}
