using I76.Formats.Mission;

namespace I76.Sim.Fsm;

/// <summary>
/// The gates a mission script shuts behind the player (docs/multiplayer.md "Co-op", "Gates shut behind the player"):
/// a position test (isWithin, isWithinNav, isWithinSqNav) whose branch runs triggerGate before anything else is tested
/// or the thread yields. S06's nowayback (within 10 of twopill), T07's gate1 (within 600 of gateblock) and the Nitro
/// trainer B01's four walls (within 25–50 of each gateshut point) are the stock missions' ones; P19's escaping target
/// opens its wall the same way, a test on a computer car, so a site is one where a thread binds the test to the player.
/// </summary>
public static class CoopGates
{
    /// <summary>A site: the test's exec, and the triggerGate's argument instruction (its op, 4 or 5, and its operand).</summary>
    public readonly record struct Site(int Test, int GatePc, uint ArgOp, int Arg);

    private static readonly string[] Tests = ["iswithin", "iswithinnav", "iswithinsqnav"];

    /// <param name="isPlayer">Whether an actor index is the player's (a group slot).</param>
    public static IReadOnlyDictionary<int, Site> Analyze(FsmProgram p, Func<int, bool> isPlayer)
    {
        var sites = new Dictionary<int, Site>();
        var entries = CoopRoutines.Entries(p).ToList();
        string Name(int a) => a >= 0 && a < p.Prototypes.Count ? p.Prototypes[a].ToLowerInvariant() : "";
        for (int pc = 0; pc < p.Code.Length; pc++)
        {
            if (p.Code[pc].Op != 13 || !Tests.Contains(Name((int)p.Code[pc].Arg))) continue;
            // the test, its temporaries freed, then the branch on it (a negated test is a "nobody near", not a pass)
            int at = pc + 1;
            while (at < p.Code.Length && p.Code[at].Op == 7) at++;
            if (at >= p.Code.Length || p.Code[at].Op != 9) continue;
            int end = (int)p.Code[at].Arg;
            // the branch's statements up to the first triggerGate, nothing tested or yielded before it
            for (int k = at + 1; k < end && k < p.Code.Length; k++)
            {
                var (op, a) = p.Code[k];
                if (op is 8 or 9 or 10 or 11 or 12) break;
                if (op == 13 && Name((int)a) == "triggergate")
                {
                    if (k > 0 && p.Code[k - 1].Op is 4 or 5 && OnThePlayer(p, entries, pc, isPlayer)) sites[pc] = new Site(pc, k, p.Code[k - 1].Op, (int)p.Code[k - 1].Arg);
                    break;
                }
                if (op == 13 && Tests.Contains(Name((int)a))) break;
            }
        }
        return sites;
    }

    /// <summary>Whether a thread running the routine the test is in passes the player as one of its arguments (a parameter, fp − n).</summary>
    private static bool OnThePlayer(FsmProgram p, List<int> entries, int test, Func<int, bool> isPlayer)
    {
        var pars = new List<int>();
        for (int k = test - 1; k >= 0 && p.Code[k].Op is 1 or 4 or 5; k--)
            if (p.Code[k].Op == 5 && (int)p.Code[k].Arg < 0) pars.Add(-(int)p.Code[k].Arg);
        int i = entries.BinarySearch(test);
        int entry = i >= 0 ? entries[i] : ~i > 0 ? entries[~i - 1] : -1;
        for (int t = 0; t < p.Threads.Count; t++)
            if (p.Threads[t].StartPc == entry)
                foreach (int n in pars)
                    if (CoopRoutines.CellOf(p, t, n) is var c and >= 0 && c < p.Data.Length && isPlayer(p.Data[c])) return true;
        return false;
    }
}
