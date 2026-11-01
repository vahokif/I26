using Godot;
using I76.Sim.Missions;
using I76.Sim.Vehicles;

namespace I76.Game.Sim;

/// <summary>
/// The car-borne sounds the original's per-frame code asks for, beside the tyre sounds: the player's horn (from
/// the key loop), the other cars' horns (the network bit 0x200000 and the AI's honk), the sirens of
/// setSiren and of a police car's honk, the wind and the ramp thump. All are engsnd.dat's horn file or a fixed
/// name, started through the voice rules of <see cref="Voice3D"/>. Called once a tick (and a frame, for a client).
/// </summary>
public sealed class CarVoices(SimDriver d)
{
    private Voice3D? _horn, _wind;
    private readonly Dictionary<Vehicle, Voice3D> _others = [], _sirens = [], _bumps = [];
    private readonly Dictionary<Entity, bool> _sirenWas = [];
    private readonly Dictionary<int, string?> _hornNames = [];

    /// <summary>engsnd.dat's HORN SND FILE for the car's engine, looked up once.</summary>
    public string? HornName(Vehicle v)
    {
        if (d.Audio is null) return null;
        if (!_hornNames.TryGetValue(v.EngineNumber, out var n)) _hornNames[v.EngineNumber] = n = VehicleFactory.HornSound(d.Audio.Vfs, v.EngineNumber);
        return n;
    }

    private static bool Live(Voice3D? v) => v is not null && GodotObject.IsInstanceValid(v);

    /// <summary>
    /// A voice of the player's car that exists while it is asked for, as mode-2 voices do: asked every frame it
    /// keeps going (its file loops: DirectSound's looping flag follows the mode), and the first frame it is not asked for the pump stops it.
    /// </summary>
    private void Held(ref Voice3D? slot, bool want, CarNode? car, string name, string? file)
    {
        if (Live(slot) && (!want || slot!.GetParent() != car))
        {
            slot!.Stop();
            slot.QueueFree();
            Voice3D.Trace($"stop {name}");
            slot = null;
        }
        if (!want || car is null || Live(slot) || file is null || d.Audio?.Get(file, loop: true) is not { } s) return;
        slot = new Voice3D { Name = name, Stream = s, Autoplay = true };
        car.AddChild(slot);
        Voice3D.Trace($"start {name} ({file}, loop) on the player");
    }

    /// <summary>A one-shot of a car that plays only if that car's last one has ended (the GAS0 header's one voice per file).</summary>
    private void Once(Dictionary<Vehicle, Voice3D> voices, Vehicle v, string? file, string name)
    {
        if (file is null || d.Audio?.Get(file) is not { } s) return;
        if (voices.TryGetValue(v, out var old) && Live(old) && old.Playing) return;
        var car = d.Cars.FirstOrDefault(c => c.Sim == v);
        if (car is null) return;
        var p = Voice3D.OneShot(s);
        p.Name = name;
        car.AddChild(p);
        p.Start();
        voices[v] = p;
        Voice3D.Trace($"start {name} ({file}) on {(car == d.Player ? "the player" : "car " + v.NetId)}");
    }

    public void Frame()
    {
        var player = d.Player;
        // the horn key (honk_horn, G): played every frame it is down, the car's horn file, a mode-2 voice (held, looping)
        bool honk = player?.Sim is { } pv && (pv.Flags & VehicleFlags.Dead) == 0 && !Controls.Suspended && d.InputHeld?.Invoke() != true
            && !(d.Remote is null && d.Session.Cutscene) && Controls.Down("HONK_HORN");
        Held(ref _horn, honk, player, "Horn", honk && player?.Sim is { } hv ? HornName(hv) : null);
        // the wind: every view but the cockpit and the binoculars (kept going; both stop it)
        Held(ref _wind, player is not null && d.InCockpit?.Invoke() != true, player, "Wind", "adwind.wav");

        // other cars: a network car with bit 0x200000 honks positionally; a one-shot of the
        // file, begun again once it has ended while the key stays down
        foreach (var c in d.Cars)
            if (c != player && (c.Sim.Flags & (VehicleFlags.Honking | VehicleFlags.Dead)) == VehicleFlags.Honking)
                Once(_others, c.Sim, HornName(c.Sim), "RemoteHorn");
        // the AI's honk when blocked by a vehicle (1 in 4, PtableEscapes)
        foreach (var v in d.World.Step.Honks) Once(_others, v, HornName(v), "AiHorn");
        // a ramp or bridge driven onto: no response, vvbo1.wav
        foreach (var v in d.World.Step.DriveOns) Once(_bumps, v, "vvbo1.wav", "RampThump");

        Sirens();
    }

    /// <summary>
    /// The sirens, the edges of Entity.Siren (setSiren, and the honk of a police car whose horn is the siren): on, the siren
    /// (<see cref="VehicleFactory.SirenSound"/>) at the car with no voice settings, so the file's own GAS0 header decides: mode 1,
    /// priority 60, two voices of it at once, so it loops until it is stopped. Off, the car's siren is stopped; so is it when the
    /// car's death timer ends in a wreck (it wails through the 10 s the car burns).
    /// </summary>
    private void Sirens()
    {
        if (d.Script?.Host.Entities is not { } entities || d.Audio is null) return;
        const string file = VehicleFactory.SirenSound;
        _sirenLoops ??= I76.Formats.Audio.SoundFiles.Loops(d.Audio.Vfs, file);
        foreach (var e in entities)
        {
            if (e.Vehicle is not { } v) continue;
            bool on = e.Siren && !v.Wreck, was = _sirenWas.GetValueOrDefault(e);
            if (on == was) continue;
            _sirenWas[e] = on;
            if (!on) { if (_sirens.Remove(v, out var s) && Live(s)) { s.Stop(); s.QueueFree(); Voice3D.Trace($"stop Siren on {e.Name}"); } }
            else if (_sirenLoops != true) Once(_sirens, v, file, "Siren");
            else if (d.Audio.Get(file, loop: true) is { } loop && d.Cars.FirstOrDefault(c => c.Sim == v) is { } car)
            {
                if (_sirens.Remove(v, out var old) && Live(old)) { old.Stop(); old.QueueFree(); }
                var p = Voice3D.OneShot(loop, file, d.Audio.Limits(file));
                p.Name = "Siren";
                car.AddChild(p);
                Voice3D.Trace($"start Siren ({file}, loop) on {e.Name}");
                p.Start();
                _sirens[v] = p;
            }
        }
    }

    private bool? _sirenLoops;
}
