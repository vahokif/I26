using I76.Formats.Mission;

namespace I76.Sim.Fsm;

/// <summary>
/// Which of a mission script's camera scenes (pushCam ... popCam) are a player's own moment and which are the story's
/// (docs/multiplayer.md "Personal cut-scenes"). The scripts hold the one camera for both; in co-op a story scene is
/// everyone's (global: the lead is driven, the others wait) but a personal one belongs to the human who triggered it,
/// shows on that player's screen alone and leaves the others playing.
/// <para>
/// A pushCam is <b>personal</b> when, by its bytecode (not the mission's name):
/// <list type="bullet">
/// <item>it sits in a thread's own first routine, whose parameter the scene watches is the player actor (a group slot);</item>
/// <item>the nearest condition before it is on the player: isAirborne (a jump), or isWithin / isWithinNav / isWithinSqNav
/// of a zone (the leave-the-map camera), with nothing in between that yields;</item>
/// <item>everything the scene does until its popCam (or the thread's return) is the camera, a timer, a condition on the
/// player, the skip key, or the player's own CB line (cbFromPrior with the player as speaker), and every object a camera
/// op frames is the player: no teleport, no order to any car, no one else's dialogue, no objective, no flag.</item>
/// </list>
/// Anything else, or anything not resolved (a pushCam in a called routine, a camera on a local), is a story scene: the
/// default when in doubt.
/// </para>
/// </summary>
public static class CoopCameras
{
    public enum Kind { Story, Personal }

    /// <summary>One pushCam: where, its routine, the verdict and the reason (the survey's table in docs/multiplayer.md).</summary>
    public sealed record Site(int Pc, int Routine, Kind Kind, string Trigger, string Frames, string Why);

    private static readonly HashSet<string> Zone = ["iswithinsqnav", "iswithinnav", "iswithin"];
    private static readonly HashSet<string> Condition = ["isairborne", "iswithinsqnav", "iswithinnav", "iswithin", "isdead", "isequal", "isgreater", "islesser",
        "isarrived", "timegreater", "timelesser", "iskeypress", "isshot", "isrammed", "isattacked", "ishit", "hplesser", "isequalid", "isatfollow", "isgroovesfault", "iscbempty", "iswithinenemy"];
    /// <summary>What a personal scene may do besides the camera ops: wait on the clock, watch the player.</summary>
    private static readonly HashSet<string> Allowed = ["popcam", "null", "starttimer", "timegreater", "timelesser", "isairborne", "iswithinsqnav", "iswithinnav", "iswithin", "iskeypress"];
    /// <summary>Camera ops and the argument positions of the objects they frame.</summary>
    private static readonly Dictionary<string, int[]> CamObjects = new()
    {
        ["camobjobj"] = [0, 4], ["camobjdir"] = [0], ["camposobj"] = [2], ["camtransobj"] = [3], ["camf12"] = [0, 1], ["camposdir"] = [], ["camtransdir"] = [],
    };

    /// <summary>Every pushCam of the program, classified. <paramref name="isPlayer"/>: whether an actor index is the player's (a group slot).</summary>
    public static IReadOnlyList<Site> Analyze(FsmProgram p, Func<int, bool> isPlayer)
    {
        var sites = new List<Site>();
        var entries = CoopRoutines.Entries(p).ToList();
        var decoded = new Dictionary<int, List<CoopRoutines.Op>>();
        for (int pc = 0; pc < p.Code.Length; pc++)
        {
            if (p.Code[pc].Op != 13 || Name(p, pc) != "pushcam") continue;
            int ei = entries.FindLastIndex(e => e <= pc);
            int entry = ei < 0 ? 0 : entries[ei], end = ei + 1 < entries.Count ? entries[ei + 1] : p.Code.Length;
            if (!decoded.TryGetValue(entry, out var ops)) decoded[entry] = ops = CoopRoutines.Decode(p, entry, end);
            sites.Add(Classify(p, isPlayer, pc, entry, end, ops));
        }
        return sites;
    }

    /// <summary>A thread's code, followed from its start without calls, does nothing but watch the player and run the camera.</summary>
    private static bool Dedicated(FsmProgram p, int entry)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(entry);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (x < 0 || x >= p.Code.Length || !seen.Add(x) || seen.Count > 600) continue;
            var (op, arg) = p.Code[x];
            switch (op)
            {
                case 8: case 10: stack.Push((int)arg); continue;
                case 9: stack.Push(x + 1); stack.Push((int)arg); continue;
                case 12: continue;
                case 11: return false;
                case 13:
                    string n = Name(p, x);
                    if (!Allowed.Contains(n) && !CamObjects.ContainsKey(n) && n != "pushcam" && n != "cbfromprior") return false;
                    break;
            }
            stack.Push(x + 1);
        }
        return true;
    }

    private static string Name(FsmProgram p, int pc) => p.Code[pc].Op == 13 && (int)p.Code[pc].Arg is var a && a >= 0 && a < p.Prototypes.Count ? p.Prototypes[a].ToLowerInvariant() : "";

    private static Site Classify(FsmProgram p, Func<int, bool> isPlayer, int pc, int entry, int end, List<CoopRoutines.Op> ops)
    {
        var threads = Enumerable.Range(0, p.Threads.Count).Where(t => p.Threads[t].StartPc == entry).ToList();
        // the parameters every thread of this routine binds to the player
        bool PlayerParam(int n) => threads.Count > 0 && threads.All(t => CoopRoutines.CellOf(p, t, n) is var c && c >= 0 && c < p.Data.Length && isPlayer(p.Data[c]));
        bool IsP(CoopRoutines.Arg a) => a.Kind == 'p' && PlayerParam(a.Value);

        // the nearest condition before the scene begins
        int at = ops.FindIndex(o => o.Pc == pc);
        CoopRoutines.Op? cond = null;
        for (int i = at - 1; i >= 0 && ops[i].Name != "<yield>"; i--)
            if (Condition.Contains(ops[i].Name)) { cond = ops[i]; break; }
        string trigger = cond is not { } c0 ? "none (the script's own moment)"
            : c0.Name == "isairborne" && c0.Args.Any(IsP) ? "the player is airborne"
            : Zone.Contains(c0.Name) && c0.Args.Any(IsP) ? "the player is in a zone" : c0.Name;

        // the scene's body: every instruction reachable from the pushCam until its popCam (a return ends it as well)
        var byPc = ops.ToDictionary(o => o.Pc);
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(pc + 1);
        var does = new SortedSet<string>();
        var frames = new SortedSet<string>();
        bool calls = false, others = false;
        string? refusal = null;
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (x < entry || x >= p.Code.Length || !seen.Add(x) || seen.Count > 400) continue;
            var (op, arg) = p.Code[x];
            switch (op)
            {
                case 8: case 10: stack.Push((int)arg); continue;
                case 9: stack.Push(x + 1); stack.Push((int)arg); continue;
                case 12: continue;
                case 11: calls = true; break;
                case 13:
                {
                    string n = Name(p, x);
                    if (n == "popcam") continue;
                    var args = byPc.TryGetValue(x, out var o) ? o.Args : [];
                    if (CamObjects.TryGetValue(n, out var objs))
                    {
                        bool onlyPlayer = true;
                        foreach (int k in objs) if (k < args.Length && !IsP(args[k])) { onlyPlayer = false; others = true; }
                        frames.Add(n + (objs.Length == 0 ? "" : onlyPlayer ? " on the player" : " on others"));
                    }
                    else if (n == "cbfromprior" && args.Length >= 2 && IsP(args[1])) does.Add("the player's own CB line");
                    else if (!Allowed.Contains(n) || n == "isairborne" && !args.Any(IsP)) { does.Add(n); refusal ??= $"does {n}"; }
                    break;
                }
            }
            stack.Push(x + 1);
        }
        string framed = string.Join(" + ", frames) + (does.Count == 0 ? "" : (frames.Count == 0 ? "" : "; ") + string.Join(", ", does));
        Site Story(string why) => new(pc, entry, Kind.Story, trigger, framed, why);

        if (threads.Count == 0) return Story("in a called routine: whose scene it is, is not known here");
        if (cond is null) return Story("begins unconditionally (the script's own moment)");
        if (trigger != "the player is airborne" && trigger != "the player is in a zone") return Story($"triggered by {cond.Value.Name}, not by the player");
        // a zone alone does not make a moment the player's own (a story's cue at a place is the same code): only a thread
        // that does nothing else is (the leave-the-map camera, a jump zone's), or a jump (airborne) anywhere
        if (trigger == "the player is in a zone" && !Dedicated(p, entry)) return Story("a zone's cue inside a thread that does other things too");
        if (calls) return Story("calls a routine");
        if (refusal is not null) return Story(refusal);
        if (frames.Count == 0) return Story("sets no camera on the player");
        if (others) return Story("frames something other than the player");
        return new Site(pc, entry, Kind.Personal, trigger, framed, "a short moment of the player's own, framing their car");
    }

}
