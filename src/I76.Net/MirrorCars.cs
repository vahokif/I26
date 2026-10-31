using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Net;

/// <summary>
/// How a <see cref="ClientWorld"/> makes the cars the server names: the one thing in the mirror that depends on the kind of
/// game it mirrors. The network melee's is <see cref="MeleeMirrorCars"/>; a co-op mission's is <see cref="CoopMirrorCars"/>.
/// </summary>
public interface IMirrorCars
{
    /// <summary>The mirror's session exists (its cars and structures have their serials), before any car of the server's is taken.</summary>
    void Prepare(SimSession session) { }
    /// <summary>The car <paramref name="serial"/> from its file; <paramref name="human"/> is a player's (not the AI fill's).</summary>
    Vehicle Build(int serial, string def, bool human);
    /// <summary>The car <paramref name="v"/> becomes <paramref name="fresh"/> in place (a reconfigured respawn), the object staying.</summary>
    void Rebuild(Vehicle v, Vehicle fresh, bool human);
}

/// <summary>The network melee's cars: <see cref="MeleeGame.Build"/> and <see cref="MeleeGame.Rebuild"/>.</summary>
public sealed class MeleeMirrorCars(MeleeGame game) : IMirrorCars
{
    public Vehicle Build(int serial, string def, bool human) => game.Build(def, human);
    public void Rebuild(Vehicle v, Vehicle fresh, bool human) => MeleeGame.Rebuild(v, fresh, game.Rand, human, game.Rules.Network);
}
