using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;

namespace I76.Sim.Missions;

public sealed partial class MissionSetup
{
    /// <summary>
    /// The drive-on collider of one structure placement (ODEF type 0xb ramp, 0xc bridge, 0xd), built the way a mission builds it but
    /// without a mission: the editor's road tool reads its surface's ends (<c>StructureAttach.Ends</c> over
    /// <c>Surfaces.Select(p =&gt; p.V)</c>) to snap a road to a bridge's deck or a jump's foot. Null for a definition with no model or
    /// no collider. <paramref name="position"/> is the placement's position with its y already on the ground (<c>MissionWorld.GroundedPosition</c>).
    /// </summary>
    public static StaticObject? StructureCollider(AssetVfs vfs, ModelAssembler asm, string name, string definition, uint type, float[] rotation, Vector3 position)
    {
        if (type is not (0xb or 0xc or 0xd) || asm.Assemble(definition) is not { } model) return null;
        var (_, nodeIndex) = StructureInfo(vfs, definition).Instance();
        return StaticWorld.Build(name, type, rotation, position, model, NodeClasses(vfs, definition), NodeTypes(vfs, definition), nodeIndex);
    }
}
