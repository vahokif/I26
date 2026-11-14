namespace I76.Game.Sim;

/// <summary>
/// The modern look's muzzle light: the brief light a shot sets off at the muzzle, reaching the world in front of and beside the car
/// at night (the ground, the cliffs and slopes, the rocks, buildings and other cars), scaled by the weapon (a cannon reaches
/// further than a machine gun), and down to a trace in the sun. Two lights a gun: a small near omni light just ahead of the
/// muzzle, which is what the firing car itself takes (about half of what the light before the key gave it), and a far spot
/// light lifted above the barrel and aimed level along it, falling off slowly, so that flat ground, which a light at the muzzle's
/// height meets edge on, takes it out to the range and a cliff face square on to it more; its cone stops short of straight back,
/// where the car's own body stands between the flash and the ground behind it. The cabin's meshes take a third light of their
/// own (CombatView). [port] muzzle_reach in percent: 100 the table below; 0 the light as it was before the key (a 4 m pool, 0.8,
/// 1.2 m ahead of the muzzle, that lit little but the firing car: the near light at those figures, the far one dark); between and
/// above, each figure goes along the line from that one to the table's. I76_MUZZLE_REACH overrides the file for a run,
/// I76_MUZZLE_OFF=1 leaves the lights out (for measuring them). The original's (at night only) is an omni light of range 25 m
/// on objects alone, never the ground (docs/deviations.md); the 1997 look draws none of this.
/// </summary>
public static class MuzzleReach
{
    public const int Default = 100;
    public static int Percent = Default;
    public static readonly int? Hook = int.TryParse(System.Environment.GetEnvironmentVariable("I76_MUZZLE_REACH"), out int h) ? h : null;
    public static readonly bool Off = System.Environment.GetEnvironmentVariable("I76_MUZZLE_OFF") == "1";

    /// <summary>The file's word (0 to 200), the default when absent or not a number.</summary>
    public static int Parse(string? s) => int.TryParse(s, out int p) ? Math.Clamp(p, 0, 200) : Default;

    /// <summary>The setting now: the hook, else the file's, 0 to 200.</summary>
    public static int Setting => Math.Clamp(Hook ?? Percent, 0, 200);

    /// <summary>What fires: the light's row (by the weapon's ordnance kind).</summary>
    public enum Gun { Handgun, MachineGun, Cannon, Rocket, Mortar, Other }

    public static Gun Of(uint kind) => kind switch
    {
        0x12 => Gun.Handgun,
        1 => Gun.MachineGun,
        6 => Gun.Cannon,
        2 or 3 or 8 or 0x14 => Gun.Rocket,
        4 or 5 or 7 or 0x16 => Gun.Mortar,
        _ => Gun.Other,
    };

    /// <summary>One light: its reach (m), its energy and the falloff's exponent (Godot's attenuation: 1 the inverse distance, lower reaches further).</summary>
    public readonly record struct Lamp(float Range, float Energy, float Decay);

    /// <summary>
    /// One shot's light: the near light (<paramref name="Near"/>, an omni light <paramref name="Ahead"/> m ahead of the muzzle, on the
    /// firing car and what is beside the gun), the far light (<paramref name="Far"/>, a spot light <paramref name="Lift"/> m above the
    /// barrel at the same place, aimed level along it, its cone <paramref name="Cone"/> degrees either side of straight ahead, on the
    /// ground, the cliffs and the scenery out to its reach), how long they last (s), the near light's specular share (the car's
    /// gloss), and the cabin's light's energy (the in-car view's hood, at the old reach).
    /// </summary>
    public readonly record struct Flash(Lamp Near, Lamp Far, float Ahead, float Lift, float Cone, float Life, float Specular, float Cabin);

    /// <summary>The light before the key (CombatView, every gun alike, one omni light on the world and the cabin, no far light): what 0 gives.</summary>
    public static readonly Flash Before = new(new(4f, 0.8f, 1f), new(4f, 0f, 1f), 1.2f, 0f, 100f, 0.05f, 1f, 0.8f);

    /// <summary>
    /// The table at 100, at night. The near light is the light before the key's at under half its energy and a little further
    /// ahead (measured on the firing car: about half its brightening; it lights the hood about the guns and the ground at the
    /// nose). The far light sits 1.5-3 m above the barrel and falls off slowly (decay 0.15: the ground 30 m off takes a third of
    /// what the ground 5 m off takes, a cliff face square on to it more), and its cone stops 80 degrees short of straight back,
    /// where the car's own body would stand between the flash and the ground behind it. A cannon's flash is the largest; a
    /// rocket's and a mortar's launch flash last longer than a gun's.
    /// </summary>
    public static Flash Night(Gun g) => g switch
    {
        Gun.Handgun => new(new(3f, 0.28f, 1f), new(25f, 0.9f, 0.15f), 1.2f, 1.5f, 100f, 0.05f, 0.5f, 0.2f),
        Gun.MachineGun => new(new(4f, 0.36f, 1f), new(65f, 1.8f, 0.15f), 1.5f, 2.5f, 100f, 0.05f, 0.5f, 0.2f),
        Gun.Cannon => new(new(5f, 0.48f, 1f), new(85f, 2.8f, 0.15f), 1.8f, 3.0f, 100f, 0.07f, 0.5f, 0.2f),
        Gun.Rocket => new(new(4.5f, 0.4f, 1f), new(70f, 2.1f, 0.15f), 1.5f, 2.5f, 100f, 0.1f, 0.5f, 0.2f),
        Gun.Mortar => new(new(4f, 0.36f, 1f), new(60f, 1.8f, 0.15f), 1.5f, 2.5f, 100f, 0.08f, 0.5f, 0.2f),
        _ => Before,
    };

    /// <summary>The far light's cone edge (Godot's spot angle attenuation): below 1 it fades over the outer part of the cone, so the edge beside the car is soft.</summary>
    public const float ConeEdge = 0.3f;

    /// <summary>What is left of the table's energy in full sun: a trace, as a flash is in daylight.</summary>
    public const float DayFloor = 0.12f;

    /// <summary>
    /// A shot's light at <paramref name="daylight"/> (Daylight: 1 in full sun, 0 at night) and the setting
    /// <paramref name="percent"/>: each figure from <see cref="Before"/> (0) to the table's (100), on past it to 200. The daylight
    /// scales the table's energies only, so 0 is the light before the key, day and night.
    /// </summary>
    public static Flash For(Gun g, float daylight, int percent)
    {
        float t = Math.Clamp(percent, 0, 200) / 100f;
        var n = Night(g);
        var b = Before;
        float day = Daylight.Scale(daylight, DayFloor);
        static float Lerp(float a, float z, float k) => a * (1 - k) + z * k;
        Lamp L(Lamp a, Lamp z) => new(
            Math.Max(0.5f, Lerp(a.Range, z.Range, t)),
            Math.Max(0f, Lerp(a.Energy, z.Energy * day, t)),
            Math.Clamp(Lerp(a.Decay, z.Decay, t), 0.1f, 2f));
        return new Flash(
            L(b.Near, n.Near),
            L(b.Far, n.Far),
            Math.Max(0f, Lerp(b.Ahead, n.Ahead, t)),
            Math.Max(0f, Lerp(b.Lift, n.Lift, t)),
            Math.Clamp(Lerp(b.Cone, n.Cone, t), 30f, 179f),
            Math.Max(0.02f, Lerp(b.Life, n.Life, t)),
            Math.Clamp(Lerp(b.Specular, n.Specular, t), 0f, 1f),
            Math.Max(0f, Lerp(b.Cabin, n.Cabin, t)));
    }

    /// <summary>The light now: the setting (or the hook) at the mission's daylight.</summary>
    public static Flash Current(Gun g) => For(g, Daylight.Current, Setting);
}
