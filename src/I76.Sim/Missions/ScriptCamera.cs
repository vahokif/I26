using System.Numerics;
using I76.Sim.Math;

namespace I76.Sim.Missions;

/// <summary>
/// The scripted camera (FSM opcodes 52–61):
/// pushCam saves the view and takes over, popCam gives it back. Each camera
/// op sets a placement the camera holds (or travels) until the next one.
/// Positions are in the game frame; the view either looks at a point or
/// takes a basis (rows: right, up, forward).
/// </summary>
public sealed partial class ScriptCamera
{
    public const int StackDepth = 8;
    private readonly Func<double, double, float> _ground;
    private string _op = "";
    private int[] _a = [];
    private Entity?[] _o = [];
    private IReadOnlyList<Vector3>? _path;
    private bool _arrived;
    // the state: the path it is on, the segment and the
    // parameter along it, the point that runs along the path, and the camera's own x/z easing after it
    private IReadOnlyList<Vector3>? _onPath;
    private int _seg;
    private double _t, _invLen;
    private Vector2 _target, _dir, _cam;

    public ScriptCamera(Func<double, double, float> ground) => _ground = ground;

    public int Depth { get; private set; }
    /// <summary>A network client's mirror: the view is this player's own camera scene, not the story's (nothing of it is the host's to skip).</summary>
    public bool Personal { get; set; }
    public string Op => _op;
    public bool Active => Depth > 0 && _op.Length > 0;
    public Vector3 Eye { get; private set; }
    public Vector3? LookAt { get; private set; }
    public Rows? Basis { get; private set; }
    /// <summary>The previous tick's placement, for drawing between ticks (as the cars are); equal to the current one after a cut.</summary>
    public Vector3 PrevEye { get; private set; }
    public Vector3? PrevLookAt { get; private set; }
    private bool _cut = true;
    public Action<string> Log { get; set; } = _ => { };

    /// <summary>A placement held after a popCam back into an outer scene, until the script sets the next op.</summary>
    public const string HeldOp = "(held)";
    private readonly List<(bool Placed, Vector3 Eye, Vector3? LookAt, Rows? Basis)> _stack = [];

    /// <summary>
    /// The camera's state is saved (the scene's placement, or none outside a scene),
    /// the travel state and camIsArrived are cleared, and the script camera takes the view.
    /// </summary>
    public void Push()
    {
        if (Depth >= StackDepth) { Log("pushCam: camera stack Underfow"); return; }
        _stack.Add((Active, Eye, LookAt, Basis));
        Depth++;
        _arrived = false; _onPath = null;
    }

    /// <summary>
    /// The state saved by the matching push comes back. Outside every scene that is the
    /// game's own camera; inside an outer scene it is that scene's placement, held still until the next op. The
    /// travel state and camIsArrived are cleared.
    /// </summary>
    public void Pop()
    {
        if (Depth == 0) { Log("popCam: camera stack 0verfow"); return; }
        Depth--;
        var s = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        _arrived = false; _onPath = null;
        _path = null; _a = []; _o = [];
        _cut = true;
        if (Depth > 0 && s.Placed) { _op = HeldOp; Eye = s.Eye; _heldLook = s.LookAt; _heldBasis = s.Basis; }
        else _op = "";
    }

    private Vector3? _heldLook;
    private Rows? _heldBasis;

    /// <summary>
    /// A network client's camera: where the host's script camera was at two ticks (the view draws between them), and
    /// whether it holds the view. Nothing of the script's own state is kept.
    /// </summary>
    public void Mirror(bool active, Vector3 prevEye, Vector3? prevLookAt, Vector3 eye, Vector3? lookAt, Rows? basis)
    {
        Depth = active ? 1 : 0;
        _op = active ? "(host)" : "";
        (PrevEye, PrevLookAt, Eye, LookAt, Basis) = (prevEye, prevLookAt, eye, lookAt, basis);
    }

    public bool ConsumeArrived() { bool a = _arrived; _arrived = false; return a; }

    public void Set(string op, int[] ints, Entity?[] objs, IReadOnlyList<Vector3>? path)
    {
        if (_op != op.ToLowerInvariant()) _cut = true;
        _op = op.ToLowerInvariant(); _a = ints; _o = objs; _path = path;
        // the scripts issue a travelling op every frame until it arrives: progress is kept while the path stays the same
        // only pushCam takes the view (camera mode 5); an op on an un-pushed camera only sets its placement
    }

    private int A(int i) => i < _a.Length ? _a[i] : 0;
    private Entity? O(int i) => i < _o.Length ? _o[i] : null;
    private static float Angle(int v) => v % 36000 * MathF.PI / 18000f;
    private float Ground(Vector3 p) => _ground(p.X, p.Z);

    private Vector3 AtHeight(Vector3 p, int h)
    {
        float g = Ground(p);
        return p with { Y = MathF.Max(g + h / 100f, g + 0.5f) };
    }

    private Vector3 Clamp(Vector3 p) => p with { Y = MathF.Max(p.Y, Ground(p) + 0.5f) };

    public void Update(float dt)
    {
        if (!Active) { _cut = true; return; }
        PrevEye = Eye; PrevLookAt = LookAt;
        LookAt = null; Basis = null;
        try { Place(dt); } finally { EndUpdate(); }
    }

    private void Place(float dt)
    {
        switch (_op)
        {
            case HeldOp:
                LookAt = _heldLook; Basis = _heldBasis;
                break;
            case "camobjobj" when O(0) is { } from:
                Eye = Clamp(from.Position + from.Rot.ToWorld(new Vector3(A(1), A(2), A(3)) / 100f));
                LookAt = O(4)?.Position;
                break;
            case "camobjdir" when O(0) is { } o:
                Eye = Clamp(o.Position + o.Rot.ToWorld(new Vector3(A(1), A(2), A(3)) / 100f));
                Basis = Rows.Concat(Rows.Euler(Angle(A(4)), Angle(A(6)), Angle(A(5))), o.Rot);
                break;
            case "camposobj":
                // With no speed: the same path state, holding still
                FollowPath(dt, 0, A(1), O(2));
                break;
            case "camposdir":
                if (_path is { Count: > 0 }) Eye = AtHeight(_path[0], A(1));
                Basis = Rows.Euler(Angle(A(2)), Angle(A(4)), Angle(A(3)));
                break;
            case "camtransobj":
                FollowPath(dt, A(2) / 100f, A(1), O(3));
                break;
            case "camtransdir":
                FollowPath(dt, A(2) / 100f, A(1), null);
                Basis = Rows.Euler(Angle(A(3)), Angle(A(5)), Angle(A(4)));
                break;
            case "camf12" when O(0) is { } a && O(1) is { } b:
                (Eye, LookAt) = F12(a.Position, b.Position, A(2) / 100f, A(3) / 100f);
                break;
        }
    }

    /// <summary>
    /// camF12 a b dist h: the view looks along f = b − a + (0, 2, 0) from
    /// P − dist·f̂, P = a + (0, h, 0) (no roll). Within 33.3 (1/|f| &gt; 0.03) the view and
    /// the eye's offset are turned about P to look down by min((1/|f| − 0.03)·5, 30°)
    /// (the original's rotation composition). The eye stays 0.5 above the ground.
    /// </summary>
    internal (Vector3 Eye, Vector3 LookAt) F12(Vector3 a, Vector3 b, float dist, float h)
    {
        var d = b - a + new Vector3(0, 2, 0);
        float len = d.Length();
        if (len < 1e-6f) d = new Vector3(0, 0, 1);
        var f = d / MathF.Max(len, 1e-6f);
        float inv = 1f / MathF.Max(len, 1e-6f);
        if (inv > 0.03f)
        {
            float pitch = MathF.Min((inv - 0.03f) * 5f, 0.5235988f);
            float hl = MathF.Sqrt(f.X * f.X + f.Z * f.Z);
            if (hl > 1e-6f)
            {
                float el = MathF.Atan2(f.Y, hl) - pitch;
                f = new Vector3(f.X / hl * MathF.Cos(el), MathF.Sin(el), f.Z / hl * MathF.Cos(el));
            }
        }
        var eye = Clamp(a + new Vector3(0, h, 0) - f * dist);
        return (eye, eye + f * 100f);
    }

    /// <summary>After a cut the previous placement is the current one: nothing to blend from.</summary>
    private void EndUpdate()
    {
        if (_cut) { PrevEye = Eye; PrevLookAt = LookAt; _cut = false; }
    }

    /// <summary>
    /// The per-frame path step (the original sets up a segment). On a new path the
    /// point starts at nav[0] and the camera with it. Each frame:
    /// <list type="number">
    /// <item>Past the end of a segment (t &gt; 1): if the path has no point
    /// after the next, the op has arrived. It stops, forgets the path and
    /// leaves the camera where it was. Otherwise it moves on to the next
    /// segment, keeping the fraction.</item>
    /// <item>The camera stands at its own x/z, ground + h/100 (at least
    /// ground + 0.5), looking at the target.</item>
    /// <item>The camera's x/z then eases 0.3 of the way to the point (per
    /// frame, not per second). The point advances by dt·speed along the
    /// segment's x/z, the parameter by that over the segment's length.</item>
    /// </list>
    /// </summary>
    private void FollowPath(float dt, float speed, int h, Entity? at)
    {
        if (_path is not { Count: > 0 } p) return;
        if (!SamePath(_onPath, p))
        {
            Segment(p, 0, 0);
            _cam = _target;
            _onPath = p;
        }
        double inc = dt * speed * _invLen;
        if (_t > 1.0)
        {
            if (p.Count <= _seg + 2)
            {
                _onPath = null; _arrived = true;
                LookAt = PrevLookAt;          // nothing placed this frame: the view holds
                return;
            }
            double f = _t - System.Math.Round(_t);
            if (f < 0) f += 1;
            Segment(p, (float)f, _seg + 1);
        }
        Eye = AtHeight(new Vector3(_cam.X, 0, _cam.Y), h);
        LookAt = at?.Position;
        if (MathF.Abs(_target.X - _cam.X) >= 0.005f) _cam.X += 0.3f * (_target.X - _cam.X);
        if (MathF.Abs(_target.Y - _cam.Y) >= 0.005f) _cam.Y += 0.3f * (_target.Y - _cam.Y);
        _t += inc;
        _target += _dir * (float)inc;
    }

    private void Segment(IReadOnlyList<Vector3> p, float t, int seg)
    {
        _seg = seg;
        var a = p[System.Math.Min(seg, p.Count - 1)];
        var b = p[System.Math.Min(seg + 1, p.Count - 1)];
        _dir = new Vector2(b.X - a.X, b.Z - a.Z);
        _t = t;
        _target = new Vector2(a.X, a.Z) + _dir * t;
        float l2 = _dir.LengthSquared();
        _invLen = l2 > 0 ? 1 / MathF.Sqrt(l2) : 0;
    }

    private static bool SamePath(IReadOnlyList<Vector3>? a, IReadOnlyList<Vector3> b) =>
        ReferenceEquals(a, b) || (a is not null && a.Count == b.Count && a.Count > 0 && a[0] == b[0] && a[^1] == b[^1]);
}
