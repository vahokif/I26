using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Sim.Save;

/// <summary>
/// When the port's Checkpoints option keeps a snapshot (docs/savestate.md, docs/deviations.md): in a scripted
/// single-player mission, once when the intro is over (the player has the car, no checkpoint yet), and each time
/// one of the mission's objectives is completed. Never while the player's car is dead or once the mission's end
/// has been decided. Asked once a frame, between ticks.
/// </summary>
public sealed class CheckpointPolicy
{
    private readonly int[] _done;
    private bool _started;

    private CheckpointPolicy(SimSession s, bool resumed)
    {
        _done = [.. s.Script!.Host.Objectives.Flags.Select(f => f & Objectives.Succeeded)];
        // a session restored from a checkpoint has had its start; the objectives it had done are done
        _started = resumed;
    }

    /// <summary>Whether a session can have checkpoints at all: a mission script running, no melee, no other human.</summary>
    public static bool Allowed(SimSession s) => s.Script is { Vm: not null, Melee: null } && s.World.Humans.Count == 0 && s.World.Player is not null;

    /// <summary>The policy for a session, or null when the option is off or the session cannot have checkpoints.</summary>
    public static CheckpointPolicy? For(SimSession s, bool enabled, bool resumed = false) => enabled && Allowed(s) ? new CheckpointPolicy(s, resumed) : null;

    /// <summary>Why a checkpoint is due now ("start", "objective N"), or null.</summary>
    public string? Due(SimSession s)
    {
        var h = s.Script!.Host;
        var flags = h.Objectives.Flags;
        string? why = null;
        for (int i = 0; i < flags.Length && i < _done.Length; i++)
        {
            if ((flags[i] & Objectives.Succeeded) == 0 || _done[i] != 0) continue;
            _done[i] = Objectives.Succeeded;
            why ??= $"objective {i + 1}";
        }
        if (why is not null) _started = true;
        // the start: the first frame the player drives (the intro's cut-scene over), if nothing was kept before
        else if (!_started && s.Ticks > 0 && !s.Cutscene) { _started = true; why = "start"; }
        if (why is null || h.End is not null || s.World.Player is not { } p || (p.Flags & VehicleFlags.Dead) != 0) return null;
        return why;
    }
}
