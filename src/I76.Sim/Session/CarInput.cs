using System.Buffers.Binary;

namespace I76.Sim.Session;

/// <summary>What a driver holds down during a tick: sampled once a tick.</summary>
[Flags]
public enum CarButtons : uint
{
    None = 0,
    ThrottleUp = 1 << 0,
    ThrottleDown = 1 << 1,
    SteerLeft = 1 << 2,
    SteerRight = 1 << 3,
    EBrake = 1 << 4,
    /// <summary>The fire key: the selected weapon and its linked group (or a raised arm's handgun).</summary>
    Fire = 1 << 5,
    /// <summary>The number keys, hardpoints 1–5 in sorted order: <c>Hardpoint1 &lt;&lt; i</c>.</summary>
    Hardpoint1 = 1 << 6,
    Hardpoint2 = 1 << 7,
    Hardpoint3 = 1 << 8,
    Hardpoint4 = 1 << 9,
    Hardpoint5 = 1 << 10,
    Horn = 1 << 11,
    /// <summary>The driver looks from the cockpit: the arms are live and gate the weapons (out of it they rest down).</summary>
    Cockpit = 1 << 12,
    /// <summary>The glance has turned to the right window: the right arm rises to it (its handgun).</summary>
    RaiseRightArm = 1 << 13,
    /// <summary>The glance has turned to the left window: the left arm rises to it.</summary>
    RaiseLeftArm = 1 << 14,
    /// <summary>Steer and throttle are analog positions (<see cref="CarInput.Steer"/>, <see cref="CarInput.Throttle"/>), not the key ramps.</summary>
    Analog = 1 << 15,
}

/// <summary>One-off commands, each applied once at the start of the tick it is sent with.</summary>
[Flags]
public enum CarCommands : uint
{
    None = 0,
    /// <summary>Tab: the gear's sign flips (drive state 1 reverse, 3 forward).</summary>
    Reverse = 1 << 0,
    Lights = 1 << 1,
    WeaponCycle = 1 << 2,
    WeaponLink = 1 << 3,
    TargetNearest = 1 << 4,
    TargetNext = 1 << 5,
    TargetFrontal = 1 << 6,
    TargetReset = 1 << 7,
    /// <summary>The poetry key (the player's own car only).</summary>
    Poem = 1 << 8,
    /// <summary>The script's skip key (isKeypress) this tick: single player's cut-scenes.</summary>
    Skip = 1 << 9,
    /// <summary>shift_up / shift_down (. and ,): the drive selector one step, 0..5, cars only.</summary>
    ShiftUp = 1 << 10,
    ShiftDown = 1 << 11,
    /// <summary>start_engine (S): set for this tick. Nothing reads it yet (no behaviour is documented).</summary>
    StartEngine = 1 << 12,
    /// <summary>special1..3 (6, 7, 8): the car's n-th special, by its kind.</summary>
    Special1 = 1 << 13,
    Special2 = 1 << 14,
    Special3 = 1 << 15,
    /// <summary>radar_range_toggle (R) and radar_camera_toggle (W).</summary>
    RadarRange = 1 << 16,
    RadarCamera = 1 << 17,
    /// <summary>Ctrl+Del / Ctrl+Alt+X: the car destroys itself.</summary>
    SelfDestruct = 1 << 18,
    /// <summary>prev_target (unbound by default): the reverse of TargetNext.</summary>
    TargetPrev = 1 << 19,
    /// <summary>The typed cheat getdown; single player only, never sent over the network.</summary>
    CheatGetDown = 1 << 20,
    /// <summary>The typed flat-tyre cheats: the player's FR, FL, RR, RL tyre blows; single player only.</summary>
    CheatFlatFR = 1 << 21,
    CheatFlatFL = 1 << 22,
    CheatFlatRR = 1 << 23,
    CheatFlatRL = 1 << 24,
    /// <summary>radar_range_reset (unbound by default): the radar back to its long range.</summary>
    RadarRangeReset = 1 << 25,
    /// <summary>
    /// RAISE_DEAD (game key 0x26, Space): a dead network player asks for a new car. The session ignores
    /// it; the network host hands it to <see cref="Missions.MeleeGame.RequestRespawn"/>, which refuses it before 5 s.
    /// </summary>
    RaiseDead = 1 << 26,
    /// <summary>
    /// RESET_UPRIGHT (the port's key, U): the car back on the terrain under it, upright, heading kept, at rest
    /// (<see cref="Vehicles.UprightReset"/>). The server's alone in a network game; a host may turn it off.
    /// </summary>
    ResetUpright = 1 << 27,
    /// <summary>Mouse wheel up with Wheel Up / Down set to Cycle Weapon: steps the weapon selection upward (reverse of <see cref="WeaponCycle"/>).</summary>
    WeaponCyclePrev = 1 << 28,
}

/// <summary>
/// One car's input for one tick: everything a human driver can do to the
/// simulation (SimDriver's keys, the cockpit's arms), so that a session can
/// be driven, recorded, replayed and sent over the wire as this alone. The
/// AI and the mission script write their cars' controls from inside the
/// tick instead. Twelve bytes on the wire (<see cref="Write"/>).
/// </summary>
public struct CarInput : IEquatable<CarInput>
{
    public const int Size = 12;
    public CarButtons Buttons;
    public CarCommands Commands;
    /// <summary>Analog steer, +1 left (the engine's sign), as ±32767; read only with <see cref="CarButtons.Analog"/>.</summary>
    public short Steer;
    /// <summary>Analog throttle, −1 full brake to +1 full throttle, as ±32767; read only with <see cref="CarButtons.Analog"/>.</summary>
    public short Throttle;

    /// <summary>
    /// The input while the player types a chat line: no driving, no weapons, no commands, no stick; the car coasts. Only the
    /// cockpit view's state stays (it is the camera, not a control).
    /// </summary>
    public readonly CarInput WhileTyping() => new() { Buttons = Buttons & (CarButtons.Cockpit | CarButtons.RaiseRightArm | CarButtons.RaiseLeftArm) };

    public readonly bool Has(CarButtons b) => (Buttons & b) != 0;
    public readonly bool Has(CarCommands c) => (Commands & c) != 0;

    /// <summary>Hardpoint key i (0–4).</summary>
    public readonly bool Hardpoint(int i) => (Buttons & (CarButtons)((uint)CarButtons.Hardpoint1 << i)) != 0;

    public void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Buttons);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)Commands);
        BinaryPrimitives.WriteInt16LittleEndian(b[8..], Steer);
        BinaryPrimitives.WriteInt16LittleEndian(b[10..], Throttle);
    }

    public static CarInput Read(ReadOnlySpan<byte> b) => new()
    {
        Buttons = (CarButtons)BinaryPrimitives.ReadUInt32LittleEndian(b),
        Commands = (CarCommands)BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
        Steer = BinaryPrimitives.ReadInt16LittleEndian(b[8..]),
        Throttle = BinaryPrimitives.ReadInt16LittleEndian(b[10..]),
    };

    public readonly bool Equals(CarInput o) => Buttons == o.Buttons && Commands == o.Commands && Steer == o.Steer && Throttle == o.Throttle;
    public override readonly bool Equals(object? o) => o is CarInput c && Equals(c);
    public override readonly int GetHashCode() => HashCode.Combine(Buttons, Commands, Steer, Throttle);
    public static bool operator ==(CarInput a, CarInput b) => a.Equals(b);
    public static bool operator !=(CarInput a, CarInput b) => !a.Equals(b);
    public override readonly string ToString() => $"{Buttons} | {Commands}{(Has(CarButtons.Analog) ? $" steer {Steer} throttle {Throttle}" : "")}";
}

/// <summary>
/// Commands pressed between ticks, waiting for the next one: each kind goes
/// out once a tick, so two presses of one key inside a tick land in two ticks
/// rather than one.
/// </summary>
public sealed class CommandQueue
{
    private readonly List<CarCommands> _pending = [];

    public void Push(CarCommands c)
    {
        for (var bit = (CarCommands)1; bit != 0 && (uint)bit <= (uint)c; bit = (CarCommands)((uint)bit << 1))
            if ((c & bit) != 0) _pending.Add(bit);
    }

    /// <summary>This tick's commands: the first pending of each kind.</summary>
    public CarCommands Take()
    {
        CarCommands taken = 0;
        for (int i = 0; i < _pending.Count; i++)
            if ((taken & _pending[i]) == 0) { taken |= _pending[i]; _pending.RemoveAt(i--); }
        return taken;
    }

    public void Clear() => _pending.Clear();
}
