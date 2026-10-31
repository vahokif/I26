namespace I76.Formats.Shell;

/// <summary>
/// Deleting a saved car variant (the Nitro shell's VARIANT popup, DELETE, and its twins in the melee
/// form). Variant n of a model is the file &lt;stem&gt;&lt;n&gt;.vcf; the stock ones (n up to the model's built-in count) are
/// refused. The original unlinks addon\&lt;stem&gt;&lt;n&gt;.vcf, then renames each later file down one so the numbers stay
/// contiguous (the loader probes upward until the first gap). Here only the player's own folder is touched, never the
/// install's addon\ folder: a variant that is not a file there cannot be deleted.
/// </summary>
public static class VariantFiles
{
    public enum Result { Deleted, Stock, NotOwned, Failed }

    public static string FileName(string stem, int n) => $"{stem}{n}.vcf";

    /// <summary>The sidecar beside variant n's file (<see cref="I76.Formats.Imaging.CarSidecar"/>).</summary>
    public static string Sidecar(string stem, int n) => I76.Formats.Imaging.CarSidecar.NameOf(FileName(stem, n));

    /// <summary>
    /// Which model of a list (by stem) and which of its <paramref name="counts"/> variants a car file is: <c>&lt;stem&gt;&lt;n&gt;.vcf</c>
    /// in any case, the longest stem that fits (vdillo before vd); null for a name none of the stems make, or a variant past its count.
    /// </summary>
    public static (int Model, int Variant)? Find(IReadOnlyList<string> stems, IReadOnlyList<int> counts, string vcf)
    {
        var name = Path.GetFileName(vcf ?? "");
        (int, int)? best = null;
        int bestLen = -1;
        if (!name.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)) return null;
        for (int m = 0; m < stems.Count && m < counts.Count; m++)
        {
            var stem = stems[m];
            if (stem.Length == 0 || stem.Length <= bestLen || name.Length <= stem.Length + 4 || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
            var digits = name[stem.Length..^4];
            if (digits.Length > 3 || !digits.All(char.IsAsciiDigit) || int.Parse(digits) is var v && (v < 1 || v > counts[m])) continue;
            (best, bestLen) = ((m, v), stem.Length);
        }
        return best;
    }

    /// <summary>
    /// Deletes variant <paramref name="n"/> (1-based) of <paramref name="count"/> and shifts n+1..count down one, in
    /// <paramref name="dir"/>. <paramref name="stock"/> is the model's built-in count.
    /// </summary>
    public static Result Delete(string dir, string stem, int stock, int count, int n)
    {
        if (n <= stock) return Result.Stock;
        if (n > count) return Result.NotOwned;
        // every file from n up must be the player's own, or the shift would mix in the install's
        for (int k = n; k <= count; k++)
            if (!File.Exists(Path.Combine(dir, FileName(stem, k)))) return Result.NotOwned;
        try
        {
            File.Delete(Path.Combine(dir, FileName(stem, n)));
            File.Delete(Path.Combine(dir, Sidecar(stem, n)));
            for (int k = n + 1; k <= count; k++)
            {
                File.Move(Path.Combine(dir, FileName(stem, k)), Path.Combine(dir, FileName(stem, k - 1)), overwrite: true);
                // the variant's I26 sidecar (its paint colours) goes along with it
                if (File.Exists(Path.Combine(dir, Sidecar(stem, k)))) File.Move(Path.Combine(dir, Sidecar(stem, k)), Path.Combine(dir, Sidecar(stem, k - 1)), overwrite: true);
                else File.Delete(Path.Combine(dir, Sidecar(stem, k - 1)));
            }
        }
        catch (IOException) { return Result.Failed; }
        catch (UnauthorizedAccessException) { return Result.Failed; }
        return Result.Deleted;
    }

    /// <summary>
    /// Where the selection goes after variant <paramref name="n"/> of <paramref name="count"/> was deleted: the one that
    /// slid into its place, or the new last (the original clamps its cursor to the new count).
    /// </summary>
    public static int Neighbour(int n, int count) => Math.Clamp(n, 1, Math.Max(1, count - 1));

    /// <summary>A car that used variant <paramref name="v"/> after variant <paramref name="deleted"/> went: later ones shift down, the deleted one takes its neighbour.</summary>
    public static int Remap(int v, int deleted, int newCount) => v > deleted ? v - 1 : v == deleted ? Neighbour(deleted, newCount + 1) : v;
}
