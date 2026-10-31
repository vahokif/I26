using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Weapons;

/// <summary>
/// A cluster bomb's bomblets (the record, 0x124 bytes): the round that landed, the count still to go off
/// (rec[1]) and fourteen slots, each live or not, its point and its time.
/// </summary>
public sealed class Bomblets
{
    public const int Slots = 14;
    public required Round Round;
    /// <summary>rec[1]: n, 8..11 (12 when rand() gives 32767); each bomblet that goes off takes one.</summary>
    public int Left;
    public readonly bool[] Live = new bool[Slots];
    public readonly Vector3[] At = new Vector3[Slots];
    /// <summary>When each goes off, on the world clock; shortened by every frame's delta while it waits.</summary>
    public readonly float[] Due = new float[Slots];
}

public sealed partial class Combat
{
    /// <summary>The pending bomblets, newest first: the original puts each new record at the head.</summary>
    public List<Bomblets> Clusters { get; } = [];

    /// <summary>
    /// A cluster bomb's landing (kind 5, after its own impact): n = 8 − ftol(−4u) bomblets, 8..11, and
    /// slots 0..n filled (n + 1 of them): each at (x ± 10u, the terrain's height, z ± 10u) around the landing point,
    /// going off at now + 1.8u. The rand() draws in the original's order: n, then per slot x, z and the time.
    /// The original computes in the x87 at the single precision Direct3D sets, so rand() = 32767 gives n = 12.
    /// </summary>
    private void ScheduleBomblets(Round r, Vector3 at)
    {
        var b = new Bomblets { Round = r };
        b.Left = 8 - (int)(Rand.Next() * 3.051851e-5f * -4f);
        for (int i = 0; i < Bomblets.Slots; i++)
        {
            if (i > b.Left) continue;
            float x = at.X + (Rand.Next() * 3.051851e-5f * 20f - 10f);
            float z = (Rand.Next() * 3.051851e-5f * 20f - 10f) + at.Z;
            b.At[i] = new Vector3(x, World.Ground.BareHeight(x, z), z);
            b.Due[i] = Rand.Next() * 3.051851e-5f * 1.8f + World.Time;
            b.Live[i] = true;
        }
        Clusters.Insert(0, b);
    }

    /// <summary>
    /// First thing in the rounds' frame: every live slot whose time has not come loses the
    /// frame's delta from it (so the bomblets go off in about half the 1.8 s: a bug, frame-rate.md #16); one whose time
    /// has passed goes off at its point: the round's own impact there (by what the zero-length path test
    /// Met), whose effect is xclst1 (45 splash over 20 m, no falloff). A record is freed once its count
    /// reaches 0 after a pass, so of the n + 1 slots the last to fall due never goes off (unless it went in the same
    /// frame as the one before, when the count passes 0 and the record lives on, empty). Freeing it sets the list's
    /// head to its successor, so any newer record ahead of it is dropped with its bomblets (the original's bug: two
    /// cluster bombs landing within a second of each other lose the second's rest).
    /// </summary>
    private void BombletFrame(float dt)
    {
        for (int k = 0; k < Clusters.Count; k++)
        {
            var b = Clusters[k];
            for (int i = 0; i < Bomblets.Slots; i++)
            {
                if (!b.Live[i]) continue;
                if (World.Time <= b.Due[i]) { b.Due[i] -= dt; continue; }
                var at = b.At[i];
                b.Round.Pos = at;
                OnHit(b.Round, BombletVictim(b.Round, at), at, default, default, bomblet: true);
                b.Left--;
                b.Live[i] = false;
            }
            if (b.Left == 0)
            {
                Clusters.RemoveRange(0, k + 1);
                k = -1;
            }
        }
    }

    /// <summary>
    /// From the bomblet's point to itself: a path of no length. Its first test refuses a
    /// collider when neither the path nor the collider moves, so a parked car never takes the bomblet's packet directly
    /// (only its splash); a moving one would go on to the box test with a zero path, which is not modelled.
    /// </summary>
    private static object? BombletVictim(Round r, Vector3 at) => null;

    // ---- the EZK Mortar ----

    /// <summary>Angles: 120 of them from 0 by π/480, summed in single precision.</summary>
    private static readonly float[] EzkAngles = BuildEzkAngles();

    private static float[] BuildEzkAngles()
    {
        var a = new float[120];
        float f = 0;
        for (int i = 0; i < a.Length; i++) { a[i] = f; f = f - -0.006544985f; }
        return a;
    }

    /// <summary>
    /// Before every EZK shot: the elevation that lands the round nearest the target.
    /// The muzzle at its base pose; v = its forward turned up about its right axis by the Cayley form with
    /// g = right·(θ/2) (so the turn is 2·atan(θ/2), not θ) times v0; a 4 g ballistic flight on the frame delta
    /// (no drag, no approach to a terminal speed) to the terrain, + 0.1; the target's aim point
    /// moved on by its velocity times that flight's time. If the target is out of the 45° shot's reach
    /// (horizontally) the answer is π/4. Otherwise each of the 120 angles from the highest down, the nearest miss
    /// (3D) winning, ties to the higher.
    /// </summary>
    private float EzkElevation(Vehicle v, Weapon w, object target, float v0)
    {
        var basis = Rows.Concat(w.Style == 3 ? Rows.Concat(Rows.AboutY(w.AimYaw), w.BaseRot) : w.BaseRot, v.Rot);
        var muzzle = v.Pos.ToVector3() + v.Rot.ToWorld(w.MuzzlePos);
        var aim = AimPointOf(target);
        var tvel = target is Vehicle tv ? tv.Vel : Vector3.Zero;
        var r = basis.Right; var f = basis.Forward;

        Vector3 Launch(float half)
        {
            float g0 = r.X * half, g1 = r.Y * half, g2 = r.Z * half;
            float k = 2f / ((g2 * g2 + g1 * g1 + g0 * g0) - -1f);
            float dot = g1 * f.Y + g2 * f.Z + g0 * f.X;
            return new Vector3(
                ((dot * g0 + ((g2 * f.Y + f.X) - g1 * f.Z)) * k - f.X) * v0,
                ((dot * g1 + ((g0 * f.Z + f.Y) - g2 * f.X)) * k - f.Y) * v0,
                ((dot * g2 + ((g1 * f.X + f.Z) - g0 * f.Y)) * k - f.Z) * v0);
        }
        static float Planar(Vector3 a, Vector3 b) => MathF.Sqrt((a.Z - b.Z) * (a.Z - b.Z) + (a.X - b.X) * (a.X - b.X));

        var (t45, land45) = Lob(muzzle, Launch(0.39269906f));
        if (Planar(aim + tvel * t45, muzzle) >= Planar(land45, muzzle)) return 0.7853981f;
        float best = 0, bestMiss = float.MaxValue;
        for (int i = EzkAngles.Length - 1; i >= 0; i--)
        {
            var (t, land) = Lob(muzzle, Launch(EzkAngles[i] * 0.5f));
            float miss = Vector3.Distance(land, aim + tvel * t);
            if (miss < bestMiss) { best = EzkAngles[i]; bestMiss = miss; }
        }
        return best;
    }

    /// <summary>Steps of the frame delta under −39.2 until at or under the bare terrain; the landing is its height + 0.1.</summary>
    private (float T, Vector3 At) Lob(Vector3 p, Vector3 vel)
    {
        float dt = FrameDt > 0 ? FrameDt : 0.05f, t = 0, ground;
        int steps = 0;
        do
        {
            p.X += vel.X * dt;
            p.Y += (vel.Y - dt * 39.2f * 0.5f) * dt;
            p.Z += vel.Z * dt;
            vel.Y += dt * -39.2f;
            t += dt;
            ground = World.Ground.BareHeight(p.X, p.Z);
        } while (ground < p.Y && ++steps < 100000);
        p.Y = ground + 0.1f;
        return (t, p);
    }
}
