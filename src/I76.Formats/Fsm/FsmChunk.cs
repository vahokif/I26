using System.Buffers.Binary;
using System.Text;
using I76.Formats.Mission.Edit;

namespace I76.Formats.Fsm;

/// <summary>The bytecode's instructions: a u32 opcode and a u32 operand each (the operand read as signed).</summary>
public static class FsmOp
{
    public const int PushImm = 1, PushLocalAddr = 2, PushLocal = 3, ArgLocalAddr = 4, ArgLocal = 5, Alloc = 6, Free = 7;
    public const int Jump = 8, BranchIfFalse = 9, Yield = 10, Call = 11, Return = 12, Exec = 13, Not = 14;

    /// <summary>Whether the operand is an instruction index.</summary>
    public static bool Branches(int op) => op is Jump or BranchIfFalse or Yield or Call;

    public static readonly string[] Mnemonics = ["", "pushImm", "pushLocalAddr", "pushLocal", "argLocalAddr", "argLocal", "allocLocals", "freeLocals",
        "jump", "branchIfFalse", "yieldJump", "call", "return", "exec", "not"];
}

/// <summary>One instruction.</summary>
public readonly record struct FsmInstr(int Op, int Arg)
{
    public override string ToString() => $"{(Op is > 0 and < 15 ? FsmOp.Mnemonics[Op] : "op" + Op)} {Arg}";
}

/// <summary>An actor: the script's label for a placed object and the placement's raw 8-byte name (high bits included).</summary>
public sealed record FsmActorEntry(string Label, byte[] Id);

/// <summary>A thread: the instruction it starts at and the data cells its parameters point to (in stack order).</summary>
public sealed record FsmThreadEntry(int StartPc, int[] Cells)
{
    /// <summary>The table's slots past the parameters: zero in every stock file, kept as read.</summary>
    public int[] Spare { get; init; } = [];
}

/// <summary>
/// The whole ADEF/FSM payload, read and written byte for byte: u32 count and 40-byte names of the prototypes (the call
/// vocabulary, by which <c>exec n</c> names its operation), u32 count and 48-byte actors (label[40], placement name[8]), u32 count
/// and 40-byte sound (and movie) file names, the nav-path table (kept as its bytes: the path tool edits it), then u32 count and
/// 168-byte threads (u32 start, u32 depth, 40 × i32 data indices), u32 count and i32 data cells, u32 count and 8-byte
/// instructions. A payload of no bytes is a map without a script.
/// </summary>
public sealed class FsmChunk
{
    public const int NameSlot = 40, ActorSize = 48, ThreadSlots = 40, ThreadSize = 8 + 4 * ThreadSlots;

    public List<string> Prototypes { get; set; } = [];
    public List<FsmActorEntry> Actors { get; set; } = [];
    public List<string> Sounds { get; set; } = [];
    /// <summary>The nav-path table's bytes, from its count to its last point (see <see cref="FsmPaths"/>).</summary>
    public byte[] PathTable { get; set; } = new byte[4];
    public List<FsmThreadEntry> Threads { get; set; } = [];
    public List<int> Data { get; set; } = [];
    public List<FsmInstr> Code { get; set; } = [];
    /// <summary>Read from a payload of no bytes (written back as none while nothing was added).</summary>
    public bool WasEmpty { get; init; }

    public List<PathEntry> Paths => FsmPaths.Read(Payload(withCode: false));

    public static FsmChunk Read(byte[] b)
    {
        if (b.Length == 0) return new FsmChunk { WasEmpty = true };
        int off = 0;
        int U()
        {
            if (off + 4 > b.Length) throw new InvalidDataException($"the script ends at {b.Length} inside a table (offset {off})");
            int v = (int)Bin.U32(b, off); off += 4; return v;
        }
        void Need(long n) { if (n < 0 || off + n > b.Length) throw new InvalidDataException($"the script ends at {b.Length} inside a table (offset {off})"); }
        var c = new FsmChunk();
        for (int n = U(), i = 0; i < n; i++) { Need(NameSlot); c.Prototypes.Add(Name(b, off)); off += NameSlot; }
        for (int n = U(), i = 0; i < n; i++) { Need(ActorSize); c.Actors.Add(new FsmActorEntry(Name(b, off), b.AsSpan(off + NameSlot, 8).ToArray())); off += ActorSize; }
        for (int n = U(), i = 0; i < n; i++) { Need(NameSlot); c.Sounds.Add(Name(b, off)); off += NameSlot; }
        var at = FsmPaths.Locate(b) ?? throw new InvalidDataException("the script's path table does not parse");
        c.PathTable = b.AsSpan(at.Start, at.End - at.Start).ToArray();
        off = at.End;
        for (int n = U(), i = 0; i < n; i++)
        {
            Need(ThreadSize);
            int pc = U(), depth = U();
            if (depth < 0 || depth > ThreadSlots) throw new InvalidDataException($"thread {i}: {depth} parameters (at most {ThreadSlots})");
            var cells = new int[depth];
            for (int k = 0; k < depth; k++) cells[k] = U();
            var spare = new int[ThreadSlots - depth];
            for (int k = 0; k < spare.Length; k++) spare[k] = U();
            c.Threads.Add(new FsmThreadEntry(pc, cells) { Spare = spare.Any(v => v != 0) ? spare : [] });
        }
        int dn = U(); Need(4L * dn);
        for (int i = 0; i < dn; i++) c.Data.Add(U());
        int cn = U(); Need(8L * cn);
        for (int i = 0; i < cn; i++) { int op = U(), a = U(); c.Code.Add(new FsmInstr(op, a)); }
        if (off != b.Length) throw new InvalidDataException($"{b.Length - off} bytes follow the code");
        return c;
    }

    /// <summary>The payload: what <see cref="Read"/> read, byte for byte, when nothing was changed.</summary>
    public byte[] ToBytes()
    {
        if (WasEmpty && Prototypes.Count == 0 && Actors.Count == 0 && Sounds.Count == 0 && Threads.Count == 0 && Data.Count == 0 && Code.Count == 0
            && PathTable.Length == 4 && Bin.U32(PathTable, 0) == 0) return [];
        return Payload(withCode: true);
    }

    private byte[] Payload(bool withCode)
    {
        using var ms = new MemoryStream();
        var u = new byte[4];
        void W(int v) { BinaryPrimitives.WriteInt32LittleEndian(u, v); ms.Write(u); }
        W(Prototypes.Count);
        foreach (var p in Prototypes) ms.Write(Slot(p, "prototype"));
        W(Actors.Count);
        foreach (var a in Actors)
        {
            ms.Write(Slot(a.Label, "actor label"));
            if (a.Id.Length != 8) throw new ArgumentException($"actor {a.Label}: the placement name is 8 bytes");
            ms.Write(a.Id);
        }
        W(Sounds.Count);
        foreach (var s in Sounds) ms.Write(Slot(s, "sound"));
        ms.Write(PathTable);
        if (!withCode) { W(0); W(0); W(0); return ms.ToArray(); }
        W(Threads.Count);
        foreach (var t in Threads)
        {
            if (t.Cells.Length > ThreadSlots) throw new ArgumentException($"a thread has {t.Cells.Length} parameters (at most {ThreadSlots})");
            W(t.StartPc); W(t.Cells.Length);
            foreach (var v in t.Cells) W(v);
            for (int k = t.Cells.Length; k < ThreadSlots; k++) W(k - t.Cells.Length < t.Spare.Length ? t.Spare[k - t.Cells.Length] : 0);
        }
        W(Data.Count);
        foreach (var v in Data) W(v);
        W(Code.Count);
        foreach (var i in Code) { W(i.Op); W(i.Arg); }
        return ms.ToArray();
    }

    /// <summary>A 40-byte NUL-padded name slot (39 characters at most).</summary>
    public static byte[] Slot(string s, string what)
    {
        if (s.Length > NameSlot - 1 || s.Any(ch => ch is < ' ' or > (char)255)) throw new ArgumentException($"{what} '{s}': at most {NameSlot - 1} characters, printable");
        var b = new byte[NameSlot];
        Encoding.Latin1.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    private static string Name(byte[] b, int at) => Bin.CStr(b, at, NameSlot);
}
