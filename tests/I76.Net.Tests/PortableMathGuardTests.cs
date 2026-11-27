using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using I76.Formats;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The simulation is the same game on every machine (docs/architecture.md, "The same game on every machine"): the code that feeds
/// its state calls no operation whose bits are the platform's. Read from the compiled IL of the sim, the server and the network
/// code: a call to one of <see cref="Banned"/> outside <see cref="Allowed"/> fails, with the method that makes it. Use
/// <see cref="DetMath"/> instead (its Sin, Cos, ..., Neg, Min, Max, Clamp, Dot, FromAxisAngle).
/// </summary>
public class PortableMathGuardTests
{
    /// <summary>
    /// What gives other bits on another platform (measured, Windows x64 against macOS Arm64, .NET 8): the C library's
    /// transcendentals and what System.Numerics builds on them, and the System.Numerics operations whose signs of zero are the
    /// processor's (vector negation, Min/Max/Clamp, Vector2.Dot).
    /// </summary>
    private static readonly HashSet<string> Banned =
    [
        .. new[] { "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Exp", "Log", "Log10", "Log2", "Pow", "Sinh", "Cosh", "Tanh",
            "Asinh", "Acosh", "Atanh", "Cbrt", "SinCos" }.SelectMany(f => new[] { "System.Math." + f, "System.MathF." + f }),
        "System.Numerics.Quaternion.CreateFromAxisAngle", "System.Numerics.Quaternion.CreateFromYawPitchRoll", "System.Numerics.Quaternion.Slerp",
        "System.Numerics.Quaternion.op_UnaryNegation", "System.Numerics.Quaternion.Negate",
        "System.Numerics.Matrix4x4.CreateRotationX", "System.Numerics.Matrix4x4.CreateRotationY", "System.Numerics.Matrix4x4.CreateRotationZ",
        "System.Numerics.Matrix4x4.CreateFromYawPitchRoll", "System.Numerics.Matrix4x4.CreateFromAxisAngle", "System.Numerics.Matrix3x2.CreateRotation",
        .. new[] { "Vector2", "Vector3", "Vector4" }.SelectMany(v => new[] { "op_UnaryNegation", "Negate", "Min", "Max", "Clamp" }.Select(m => $"System.Numerics.{v}.{m}")),
        "System.Numerics.Vector2.Dot",
    ];

    /// <summary>Methods (declaring type, then method; lambdas count as their declaring type's) that may: none feeds the sim's state.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["I76.Net.CarTrack"] = "the client's drawing of other cars between snapshots (smoothing offsets, slerp); never simulated",
        ["I76.Net.OwnCarPredictor.Smooth"] = "the client's drawn correction of its own car after a reconcile; the predicted state itself is the sim's",
        ["I76.Net.Biquad"] = "the CB voice's radio filter coefficients (audio)",
        ["I76.Net.RadioVoice"] = "the CB voice's radio colouring and squelch (audio)",
    };

    private static string Declaring(MetadataReader md, TypeDefinition t)
    {
        // a lambda's or local function's compiler type (<>c, <>c__DisplayClass..) counts as the type it is nested in
        while (t.GetDeclaringType() is { IsNil: false } outer && md.GetString(t.Name).StartsWith('<')) t = md.GetTypeDefinition(outer);
        string ns = md.GetString(t.Namespace);
        string name = md.GetString(t.Name);
        var d = t.GetDeclaringType();
        while (!d.IsNil) { var dt = md.GetTypeDefinition(d); name = md.GetString(dt.Name) + "+" + name; ns = md.GetString(dt.Namespace); d = dt.GetDeclaringType(); }
        return ns.Length > 0 ? ns + "." + name : name;
    }

    private static string MethodName(MetadataReader md, string name)
    {
        // a local function or lambda body: <Outer>g__Local|.. or <Outer>b__..: the outer method
        if (name.StartsWith('<') && name.IndexOf('>') is > 1 and var e) return name[1..e];
        return name;
    }

    private static string? Callee(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                if (mr.Parent.Kind != HandleKind.TypeReference) return null;
                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                return $"{md.GetString(tr.Namespace)}.{md.GetString(tr.Name)}.{md.GetString(mr.Name)}";
            case HandleKind.MethodSpecification:
                return Callee(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            default:
                return null;
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    private static int OperandSize(OpCode o, ref BlobReader il) => o.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 * il.ReadInt32(),
        _ => 4,
    };

    /// <summary>Every banned call in the assembly: "Caller.Method -> Callee".</summary>
    internal static List<(string Type, string Method, string Callee)> Calls(Assembly asm)
    {
        var found = new List<(string, string, string)>();
        using var pe = new PEReader(File.OpenRead(asm.Location));
        var md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            string type = Declaring(md, t);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
                while (il.RemainingBytes > 0)
                {
                    short v = il.ReadByte();
                    if (v == 0xFE) v = (short)(0xFE00 | il.ReadByte());
                    var op = OpCodesByValue[v];
                    if (op.OperandType == OperandType.InlineMethod)
                    {
                        var callee = Callee(md, MetadataTokens.EntityHandle(il.ReadInt32()));
                        if (callee is not null && Banned.Contains(callee)) found.Add((type, MethodName(md, md.GetString(m.Name)), callee));
                        continue;
                    }
                    int n = OperandSize(op, ref il);
                    il.Offset += n;
                }
            }
        }
        return found;
    }

    [Theory]
    [InlineData("I76.Sim")]
    [InlineData("I76.Server")]
    [InlineData("I76.Net")]
    public void TheSimsStateIsBuiltWithPortableMathsOnly(string assembly)
    {
        var asm = assembly switch
        {
            "I76.Sim" => typeof(I76.Sim.Session.SimSession).Assembly,
            "I76.Server" => typeof(I76.Server.MeleeHost).Assembly,
            _ => typeof(I76.Net.Protocol).Assembly,
        };
        var bad = Calls(asm).Where(c => !Allowed.ContainsKey(c.Type) && !Allowed.ContainsKey(c.Type + "." + c.Method))
            .Select(c => $"{c.Type}.{c.Method} calls {c.Callee}").Distinct().Order().ToList();
        Assert.True(bad.Count == 0, "platform-dependent maths in the sim's code (use DetMath):\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// The readers' code that shapes the sim's world is held to the same rule: a structure's road ends at load, the ground past the
    /// map's edge.
    /// </summary>
    [Fact]
    public void TheReadersStateFeedingCodeIsPortableToo()
    {
        string[] feeding = ["I76.Formats.Assembly.StructureAttach", "I76.Formats.Assembly.OutsideGround", "I76.Formats.DetMath"];
        var bad = Calls(typeof(DetMath).Assembly).Where(c => feeding.Any(f => c.Type == f || c.Type.StartsWith(f + "+") || c.Type.StartsWith(f + ".")))
            .Select(c => $"{c.Type}.{c.Method} calls {c.Callee}").Distinct().ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>The guard sees what it is meant to: the readers' drawing code calls MathF.Sin and negates vectors.</summary>
    [Fact]
    public void TheScanFindsTheCallsItLooksFor()
    {
        var calls = Calls(typeof(DetMath).Assembly);
        Assert.Contains(calls, c => c.Callee == "System.MathF.Sin");
        Assert.Contains(calls, c => c.Callee == "System.Numerics.Vector3.op_UnaryNegation");
    }
}
