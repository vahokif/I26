using I76.Formats.Mission;
using I76.Sim.Fsm;
using I76.Sim.Rng;

namespace I76.Sim.Missions;

/// <summary>
/// A running mission script: the VM over a ScriptHost, stepped once per sim
/// frame. Before the vehicles move, standing orders become AI targets; after,
/// the frame's car contacts feed the damage-event recorder, the mission clock
/// advances (not in the first 3 frames, as the original's frame clock), the FSM ticks
/// (the original runs after the per-object passes), then the camera and the
/// CB radio, and the mission's end is checked.
/// </summary>
public sealed partial class MissionScript
{
    public ScriptHost Host { get; }
    /// <summary>Weapons and rounds, stepped after the script.</summary>
    public Weapons.Combat? Combat { get; set; }
    /// <summary>The script's VM; null in a melee, which runs no script.</summary>
    public FsmVm? Vm { get; }
    /// <summary>A melee's engine-side game, run where the FSM would tick.</summary>
    public MeleeGame? Melee { get; init; }
    private int _frames, _ticks;

    /// <param name="poetrySeed">The poetry decks' shuffle: the session's, so that nothing on the machine (a clock) decides it.</param>
    /// <param name="nitroDriver">A Nitro mission's driver variant (<see cref="I76.Formats.Vfs.GameInstall.DriverVariant"/>): the poetry key says that driver's lines; 0 in I76.</param>
    /// <param name="i76Sounds">Under Nitro, Interstate '76 is on the machine: Groove (variant 1) says his I76 kill lines.</param>
    public MissionScript(FsmProgram? program, ScriptHost host, CrtRand rand, int poetrySeed = 1, int nitroDriver = 0, bool i76Sounds = false)
    {
        Host = host;
        if (program is not null) Vm = new FsmVm(program, host, rand);
        var taurus = Vm?.Actors.FirstOrDefault(a => a.Label.Equals("taurus", StringComparison.OrdinalIgnoreCase)).Handle;
        Poetry = new Poetry(taurus, poetrySeed, nitroDriver, i76Sounds);
    }

    /// <summary>The poetry key's decks and poet.</summary>
    public Poetry Poetry { get; }

    /// <summary>The player pressed POETRY.</summary>
    public void RequestPoem() => Poetry.Request(Host.Cb, Host.Player);

    /// <summary>A human pressed POETRY in Nitro: their driver speaks (in co-op, whichever player pressed).</summary>
    public void RequestPoem(Entity? speaker) => Poetry.Request(Host.Cb, speaker);

    /// <summary>The mission's result once its end time has come: true success, false failure; a melee is over (state 1) five seconds after the player is destroyed.</summary>
    public bool? Result => Melee is { } m ? (m.Over ? true : null) : Host.End is { } e && Host.Now >= e.At ? e.Success : null;

    /// <summary>The getdown cheat has been used this mission: the script no longer runs.</summary>
    public bool GotDown { get; private set; }
    /// <summary>End: when the player was destroyed + 5 s (FLT_MAX until then).</summary>
    public float? GetDownEndAt { get; private set; }

    /// <summary>
    /// The typed cheat getdown: refused in a network game (and co-op) and once used (until the next mission). Every
    /// helicopter but the player is sent to a height, 25, 35, 45 ... in the AI lists' order (by team, then as
    /// registered); the CB plays gdsgc69.wav; the player's attacker cap becomes 1000; and from the next frame on the
    /// script stops for good and the original runs in its place (<see cref="GetDownFrame"/>). Returns whether it took.
    /// </summary>
    public bool GetDown()
    {
        // a co-op game's cheats are nobody's (the typed cheats are the single player's own)
        if (Melee?.Rules.Network == true || Host.Coop is not null || GotDown || Host.Player is not { } player) return false;
        int h = 25;
        foreach (var e in AiVehicles().Where(e => e != player && e.IsHeli))
        {
            Host.SetHeliHeight(e, h);
            h += 10;
        }
        Host.CbSay("gdsgc69.wav", null, 0);
        GotDown = true;
        _ticks = (Vm?.TickCount ?? _ticks + 1) - 1;   // the FSM's tick counter keeps counting without it
        player.MaxAttackers = 1000;
        return true;
    }

    /// <summary>The AI's vehicle lists: every vehicle but the gun emplacements, by team bucket, then in order.</summary>
    private IEnumerable<Entity> AiVehicles() =>
        Host.Entities.Where(e => e.Vehicle is { IsEmplacement: false } && !e.Removed).OrderBy(e => e.Team);

    /// <summary>
    /// The frame of a mission after getdown (a melee's own frame is the same function): once the end time
    /// has passed, the game state becomes 1, a won mission; 5 s after the player is destroyed is that end time; every
    /// live AI vehicle is ordered to attack the player.
    /// </summary>
    private void GetDownFrame()
    {
        if (GetDownEndAt is { } at && Host.Now > at && Host.End is null) Host.EndMission(true, 0, -2);
        if (Host.Player is not { } player) return;   // the original idles every AI car; a mission always has a player
        if (GetDownEndAt is null && player.Vehicle is { } pv && Damage.DamageModel.IsDead(pv)) GetDownEndAt = Host.Now + 5f;
        foreach (var e in AiVehicles().ToList())
            if (e != player && !e.Dead) Host.Order(e, player, AiOrder.Attack, null, 0);
    }

    public void AfterFrame(float frameDelta)
    {
        if (++_frames > 3) Host.Now += frameDelta;
        Host.FsmTick = Vm is not null && !GotDown ? Vm.TickCount : ++_ticks;
        Host.FrameDelta = frameDelta;
        if (Melee is not null) Melee.Frame(frameDelta);
        else if (GotDown) GetDownFrame();
        else
        {
            Host.CoopFrame();
            Vm?.Tick();
            // the original advances its counter right after the machines: what happens from here to the next machines
            // (this frame's rounds, the next frame's collisions) is stamped with the counter those machines will see
            if (Vm is not null) Host.FsmTick = Vm.TickCount;
        }
        // The emplacements' fire pass, right after the FSM and before the weapons
        if (Combat is { } c) Vehicles.Emplacements.Frame(c.World, c, Melee?.Rules.Network == true);
        Host.Ptable.EndFrame();
        Host.View.Update(frameDelta);
        Host.UpdatePersonalViews(frameDelta);
        Host.Cb.Update(Host.Now * 1000.0);
        Combat?.Frame(frameDelta);
    }
}
