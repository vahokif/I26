using I76.Formats;

namespace I76.Formats.Shell;

/// <summary>
/// nituser.mel (0x8c bytes): the shell's remembered setups. +0x00 scenario {model, variant};
/// +0x08 instant melee {arena, model, variant, drivers, driver[32]}; +0x38 multi {kind, arena, model, variant, u32, game[32], driver[32]}.
/// </summary>
public sealed class NitUserMel
{
    public const int Size = 0x8c;
    public int ScenarioModel { get; set; } = 23;
    public int ScenarioVariant { get; set; } = 1;
    public int MeleeArena { get; set; }
    public int MeleeModel { get; set; }
    public int MeleeVariant { get; set; } = 1;
    public int MeleeDrivers { get; set; } = 2;
    public string MeleeDriver { get; set; } = "Unknown";
    public int MultiKind { get; set; } = 1;
    public int MultiArena { get; set; }
    public int MultiModel { get; set; }
    public int MultiVariant { get; set; } = 1;
    public int MultiExtra { get; set; }
    public string MultiGame { get; set; } = "";
    public string MultiDriver { get; set; } = "Unknown";

    public static NitUserMel Parse(byte[] d)
    {
        var m = new NitUserMel();
        if (d.Length < Size) return m;
        m.ScenarioModel = (int)Bin.U32(d, 0); m.ScenarioVariant = (int)Bin.U32(d, 4);
        m.MeleeArena = (int)Bin.U32(d, 8); m.MeleeModel = (int)Bin.U32(d, 12); m.MeleeVariant = (int)Bin.U32(d, 16); m.MeleeDrivers = (int)Bin.U32(d, 20);
        m.MeleeDriver = Bin.CStr(d, 0x18, 32);
        m.MultiKind = (int)Bin.U32(d, 0x38); m.MultiArena = (int)Bin.U32(d, 0x3c); m.MultiModel = (int)Bin.U32(d, 0x40);
        m.MultiVariant = (int)Bin.U32(d, 0x44); m.MultiExtra = (int)Bin.U32(d, 0x48);
        m.MultiGame = Bin.CStr(d, 0x4c, 32); m.MultiDriver = Bin.CStr(d, 0x6c, 32);
        return m;
    }

    public byte[] ToBytes()
    {
        var d = new byte[Size];
        void U(int o, int v) => BitConverter.TryWriteBytes(d.AsSpan(o), (uint)v);
        void S(int o, string s) { var b = System.Text.Encoding.Latin1.GetBytes(s.Length > 31 ? s[..31] : s); b.CopyTo(d, o); }
        U(0, ScenarioModel); U(4, ScenarioVariant);
        U(8, MeleeArena); U(12, MeleeModel); U(16, MeleeVariant); U(20, MeleeDrivers); S(0x18, MeleeDriver);
        U(0x38, MultiKind); U(0x3c, MultiArena); U(0x40, MultiModel); U(0x44, MultiVariant); U(0x48, MultiExtra); S(0x4c, MultiGame); S(0x6c, MultiDriver);
        return d;
    }
}
