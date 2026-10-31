using I76.Formats.Mission;

namespace I76.Sim.Missions;

/// <summary>The mission's objectives (1 hidden, 2 succeeded, 4 failed) and their text.</summary>
public sealed class Objectives
{
    public const int Hidden = 1, Succeeded = 2, Failed = 4;
    public const string NoteSound = "cnote.wav";
    public ObjectiveText Text { get; }
    public int[] Flags { get; }
    public event Action<string>? Sound;
    public Action<string> Log { get; set; } = _ => { };

    public Objectives(ObjectiveText text)
    {
        Text = text;
        Flags = text.Objectives.Select(o => o.Hidden ? Hidden : 0).ToArray();
    }

    private bool Valid(int n, string what)
    {
        if (n >= 1 && n <= Flags.Length) return true;
        Log($"{what}: objective {n} does not exist");
        return false;
    }

    /// <summary>Mark succeeded and play the note sound; one already succeeded or failed stays as it is.</summary>
    public void Success(int n)
    {
        if (!Valid(n, "success")) return;
        ref int f = ref Flags[n - 1];
        if ((f & (Succeeded | Failed)) != 0) { Log($"success: objective {n} is already {Describe(f)}"); return; }
        if ((f & Hidden) != 0) Log($"success: objective {n} is hidden");
        f |= Succeeded;
        Sound?.Invoke(NoteSound);
    }

    /// <summary>Mark failed, silently; one already failed or succeeded stays as it is.</summary>
    public void Fail(int n)
    {
        if (!Valid(n, "fail")) return;
        ref int f = ref Flags[n - 1];
        if ((f & (Succeeded | Failed)) != 0) { Log($"fail: objective {n} is already {Describe(f)}"); return; }
        if ((f & Hidden) != 0) Log($"fail: objective {n} is hidden");
        f |= Failed;
    }

    public void Reveal(int n)
    {
        if (!Valid(n, "reveal")) return;
        Flags[n - 1] &= ~Hidden;
        Sound?.Invoke(NoteSound);
    }

    public static string Describe(int f) =>
        (f & Failed) != 0 ? "failed" : (f & Succeeded) != 0 ? "succeeded" : (f & Hidden) != 0 ? "hidden" : "open";
}
