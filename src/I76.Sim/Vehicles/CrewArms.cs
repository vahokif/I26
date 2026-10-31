namespace I76.Sim.Vehicles;

/// <summary>
/// One of the driver's arms (right, left; state block
/// {busy, cur, tgt, axis, …, next}): the .tmt's axis 0 is the raise
/// (ZH?45{v}01..06, frames 0..5), axis 1 the shot (ZS?45{v}01..03, 0..2).
/// </summary>
public sealed class CrewArm
{
    public bool Busy;
    public int Cur, Tgt, Axis;
    /// <summary>When the strip next steps, on the arms' own clock.</summary>
    public double Next;
}

/// <summary>
/// The driver's two arms, which gate the weapons: the
/// hardpoints answer only while both arms are down and still, a raised,
/// still arm's handgun takes the fire key. Simulation state, stepped once a
/// tick from the driver's input (the cockpit's glance raises an arm to its
/// window); the cockpit only draws the frames. Channel 1 is the right arm,
/// 2 the left.
/// </summary>
public sealed class CrewArms
{
    /// <summary>A frame of the strip lasts a tenth of a second.</summary>
    public const double FrameTime = 0.1;

    public CrewArm Right = new(), Left = new();
    /// <summary>The arms' clock: advanced only while they are stepped (in the cockpit).</summary>
    public double Clock;

    public CrewArm this[int channel] => channel == 1 ? Right : Left;

    /// <summary>The arm is down and still (the hardpoints may fire).</summary>
    public bool Idle(int ch) { var a = this[ch]; return !a.Busy && a.Axis == 0 && a.Cur == 0 && a.Tgt == 0; }
    /// <summary>And not: raised to the window and not mid-shot (its handgun may fire).</summary>
    public bool Ready(int ch) { var a = this[ch]; return !a.Busy && a.Axis == 0 && a.Cur == 5 && a.Tgt == 5; }

    /// <summary>Aim for the raised or lowered frame, unless a shot is playing.</summary>
    public void Want(int ch, bool raised)
    {
        var a = this[ch];
        if (!a.Busy) { a.Tgt = raised ? 5 : 0; a.Axis = 0; }
    }

    /// <summary>From the shot routine: the shot frames 0, 1, 2, a tenth of a second each, from now.</summary>
    public void Shoot(int ch)
    {
        var a = this[ch];
        if (a.Busy) return;
        a.Busy = true; a.Axis = 1; a.Cur = -1; a.Tgt = 2;
        a.Next = Clock - FrameTime;
    }

    /// <summary>Both down at once: out of the cockpit nothing steps them.</summary>
    public void Reset()
    {
        foreach (var a in (CrewArm[])[Right, Left]) { a.Busy = false; a.Cur = a.Tgt = a.Axis = 0; a.Next = Clock; }
    }

    /// <summary>
    /// The strip's callback: each 0.1 s that has passed steps
    /// cur one frame towards tgt (the direction taken from before the steps,
    /// so a late catch-up runs to the end of the strip); axis 0 stops at the
    /// raised frame 5, and a shot that has reached its last frame returns to it.
    /// </summary>
    public void Step(double dt)
    {
        Clock += dt;
        foreach (var a in (CrewArm[])[Right, Left])
        {
            if (a.Next >= Clock) continue;
            int c0 = a.Cur, t0 = a.Tgt;
            double t;
            do
            {
                t = a.Next;
                a.Next = t + FrameTime;
                if (t0 < c0 && a.Cur > 0) a.Cur--;
                if (c0 < t0 && a.Cur < 6) a.Cur++;
            } while (t + FrameTime <= Clock);
            if (a.Axis == 0 && a.Cur > 5) a.Cur = 5;
            if (a.Axis == 1 && t0 <= c0) { a.Cur = 5; a.Tgt = 5; a.Axis = 0; a.Busy = false; }
        }
    }

    /// <summary>Every field from another (a snapshot's restore).</summary>
    public void CopyFrom(CrewArms o)
    {
        Clock = o.Clock;
        foreach (var (a, b) in ((CrewArm, CrewArm)[])[(Right, o.Right), (Left, o.Left)])
            (a.Busy, a.Cur, a.Tgt, a.Axis, a.Next) = (b.Busy, b.Cur, b.Tgt, b.Axis, b.Next);
    }

    public CrewArms Clone() { var c = new CrewArms(); c.CopyFrom(this); return c; }
}
