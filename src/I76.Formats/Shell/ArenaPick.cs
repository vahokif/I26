namespace I76.Formats.Shell;

/// <summary>The host form's remembered arena: kept by its file, not its place in a list (which another game type, or a changed addon folder, reorders).</summary>
public static class ArenaPick
{
    /// <summary>The index of the arena whose file is <paramref name="file"/>, else 0.</summary>
    public static int IndexOf(IReadOnlyList<(string Name, string Msn)> arenas, string? file)
    {
        if (file is not { Length: > 0 }) return 0;
        for (int i = 0; i < arenas.Count; i++)
            if (arenas[i].Msn.Equals(file, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
