using I76.Formats.Mission;

namespace I76.Formats.Fsm;

public readonly record struct FsmInstruction(int Index, int Offset, uint Op, string Mnemonic, string Operand);

/// <summary>
/// The FSM mission-script bytecode disassembler, a port of the reference reader
/// (docs/formats/fsm-scripting.md). Every instruction
/// is a u32 opcode and a u32 operand.
/// </summary>
public static class FsmDisassembler
{
    public const int Instr = 8;

    public static readonly IReadOnlyDictionary<uint, (string Mnemonic, string Doc)> Ops = new Dictionary<uint, (string, string)>
    {
        [1] = ("pushImm", "push the operand"),
        [2] = ("pushLocalAddr", "push &local[n]"),
        [3] = ("pushLocal", "push local[n]"),
        [4] = ("argLocalAddr", "append &local[n] to the argument list"),
        [5] = ("argLocal", "append local[n] to the argument list"),
        [6] = ("allocLocals", "stack += n slots"),
        [7] = ("freeLocals", "stack -= n slots"),
        [8] = ("jump", "pc = code + n"),
        [9] = ("branchIfFalse", "if !cond: pc = code + n"),
        [10] = ("yieldJump", "pc = code + n, then return to the scheduler"),
        [11] = ("call", "push frame, pc = code + n"),
        [12] = ("return", "pop frame and n slots"),
        [13] = ("exec", "run prototype n from the mission's table"),
        [14] = ("not", "cond = !cond"),
    };

    /// <summary>Operands that are jump targets in instructions, not bytes.</summary>
    public static readonly uint[] Branchers = [8, 9, 10, 11];

    /// <summary>The bytecode, located as the longest run of valid instructions.</summary>
    public static (int Base, int Count) FindProgram(byte[] b, int start)
    {
        int Run(int off)
        {
            int n = 0;
            while (off + Instr <= b.Length)
            {
                uint op = Bin.U32(b, off);
                if (op < 1 || op > 14) break;
                n++;
                off += Instr;
            }
            return n;
        }

        (int Count, int Off) best = (0, start);
        int o = start;
        while (o < b.Length - Instr)
        {
            int r = Run(o);
            if (r > best.Count) best = (r, o);
            o += r == 0 ? 4 : r * Instr;
        }
        return (best.Off, best.Count);
    }

    public static (int Base, List<FsmInstruction> Code)? Disassemble(MissionFile msn)
    {
        var b = msn.FsmPayload();
        if (b.Length < 4) return null;
        var t = msn.GetFsmTables()!;
        var names = msn.FsmOpcodes();
        var (bse, count) = FindProgram(b, t.ProgramOffset);
        var o = new List<FsmInstruction>(count);
        for (int i = 0; i < count; i++)
        {
            int at = bse + i * Instr;
            uint op = Bin.U32(b, at), arg = Bin.U32(b, at + 4);
            string mnem = Ops.TryGetValue(op, out var m) ? m.Mnemonic : "?";
            string operand = op == 13 ? (arg < names.Count ? names[(int)arg] : $"proto#{arg}")
                : Branchers.Contains(op) ? $"@{arg}"
                : ((int)arg).ToString(System.Globalization.CultureInfo.InvariantCulture);
            o.Add(new FsmInstruction(i, at, op, mnem, operand));
        }
        return (bse, o);
    }
}
