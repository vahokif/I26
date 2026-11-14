namespace I76.Game.Boot;

/// <summary>
/// A checkpoint (docs/savestate.md): the session's snapshot, and what of the game's own goes with it (the trip's salvage,
/// the music's place, the view). Kept in memory for a retry, and written into a checkpoint bookmark (<see cref="ToBytes"/>).
/// Pure managed: no engine calls.
/// </summary>
public sealed record Checkpoint(string Mission, byte[] Snapshot, byte[]? Salvage, double? MusicAt, float[]? Chase, bool Cockpit, int LastPreset, string Why, int Tick)
{
    /// <summary>"I26CKPT" and a zero: a checkpoint bookmark's file.</summary>
    private static ReadOnlySpan<byte> Magic => "I26CKPT\0"u8;
    /// <summary>The file's own layout around the snapshot; the snapshot carries its own format version (SnapshotHeader).</summary>
    private const int FileVersion = 1;

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FileVersion);
            // the snapshot first, so that its version is the first thing read after the file's
            w.Write(Snapshot.Length);
            w.Write(Snapshot);
            w.Write(Mission);
            w.Write(Why);
            w.Write(Tick);
            w.Write(Cockpit);
            w.Write(LastPreset);
            w.Write(MusicAt.HasValue);
            if (MusicAt is { } at) w.Write(at);
            w.Write(Chase is not null);
            if (Chase is not null) { w.Write(Chase.Length); foreach (float f in Chase) w.Write(f); }
            w.Write(Salvage is not null);
            if (Salvage is not null) { w.Write(Salvage.Length); w.Write(Salvage); }
        }
        return ms.ToArray();
    }

    /// <summary>Why a kept checkpoint cannot be used.</summary>
    public enum Problem { None, OtherVersion, Unreadable }

    /// <summary>
    /// A checkpoint bookmark's file back, or null with why: the snapshot's format version is checked (SnapshotHeader.Read)
    /// before anything else reads it, so a file from another version of the game is refused here, never thrown at the
    /// mission build. Never throws.
    /// </summary>
    public static Checkpoint? Read(byte[]? data, out Problem problem)
    {
        problem = Problem.Unreadable;
        if (data is null || data.Length < Magic.Length + 4 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(data, Magic.Length, data.Length - Magic.Length));
            if (r.ReadInt32() != FileVersion) { problem = Problem.OtherVersion; return null; }
            int n = r.ReadInt32();
            if (n < 0 || n > data.Length) return null;
            var snapshot = r.ReadBytes(n);
            if (snapshot.Length != n) return null;
            try { I76.Sim.Save.SnapshotHeader.Read(snapshot); }
            catch (I76.Sim.Save.SnapshotMismatchException) { problem = Problem.OtherVersion; return null; }
            string mission = r.ReadString();
            string why = r.ReadString();
            int tick = r.ReadInt32();
            bool cockpit = r.ReadBoolean();
            int lastPreset = r.ReadInt32();
            double? musicAt = r.ReadBoolean() ? r.ReadDouble() : null;
            float[]? chase = null;
            if (r.ReadBoolean())
            {
                int k = r.ReadInt32();
                if (k is < 0 or > 64) return null;
                chase = new float[k];
                for (int i = 0; i < k; i++) chase[i] = r.ReadSingle();
            }
            byte[]? salvage = null;
            if (r.ReadBoolean())
            {
                int k = r.ReadInt32();
                if (k < 0 || k > data.Length) return null;
                salvage = r.ReadBytes(k);
                if (salvage.Length != k) return null;
            }
            problem = Problem.None;
            return new Checkpoint(mission, snapshot, salvage, musicAt, chase, cockpit, lastPreset, why, tick);
        }
        catch (Exception)   // cut short or garbled: whatever the readers throw
        {
            return null;
        }
    }

    /// <summary>
    /// A checkpoint bookmark's launch of <paramref name="mission"/>: the checkpoint to resume at, or null, and then what the player is
    /// told (<paramref name="notice"/>), when it cannot be used (another version's, a damaged file, another mission's): the mission
    /// then starts from its beginning, as the bookmark would without it. Never throws.
    /// </summary>
    public static Checkpoint? ForLaunch(string mission, byte[]? data, out string? notice)
    {
        var cp = Read(data, out var problem);
        if (cp is not null && !cp.Mission.Equals(mission, StringComparison.OrdinalIgnoreCase)) (cp, problem) = (null, Problem.Unreadable);
        notice = cp is null ? Notice(problem) : null;
        return cp;
    }

    /// <summary>What the player is told, once, when a bookmark's checkpoint cannot be used and the mission starts from its beginning.</summary>
    public static string Notice(Problem problem) => problem == Problem.OtherVersion
        ? "This checkpoint is from another version;\nthe mission starts from the beginning."
        : "This checkpoint could not be restored;\nthe mission starts from the beginning.";
}
