using System.Numerics;

namespace I76.Formats.Vegetation;

public enum SegmentKind : byte { Wood, Joint }

/// <summary>A drawn stretch of stem, from <see cref="A"/> to <see cref="B"/>, with the turtle's side vector for the tube's seam.</summary>
public readonly record struct Segment(Vector3 A, Vector3 B, float Ra, float Rb, int Depth, SegmentKind Kind, Vector3 Side);

/// <summary>A foliage cluster: where, which way the twig points, and how big.</summary>
public readonly record struct Leaf(Vector3 Pos, Vector3 Dir, Vector3 Up, float Size, int Depth);

/// <summary>A flat cactus pad: from its base along <see cref="Dir"/>, facing <see cref="Normal"/>.</summary>
public readonly record struct Pad(Vector3 Base, Vector3 Dir, Vector3 Normal, float Length, float Width, int Depth);

/// <summary>A fruit or flower: a small ellipsoid at a point.</summary>
public readonly record struct Knob(Vector3 Pos, Vector3 Dir, float Size);

/// <summary>What a turtle drew: the plant's skeleton, in metres, Y up, the base at the origin.</summary>
public sealed class PlantSkeleton
{
    public List<Segment> Segments { get; } = [];
    public List<Leaf> Leaves { get; } = [];
    public List<Pad> Pads { get; } = [];
    public List<Knob> Knobs { get; } = [];

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        void Add(Vector3 p, float r) { mn = Vector3.Min(mn, p - new Vector3(r)); mx = Vector3.Max(mx, p + new Vector3(r)); }
        foreach (var s in Segments) { Add(s.A, s.Ra); Add(s.B, s.Rb); }
        foreach (var l in Leaves) Add(l.Pos, l.Size * 0.5f);
        foreach (var p in Pads) { Add(p.Base, p.Width * 0.5f); Add(p.Base + p.Dir * p.Length, p.Width * 0.5f); }
        foreach (var k in Knobs) Add(k.Pos, k.Size);
        return Segments.Count + Leaves.Count + Pads.Count + Knobs.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (mn, mx);
    }
}

/// <summary>
/// The 3D turtle that reads an L-system string (ABOP ch. 1.5): heading H, left L and up U, starting at the origin
/// pointing up (+Y). Commands, each with an optional parameter in parentheses:
/// <list type="table">
/// <item><term>F(l)</term><description>a woody segment of length l (default: the current length); then the tropism bends H.</description></item>
/// <item><term>J(l)</term><description>a cactus joint (cholla) of length l.</description></item>
/// <item><term>f(l)</term><description>move without drawing.</description></item>
/// <item><term>P(l)</term><description>a flat pad (prickly pear) l long and <see cref="PadWidth"/> l wide; the turtle moves to its tip.</description></item>
/// <item><term>L(s)</term><description>a foliage cluster of size s (default: the current leaf size).</description></item>
/// <item><term>K(s)</term><description>a fruit or flower of size s.</description></item>
/// <item><term>+ -</term><description>turn left / right about U (default <see cref="Angle"/>).</description></item>
/// <item><term>&amp; ^</term><description>pitch down / up about L.</description></item>
/// <item><term>\ /</term><description>roll left / right about H (default <see cref="Roll"/>).</description></item>
/// <item><term>|</term><description>turn around.</description></item>
/// <item><term>$</term><description>roll so that L is horizontal.</description></item>
/// <item><term>[ ]</term><description>push / pop the state.</description></item>
/// <item><term>!(f) '(f) ~(f)</term><description>scale the radius, the length, the leaf size (defaults: the decays).</description></item>
/// </list>
/// Every turn is jittered by up to <see cref="Jitter"/> degrees and every step by <see cref="LengthJitter"/>, from the seeded stream.
/// </summary>
public sealed class Turtle
{
    public float Angle { get; init; } = 25f;
    public float Roll { get; init; } = 137.5f;
    public float Jitter { get; init; } = 6f;
    public float LengthJitter { get; init; } = 0.15f;
    public float Length { get; init; } = 0.2f;
    public float Radius { get; init; } = 0.02f;
    public float LeafSize { get; init; } = 0.2f;
    public float LengthDecay { get; init; } = 0.85f;
    public float WidthDecay { get; init; } = 0.7f;
    public float LeafDecay { get; init; } = 0.9f;
    /// <summary>Each segment's end radius is its start's times this (never under <see cref="MinRadius"/>).</summary>
    public float Taper { get; init; } = 0.92f;
    public float MinRadius { get; init; } = 0.002f;
    /// <summary>The tropism (ABOP eq. 2.3): after each segment H turns toward this vector by <see cref="Susceptibility"/> per metre.</summary>
    public Vector3 Tropism { get; init; } = Vector3.UnitY;
    public float Susceptibility { get; init; }
    public float PadWidth { get; init; } = 0.75f;
    /// <summary>Segments and pads go no lower than this above the ground (a branch bent down rests on it).</summary>
    public float Floor { get; init; } = -0.05f;

    private struct State
    {
        public Vector3 P, H, L, U;
        public float Len, Rad, Leaf;
        public int Depth;
    }

    public PlantSkeleton Run(string program, ref VegRng rng)
    {
        var sk = new PlantSkeleton();
        var st = new State { P = Vector3.Zero, H = Vector3.UnitY, L = -Vector3.UnitX, U = Vector3.UnitZ, Len = Length, Rad = Radius, Leaf = LeafSize };
        var stack = new Stack<State>();
        foreach (var (sym, par) in LSystem.Tokens(program))
        {
            float? v = LSystem.Number(par);
            switch (sym)
            {
                case 'F':
                case 'J':
                {
                    float len = (v ?? st.Len) * (1 + LengthJitter * rng.Bell());
                    var b = st.P + st.H * len;
                    if (b.Y < Floor) { b.Y = Floor; }
                    float rb = MathF.Max(st.Rad * Taper, MinRadius);
                    sk.Segments.Add(new Segment(st.P, b, st.Rad, rb, st.Depth, sym == 'J' ? SegmentKind.Joint : SegmentKind.Wood, st.L));
                    st.P = b; st.Rad = rb;
                    Bend(ref st, len);
                    break;
                }
                case 'f':
                    st.P += st.H * (v ?? st.Len);
                    break;
                case 'P':
                {
                    float len = (v ?? st.Len) * (1 + LengthJitter * rng.Bell());
                    var b = st.P;
                    if (b.Y + st.H.Y * len < Floor) { /* a pad never grows into the ground: tip it up */ Pitch(ref st, -20f); }
                    sk.Pads.Add(new Pad(b, st.H, st.U, len, len * PadWidth * (1 + 0.1f * rng.Bell()), st.Depth));
                    // the next pad grows off this one's rim: at its tip, the same width either way of it
                    st.P = b + st.H * len * 0.92f;
                    Bend(ref st, len);
                    break;
                }
                case 'L':
                    sk.Leaves.Add(new Leaf(st.P, st.H, st.U, (v ?? st.Leaf) * (1 + 0.2f * rng.Bell()), st.Depth));
                    break;
                case 'K':
                    sk.Knobs.Add(new Knob(st.P, st.H, v ?? st.Rad * 2));
                    break;
                case '+': Yaw(ref st, (v ?? Angle) + Jitter * rng.Bell()); break;
                case '-': Yaw(ref st, -(v ?? Angle) + Jitter * rng.Bell()); break;
                case '&': Pitch(ref st, (v ?? Angle) + Jitter * rng.Bell()); break;
                case '^': Pitch(ref st, -(v ?? Angle) + Jitter * rng.Bell()); break;
                case '\\': RollBy(ref st, (v ?? Roll) + Jitter * rng.Bell()); break;
                case '/': RollBy(ref st, -(v ?? Roll) + Jitter * rng.Bell()); break;
                case '|': Yaw(ref st, 180f); break;
                case '$':
                {
                    var l = Vector3.Cross(Vector3.UnitY, st.H);
                    if (l.LengthSquared() > 1e-6f)
                    {
                        st.L = Vector3.Normalize(l);
                        st.U = Vector3.Cross(st.H, st.L);
                    }
                    break;
                }
                case '[': stack.Push(st); st.Depth++; break;
                case ']': if (stack.Count > 0) st = stack.Pop(); break;
                case '!': st.Rad *= v ?? WidthDecay; break;
                case '\'': st.Len *= v ?? LengthDecay; break;
                case '~': st.Leaf *= v ?? LeafDecay; break;
                default: break;   // growth symbols (A, B, X...) draw nothing
            }
        }
        return sk;
    }

    private void Bend(ref State st, float len)
    {
        if (Susceptibility == 0) return;
        var h = st.H + Tropism * (Susceptibility * len);
        if (h.LengthSquared() < 1e-8f) return;
        var nh = Vector3.Normalize(h);
        // keep the frame orthonormal: rotate L and U by the same turn as H
        var axis = Vector3.Cross(st.H, nh);
        float s = axis.Length();
        if (s < 1e-6f) return;
        float ang = MathF.Asin(MathF.Min(1f, s));
        axis /= s;
        st.L = Rotate(st.L, axis, ang);
        st.U = Rotate(st.U, axis, ang);
        st.H = nh;
    }

    private static void Yaw(ref State st, float deg)
    {
        float a = deg * MathF.PI / 180f;
        st.H = Rotate(st.H, st.U, a);
        st.L = Rotate(st.L, st.U, a);
    }

    private static void Pitch(ref State st, float deg)
    {
        float a = deg * MathF.PI / 180f;
        st.H = Rotate(st.H, st.L, a);
        st.U = Rotate(st.U, st.L, a);
    }

    private static void RollBy(ref State st, float deg)
    {
        float a = deg * MathF.PI / 180f;
        st.L = Rotate(st.L, st.H, a);
        st.U = Rotate(st.U, st.H, a);
    }

    /// <summary>Rodrigues' rotation of v about the unit axis k by a radians.</summary>
    public static Vector3 Rotate(Vector3 v, Vector3 k, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return v * c + Vector3.Cross(k, v) * s + k * (Vector3.Dot(k, v) * (1 - c));
    }
}
