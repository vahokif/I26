using System.Text;
using I76.Formats.Save;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The original's bookmarks: savegame.dir and saveNNN.cmp, built here byte by byte (no game data).</summary>
public sealed class OriginalSaveTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("i26orig").FullName;
    public void Dispose() => Directory.Delete(_d, true);

    // ---- writers for the test files, field for field as the original's shell lays them out

    private static void Str(byte[] b, int at, int width, string s, string stale = "")
    {
        var full = Encoding.Latin1.GetBytes(s + "\0" + stale);
        Array.Copy(full, 0, b, at, Math.Min(full.Length, width));
    }

    private static void I32(byte[] b, int at, int v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);

    internal static byte[] Dir(params (int Scene, string Name, string File, int State)[] rows) => DirPadded(0, rows);

    internal static byte[] DirPadded(int slack, params (int Scene, string Name, string File, int State)[] rows)
    {
        var b = new byte[4 + 60 * rows.Length + slack];
        I32(b, 0, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            int p = 4 + 60 * i;
            I32(b, p, rows[i].Scene);
            Str(b, p + 4, 32, rows[i].Name, stale: "OLD NAME");      // the buffer is written whole: stale bytes after the NUL
            Str(b, p + 36, 16, rows[i].File);
            I32(b, p + 52, rows[i].State);
        }
        return b;
    }

    internal sealed record Part(int Cond, int State, string Name, int Type, string Class, string Def, int Full, float Weight);

    private static void WritePart(byte[] b, int at, Part p)
    {
        I32(b, at, 0x0badf00d);                 // runtime pointers: garbage on disk
        I32(b, at + 0x0c, p.Cond);
        I32(b, at + 0x10, p.State);
        I32(b, at + 0x14, p.Type);              // a runtime leftover
        Str(b, at + 0x20, 30, p.Name);
        I32(b, at + 0x3e, p.Type);
        Str(b, at + 0x4e, 13, p.Class);
        Str(b, at + 0x5b, 13, p.Def);
        I32(b, at + 0x68, 2);
        I32(b, at + 0x6c, p.Full);
        BitConverter.TryWriteBytes(b.AsSpan(at + 0x70), p.Weight);
    }

    internal static readonly string[] PiranhaEquipped =
        ["327ci  V-8", "Stock", "4-Wheel Drum", "13in Stock", "13in Stock", "13in Stock", "13in Stock", "Empty", ".30cal MG", ".30cal MG", "Empty", "Empty", "", ""];

    internal static byte[] Cmp(Part[] inventory, Part[] queue, string[]? equipped = null)
    {
        var b = new byte[0x8c4 + 8 + 0x74 * (inventory.Length + queue.Length)];
        Str(b, 0x00, 20, "Picard Piranha");
        Str(b, 0x14, 20, "Stock (Orange)");
        Str(b, 0x28, 16, "doarmel");
        var reg = new (int, string, int, int)[] { (2, "ENGINE", 300, 150), (8, "PP1_GDB1", 10, 20), (7, "PP2_GDB1", 30, 40), (7, "PP3_GDB1", 50, 60), (7, "PP4_GDB1", 70, 80) };
        I32(b, 0x3c, reg.Length);
        for (int i = 0; i < reg.Length; i++)
        {
            int p = 0x40 + 32 * i;
            I32(b, p, reg[i].Item1); Str(b, p + 4, 16, reg[i].Item2); I32(b, p + 20, reg[i].Item3); I32(b, p + 24, reg[i].Item4);
        }
        var eq = equipped ?? PiranhaEquipped;
        for (int k = 0; k < 14; k++) Str(b, 0x400 + 30 * k, 30, eq[k]);
        int[] zones = [910, 570, 570, 700, 300, 250, 250, 280, 12];
        for (int z = 0; z < 9; z++) I32(b, 0x7fc + 4 * z, zones[z]);
        Str(b, 0x820, 13, "vppirnha.vdf"); Str(b, 0x82d, 13, "piranha2.vtf");
        Str(b, 0x83a, 13, "wauto_1a.wdf"); Str(b, 0x854, 13, "wauto_1a.wdf");
        I32(b, 0x864, 2);
        I32(b, 0x8c4, inventory.Length);
        for (int i = 0; i < inventory.Length; i++) WritePart(b, 0x8c8 + 0x74 * i, inventory[i]);
        int c = 0x8c8 + 0x74 * inventory.Length;
        I32(b, c, queue.Length);
        for (int j = 0; j < queue.Length; j++) WritePart(b, c + 4 + 0x74 * j, queue[j]);
        return b;
    }

    internal static readonly Part[] Inventory =
    [
        new(300, 1, "327ci  V-8", 2, "eng01", "eng01", 300, 400f),
        new(200, 1, "Stock", 3, "sus00", "sus00", 200, 50f),
        new(150, 1, "4-Wheel Drum", 4, "brk00", "brk00", 150, 40f),
        new(100, 1, "13in Stock", 5, "whe01", "wauto_1a.wdf", 100, 30f),
        new(100, 1, "13in Stock", 5, "whe01", "wauto_1a.wdf", 100, 30f),
        new(100, 1, "13in Stock", 5, "whe01", "wauto_1a.wdf", 100, 30f),
        new(100, 1, "13in Stock", 5, "whe01", "wauto_1a.wdf", 100, 30f),
        new(200, 1, ".30cal MG", 7, "mgn01", "gmmedium.gdf", 200, 35f),
        new(120, 1, ".30cal MG", 7, "mgn01", "gmmedium.gdf", 200, 35f),
        new(50, 3, ".30cal MG", 7, "mgn01", "gmmedium.gdf", 200, 35f),      // queued
        new(50, 3, ".30cal MG", 7, "mgn01", "gmmedium.gdf", 200, 35f),      // queued, the same condition
        new(90, 2, "Oil Slick", 8, "drp01", "doilslck.gdf", 200, 20f),
        new(0, 4, "NitrousOxide", 13, "", "spc02", 0, 0f),
    ];

    [Fact]
    public void A_bookmark_list_reads_every_field_and_stops_at_its_count()
    {
        var rows = OriginalSaveEntry.ReadDir(DirPadded(56, (1, "", "save000", 1), (7, "Easy Groove!", "save013", 8)));
        Assert.Equal(2, rows.Count);
        Assert.Equal(new OriginalSaveEntry(1, "", "save000", 1, 0), rows[0]);
        Assert.Equal("Easy Groove!", rows[1].Name);       // stale bytes after the NUL are not part of it
        Assert.Equal(2, rows[0].Plays);                   // saved after a win: the next scene
        Assert.Equal(7, rows[1].Plays);                   // saved in the garage: this one
        Assert.Throws<InvalidDataException>(() => OriginalSaveEntry.ReadDir(Dir((1, "a", "save000", 1))[..40]));
    }

    [Fact]
    public void A_bookmark_body_round_trips_the_garage_record_inventory_and_repair_queue()
    {
        var queue = new[] { Inventory[10], Inventory[9] };
        var s = OriginalSave.Read(Cmp(Inventory, queue));
        Assert.Equal("Picard Piranha", s.CarName);
        Assert.Equal("Stock (Orange)", s.Variant);
        Assert.Equal("doarmel", s.Ident);
        Assert.Equal(5, s.Registry.Count);
        Assert.Equal(new OriginalSlot(8, "PP1_GDB1", 10, 20), s.Registry[1]);
        Assert.Equal(PiranhaEquipped, s.Equipped);
        Assert.Equal([910, 570, 570, 700], s.Armour);
        Assert.Equal([300, 250, 250, 280], s.Chassis);
        Assert.Equal(12, s.Ninth);
        Assert.Equal(("vppirnha.vdf", "piranha2.vtf"), (s.Vdf, s.Vtf));
        Assert.Equal(["wauto_1a.wdf", "", "wauto_1a.wdf"], s.Wdf);
        Assert.Equal(2, s.Unk_0x864[0]);
        Assert.Equal(Inventory.Length, s.Inventory.Count);
        for (int i = 0; i < Inventory.Length; i++)
        {
            var (w, r) = (Inventory[i], s.Inventory[i]);
            Assert.Equal((w.Cond, w.State, w.Name, w.Type, w.Class, w.Def, w.Full, w.Weight), (r.Condition, r.State, r.Name, r.Type, r.Class, r.Def, r.Full, r.Weight));
            Assert.Equal(2, r.Unk_0x68);
        }
        Assert.Equal(2, s.RepairQueue.Count);
        Assert.Equal(OriginalSave.EquippedCount, s.Equipped.Length);
        Assert.True(OriginalSave.IsEmpty(s.Equipped[7]) && OriginalSave.IsEmpty(s.Equipped[12]) && !OriginalSave.IsEmpty(s.Equipped[8]));
    }

    [Fact]
    public void The_size_must_be_exact()
    {
        var b = Cmp(Inventory, []);
        Assert.Throws<InvalidDataException>(() => OriginalSave.Read(b[..^1]));
        Assert.Throws<InvalidDataException>(() => OriginalSave.Read([.. b, 0]));
        Assert.Throws<InvalidDataException>(() => OriginalSave.Read(new byte[0x8c4]));
    }

    [Fact]
    public void The_repair_queue_links_to_the_first_unlinked_part_in_repair_with_its_condition_and_file()
    {
        var stray = new Part(10, 3, "Landmines", 8, "drp02", "dmines.gdf", 200, 25f);    // no such part in repair: dropped
        var s = OriginalSave.Read(Cmp(Inventory, [Inventory[9], stray, Inventory[10], Inventory[7]]));
        // the two queued MGs share condition and file: the first takes #9, the second #10; the mounted MG (#7, state 1) is never linked
        Assert.Equal([9, 10], s.QueuedInventory());
    }

    [Fact]
    public void A_folder_lists_the_rows_whose_body_is_there()
    {
        File.WriteAllBytes(Path.Combine(_d, "SAVEGAME.DIR"), Dir((1, "", "save000", 1), (2, "gone", "save001", 1), (3, "bad", "save002", 8), (4, "upper", "save003", 8)));
        File.WriteAllBytes(Path.Combine(_d, "save000.cmp"), Cmp(Inventory, [Inventory[9]]));
        File.WriteAllBytes(Path.Combine(_d, "save002.cmp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_d, "SAVE003.CMP"), Cmp(Inventory, []));
        Assert.Equal([_d], OriginalSaveFolders.Find(_d, null), StringComparer.OrdinalIgnoreCase);
        var l = OriginalSaveFolders.Read(_d);
        Assert.Equal(["save000", "save002", "save003"], l.Select(x => x.Entry.File));
        Assert.NotNull(l[0].Save);
        Assert.Null(l[1].Save);
        Assert.NotNull(l[1].Error);
        Assert.Equal(Inventory.Length, l[2].Save!.Inventory.Count);
        Assert.Empty(OriginalSaveFolders.Find(Path.Combine(_d, "nothing"), null));
    }

    [Fact]
    public void An_install_under_program_files_is_also_looked_for_in_the_virtual_store()
    {
        Assert.Equal(Path.Combine("L", "VirtualStore", @"Program Files (x86)\GOG Galaxy\Games\Interstate 76"),
            OriginalSaveFolders.VirtualStore(@"C:\Program Files (x86)\GOG Galaxy\Games\Interstate 76\", "L"));
        Assert.Null(OriginalSaveFolders.VirtualStore("/opt/games/i76", "L"));
        if (!OperatingSystem.IsWindows()) return;
        var install = Path.Combine(_d, "install");
        var local = Path.Combine(_d, "local");
        Directory.CreateDirectory(install);
        var vs = OriginalSaveFolders.VirtualStore(install, local)!;
        Directory.CreateDirectory(vs);
        File.WriteAllBytes(Path.Combine(vs, "savegame.dir"), Dir());
        Assert.Equal([Path.GetFullPath(vs)], OriginalSaveFolders.Find(install, local));
    }

    /// <summary>The saves of an install (I76_INSTALL, or any folder of the original's saves in I76_ORIGINAL_SAVES): every one reads, and keeps the game's own invariants.</summary>
    [OriginalSavesFact]
    public void The_saves_of_an_install_read_and_keep_the_games_invariants()
    {
        int n = 0;
        foreach (var folder in OriginalSavesFactAttribute.Folders)
            foreach (var (e, s, err) in OriginalSaveFolders.Read(folder))
            {
                Assert.True(s is not null, $"{e.File}: {err}");
                Assert.InRange(e.Plays, 1, 17);
                Assert.All(s!.Inventory, p => Assert.InRange(p.State, 1, 4));
                // the queue holds copies of the parts in repair, each linked back
                Assert.Equal(s.RepairQueue.Count, s.QueuedInventory().Count);
                Assert.All(s.QueuedInventory(), i => Assert.Equal((int)OriginalPartState.Repair, s.Inventory[i].State));
                Assert.All(s.Inventory, p => Assert.True(p.Type is 2 or 3 or 4 or 5 or 7 or 8 or 13, $"{e.File}: {p.Name} type {p.Type}"));
                Assert.EndsWith(".vdf", s.Vdf, StringComparison.OrdinalIgnoreCase);
                n++;
            }
        Assert.True(n > 0);
    }
}

/// <summary>A fact that needs the original's saves: a savegame.dir with a body in I76_INSTALL (or its VirtualStore copy), or in I76_ORIGINAL_SAVES.</summary>
public sealed class OriginalSavesFactAttribute : FactAttribute
{
    public static List<string> Folders
    {
        get
        {
            var l = new List<string>();
            if (Environment.GetEnvironmentVariable("I76_ORIGINAL_SAVES") is { Length: > 0 } o && Directory.Exists(o)) l.AddRange(OriginalSaveFolders.Find(o, null));
            if (TestInstall.Dir is { } d) l.AddRange(OriginalSaveFolders.Find(d, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            return l.Where(f => { try { return OriginalSaveFolders.Read(f).Count > 0; } catch (InvalidDataException) { return true; } }).ToList();
        }
    }

    public OriginalSavesFactAttribute()
    {
        if (Folders.Count == 0) Skip = "set I76_INSTALL to an install with saved bookmarks (savegame.dir and saveNNN.cmp), or I76_ORIGINAL_SAVES to a folder of them, to run";
    }
}
