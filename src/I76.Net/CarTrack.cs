using System.Numerics;
using I76.Sim.Math;

namespace I76.Net;

/// <summary>
/// One remote car's states as they came (each of its own tick, <see cref="CarState.SampleTick"/>) and where to draw it.
/// A near car comes every tick, a far one every few (interest management), and any at uneven intervals when the snapshot
/// budget puts it off, so each car is drawn on a clock of its own:
/// the view's, less the car's interval less one tick, so there is a later state to interpolate to; the extra delay
/// changes at most 10% of the time passing, so a car whose rate changes speeds up or slows down a little instead of
/// jumping. With no later state (lost snapshots) the car goes on along its velocity for up to <see cref="ExtrapolateSeconds"/>,
/// then holds. When a state comes that moves where the car would have been drawn, the difference is drawn away over
/// about <see cref="SmoothSeconds"/> rather than jumped (a respawn, or more than <see cref="SnapMetres"/>, is drawn at once).
/// A car near the own predicted car is drawn at the predicted present instead (<see cref="Present"/>): its newest state moved
/// on along its velocity, as the prediction meets it, so the two touch on screen when they touch in the step.
/// </summary>
public sealed class CarTrack
{
    public const double ExtrapolateSeconds = 0.25, SmoothSeconds = 0.1, SnapMetres = 8;
    /// <summary>
    /// Within <see cref="NearMetres"/> of the own car (between their centres, at the present) a car is drawn at the present;
    /// beyond <see cref="FarMetres"/> on its own clock; between, a blend. The present is at most <see cref="LeadSeconds"/>
    /// past the car's newest state (a longer gap holds it there).
    /// </summary>
    public const double NearMetres = 15, FarMetres = 40, LeadSeconds = 0.5;

    /// <summary>The own car's predicted present: the server tick (fractional, as <see cref="CarState.SampleTick"/>) and where it is drawn.</summary>
    public readonly record struct Present(double Tick, Double3 Own);
    private const int MaxSamples = 40, GapWindow = 12;

    public List<CarState> Samples { get; } = [];
    /// <summary>Ticks this car is drawn behind the view.</summary>
    public double Extra { get; private set; }
    /// <summary>
    /// The send interval seen lately: the gap between states that three in four of the last twelve were within. A snapshot
    /// budget sends a car at uneven intervals (it waits a tick or two when the snapshot is full), and a lost snapshot
    /// lengthens one gap: this covers the first and ignores a few of the second.
    /// </summary>
    public int Interval { get; private set; } = 1;
    /// <summary>The correction still being drawn away (metres).</summary>
    public Vector3 Offset { get; private set; }
    /// <summary>Bumped when a state is added.</summary>
    public int Version { get; private set; }
    /// <summary>Frames this car had no later state to draw towards (it went on along its velocity, or held).</summary>
    public int Extrapolated { get; private set; }
    /// <summary>How far towards the present the car was drawn in the last <see cref="Step"/> (0 on its own clock, 1 at the present).</summary>
    public double Lead { get; private set; }
    /// <summary>The states around the car's clock in the last <see cref="Step"/>.</summary>
    public CarState? A { get; private set; }
    public CarState? B { get; private set; }

    private readonly Queue<int> _gaps = new();
    private double _lastT = double.NaN, _lastPresent, _lastLead;
    private Double3 _lastRaw;
    private int _lastVersion, _lastGen = -1;

    /// <summary>A state of this car (a duplicate or one older than what is kept is ignored).</summary>
    public void Add(CarState s)
    {
        uint t = s.SampleTick;
        int i = Samples.Count;
        while (i > 0 && Samples[i - 1].SampleTick > t) i--;
        if (i > 0 && Samples[i - 1].SampleTick == t) return;
        if (i == Samples.Count && i > 0)
        {
            _gaps.Enqueue((int)Math.Min(t - Samples[i - 1].SampleTick, 1000));
            while (_gaps.Count > GapWindow) _gaps.Dequeue();
            var sorted = _gaps.Order().ToArray();
            Interval = Math.Clamp(sorted[(sorted.Length - 1) * 3 / 4], 1, 16);
        }
        Samples.Insert(i, s);
        while (Samples.Count > MaxSamples) Samples.RemoveAt(0);
        Version++;
    }

    /// <summary>
    /// The frame: the car's clock (the view's <paramref name="renderTick"/> less its extra delay) moved on, and where to draw it;
    /// near the own car, towards the <paramref name="present"/>.
    /// </summary>
    public (Rows Rot, Double3 Pos)? Step(double renderTick, double dt, float tickSeconds, Present? present = null)
    {
        if (Samples.Count == 0) return null;
        double maxStep = dt / tickSeconds * 0.1;
        Extra += Math.Clamp(Interval - 1 - Extra, -maxStep, maxStep);
        double t = renderTick - Extra;
        // states the clock has passed are let go, but the one before it and enough to interpolate stay
        while (Samples.Count > 2 && Samples[1].SampleTick <= t - 1) Samples.RemoveAt(0);
        if (Version != _lastVersion && !double.IsNaN(_lastT))
        {
            // a new state moved the curve under where the car was just drawn: draw the difference away
            var (_, was, _) = Drawn(_lastT, _lastPresent, _lastLead, tickSeconds);
            var jump = new Vector3((float)(_lastRaw.X - was.X), (float)(_lastRaw.Y - was.Y), (float)(_lastRaw.Z - was.Z));
            Offset = (Offset + jump).Length() > SnapMetres ? Vector3.Zero : Offset + jump;
        }
        double presentTick = t, lead = 0;
        if (present is { } p)
        {
            // not across a respawn the car's clock has not reached (that is drawn when it does)
            Raw(t, tickSeconds, ExtrapolateSeconds, out var ca, out var cb);
            if ((cb ?? ca)!.Generation == Samples[^1].Generation)
            {
                presentTick = p.Tick;
                var (_, at, _) = Raw(presentTick, tickSeconds, LeadSeconds, out _, out _);
                double d = Math.Sqrt(Math.Pow(at.X - p.Own.X, 2) + Math.Pow(at.Y - p.Own.Y, 2) + Math.Pow(at.Z - p.Own.Z, 2));
                double x = Math.Clamp((FarMetres - d) / (FarMetres - NearMetres), 0, 1);
                lead = x * x * (3 - 2 * x);
            }
        }
        Lead = lead;
        var (rot, pos, extrapolated) = Drawn(t, presentTick, lead, tickSeconds);
        if (extrapolated) Extrapolated++;
        // a respawn or a regen pad (the generation changes) is drawn where it is at once
        int gen = (B ?? A)!.Generation;
        if (gen != _lastGen) Offset = Vector3.Zero;
        _lastGen = gen;
        Offset *= (float)Math.Exp(-dt / SmoothSeconds);
        if (Offset.LengthSquared() < 1e-8f) Offset = Vector3.Zero;
        (_lastT, _lastPresent, _lastLead, _lastRaw, _lastVersion) = (t, presentTick, lead, pos, Version);
        return (rot, new Double3(pos.X + Offset.X, pos.Y + Offset.Y, pos.Z + Offset.Z));
    }

    /// <summary>
    /// The pose on the car's clock <paramref name="t"/> (setting <see cref="A"/> and <see cref="B"/>), taken <paramref name="lead"/>
    /// of the way to its pose at the present <paramref name="presentTick"/>.
    /// </summary>
    private (Rows Rot, Double3 Pos, bool Extrapolated) Drawn(double t, double presentTick, double lead, float tickSeconds)
    {
        var (rot, pos, extrapolated) = Raw(t, tickSeconds, ExtrapolateSeconds, out var a, out var b);
        (A, B) = (a, b);
        if (lead <= 0) return (rot, pos, extrapolated);
        var (rp, pp, _) = Raw(presentTick, tickSeconds, LeadSeconds, out _, out _);
        var q = Quaternion.Slerp(Wire.ToQuat(rot), Wire.ToQuat(rp), (float)lead);
        return (Wire.ToRows(q), new Double3(pos.X + (pp.X - pos.X) * lead, pos.Y + (pp.Y - pos.Y) * lead, pos.Z + (pp.Z - pos.Z) * lead), extrapolated);
    }

    /// <summary>The pose at car tick <paramref name="t"/> from the states alone, going on along the velocity for up to <paramref name="aheadSeconds"/> past the newest.</summary>
    private (Rows Rot, Double3 Pos, bool Extrapolated) Raw(double t, float tickSeconds, double aheadSeconds, out CarState? a, out CarState? b)
    {
        a = null; b = null;
        foreach (var s in Samples)
        {
            if (s.SampleTick <= t) a = s;
            else { b = s; break; }
        }
        if (a is null) return (b!.Rot, b.Pos, false);
        if (b is null || b.Generation != a.Generation)
        {
            if (b is not null) return (b.Rot, b.Pos, false);
            double ahead = Math.Min(t - a.SampleTick, aheadSeconds / tickSeconds) * tickSeconds;
            if (ahead <= 1e-9) return (a.Rot, a.Pos, false);
            return (a.Rot, new Double3(a.Pos.X + a.Vel.X * ahead, a.Pos.Y + a.Vel.Y * ahead, a.Pos.Z + a.Vel.Z * ahead), true);
        }
        double k = (t - a.SampleTick) / (double)(b.SampleTick - a.SampleTick);
        var q = Quaternion.Slerp(Wire.ToQuat(a.Rot), Wire.ToQuat(b.Rot), (float)k);
        return (Wire.ToRows(q), new Double3(a.Pos.X + (b.Pos.X - a.Pos.X) * k, a.Pos.Y + (b.Pos.Y - a.Pos.Y) * k, a.Pos.Z + (b.Pos.Z - a.Pos.Z) * k), false);
    }
}
