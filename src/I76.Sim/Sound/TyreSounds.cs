using I76.Sim.Vehicles;

namespace I76.Sim.Sound;

/// <summary>
/// The player's tyre sounds. The car
/// update calls it once a frame for every car with the frame's
/// flags and surface, but it does anything only for the
/// object with flag 0x10, the player: the AI's cars are silent here. Each name
/// it asks for goes to a looping voice on the car that is kept
/// alive by being asked for again and stopped by the frame-end pump at the first
/// frame it is not, so what plays is exactly the set returned for the frame.
/// </summary>
public static class TyreSounds
{
    /// <summary>The surface rows (stride 12): the skid, turn and rolling sounds per surface id.</summary>
    public static readonly (string Skid, string Turn, string? Roll)[] Rows =
    [
        ("tskid2.wav", "tturn2.wav", "vcddirt.wav"),   // 0
        ("tskid3.wav", "tturn3.wav", "vcdsand.wav"),   // 1
        ("tskid1.wav", "tturn1.wav", null),            // 2 (road: rolls silent)
        ("tskid2.wav", "tturn2.wav", "vcddirt.wav"),   // 3
        ("tskid2.wav", "tturn2.wav", "vcddirt.wav"),   // 4
        ("tskid4.wav", "tturn4.wav", "vcdgrav.wav"),   // 5
        ("tskid1.wav", "tturn1.wav", null),            // 6
        ("tskid2.wav", "tturn2.wav", "vcddirt.wav"),   // 7
    ];

    /// <summary>The drive scale above this: the turn / rolling sounds, the flat tyre and the brake grind (7.0).</summary>
    public const float FastSpeed = 7f;

    /// <summary>The flat-tyre loop: any of the four corner tyres blown.</summary>
    public const string Flat = "tflat.wav";

    /// <summary>The brake grind: braking harder than half (throttle &lt; −0.5) on brakes below half their strength.</summary>
    public const string BrakeGrind = "fdmg1.wav";

    /// <summary>
    /// The port's headroom on the car's looping voices, the engine's (CarNode, BaseDb -6): the original plays the engine
    /// loop and these at the same volume 100 (the original sets no volume; the vcd*, tflat and fdmg1 files leave
    /// the GAS0 slot at the default), so rolling on gravel sits 3.6 dB under the engine in RMS. Held at 0 dB beside an
    /// engine at -6 dB it sat 2.4 dB over it (#127).
    /// </summary>
    public const float MixDb = -6f;

    /// <summary>
    /// The voice's own volume: the tskid/tturn files carry 50 in their GAS0 header's volume slot (the others
    /// leave it at the default 100), and the original sets 400·log2(v/100) mB, so −4 dB; <see cref="MixDb"/> on top.
    /// </summary>
    public static float VolumeDb(string name) =>
        MixDb + (name.StartsWith("tskid", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tturn", StringComparison.OrdinalIgnoreCase) ? -4f : 0f);

    /// <summary>
    /// The names the original asks for this frame, in its order. <paramref name="isPlayer"/> is the object's
    /// flag 0x10; <paramref name="brakesWorn"/> is the brake strength &lt; the nominal · 0.5 (the component).
    /// </summary>
    public static void Wanted(Vehicle v, bool isPlayer, bool brakesWorn, List<string> into)
    {
        into.Clear();
        // the car update never runs for a helicopter
        if (!isPlayer || v.IsHeli || (v.Flags & VehicleFlags.Airborne) != 0) return;
        var row = Rows[v.Surface & 7];
        bool skid = false;
        if ((v.Flags & VehicleFlags.Sliding) != 0) { skid = true; into.Add(row.Skid); }   // at any speed
        if (!(v.Speed > FastSpeed)) return;
        if (!skid)
        {
            // the turn sound while the grip model blends (0x2000), else the surface's rolling sound if it has one
            if ((v.Flags & VehicleFlags.Blending) != 0) into.Add(row.Turn);
            else if (row.Roll is { } roll) into.Add(roll);
        }
        if (v.FrontLeft.Blown || v.FrontRight.Blown || v.RearLeft.Blown || v.RearRight.Blown) into.Add(Flat);
        if (v.Throttle < -0.5f && brakesWorn) into.Add(BrakeGrind);
    }
}
