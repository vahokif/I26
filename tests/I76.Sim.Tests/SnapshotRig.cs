using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Sim.Tests;

/// <summary>
/// Builds sessions from an install the way a mission start does (MissionSetup, the session, the player's driver,
/// a melee's start), and drives them with inputs that are a fixed function of the tick, so a run restored from a
/// snapshot can be fed exactly what the original was.
/// </summary>
public static class SnapshotRig
{
    public static string? Install => Dir("I76_INSTALL");
    public static string? NitroInstall => Dir("I76_NITRO_INSTALL");
    private static string? Dir(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>An install opened for one worker (the VFS and the assembler are not thread-safe), with the missions it has read.</summary>
    public sealed class Loaded(string dir)
    {
        public string Dir { get; } = dir;
        public GameInstall Install { get; } = new(dir);
        public AssetVfs Vfs => _vfs ??= new AssetVfs(Install);
        public ModelAssembler Asm => _asm ??= new ModelAssembler(Vfs);
        private AssetVfs? _vfs;
        private ModelAssembler? _asm;
        public readonly Dictionary<string, MissionWorld> Worlds = [];
    }

    /// <summary>The missions a corpus test runs: every .msn (scripted missions, the arenas as plain missions).</summary>
    public static IEnumerable<string> Missions(string dir)
    {
        var miss = new GameInstall(dir).Resolve("miss16")!;
        return Directory.EnumerateFiles(miss).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f))
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).Order();
    }

    public static SessionBuild BuildOf(string dir, string mission, string? car = null, IReadOnlyList<string>? melee = null, MeleeRules? rules = null, CoopSetup? coop = null) =>
        new() { Mission = mission, Install = SessionBuild.InstallIdentity(new GameInstall(dir), mission), Car = car, Melee = melee, Rules = rules, Coop = coop };

    /// <summary>A session built as a mission start builds it, with <paramref name="seed"/> for its rand stream; network players join as <paramref name="players"/> says.</summary>
    public static (SimSession Session, MissionSetup Setup) Make(Loaded l, SessionBuild b, SimRate rate, uint seed, IReadOnlyList<(string Car, int Team)>? players = null)
    {
        if (!l.Worlds.TryGetValue(b.Mission, out var world))
            l.Worlds[b.Mission] = world = new MissionWorld(l.Vfs, l.Asm, l.Install.FindMission(b.Mission) ?? throw new FileNotFoundException(b.Mission));
        var setup = MissionSetup.Build(l.Vfs, world, l.Asm, b.Car, script: b.Script, rand: new CrtRand(seed), melee: b.Melee, meleeRules: b.Rules, seed: unchecked((int)seed), coop: b.Coop);
        bool network = b.Rules?.Network == true;
        setup.Melee?.Start();
        var s = new SimSession(setup, network ? SessionOptions.Network : SessionOptions.SinglePlayer, rate) { Build = b };
        foreach (var h in setup.Humans) s.AddDriver(h);
        if (players is not null && setup.Melee is { } m)
            foreach (var (car, team) in players) { var mp = m.AddPlayer(car, "", team); s.AddDriver(mp.Car); s.AssignIds(); }
        return (s, setup);
    }

    private static uint Hash(int a, int b, int c)
    {
        uint h = 2166136261;
        foreach (int x in (int[])[a, b, c]) { h ^= (uint)x; h *= 16777619; h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12; }
        return h;
    }

    /// <summary>The first tick (from <see cref="SkipFrom"/>) on which the inputs press the skip key, and every 40 after.</summary>
    public const int SkipFrom = 320;

    /// <summary>
    /// A driver's keys at a tick: mostly the throttle, steering in spells, the fire key and hardpoints in bursts,
    /// now and then the hotkeys (weapons, targets, radar, specials, lights, shifts, a poem), and from 16 s the skip
    /// key every two seconds (so cut-scenes end and the missions get going).
    /// </summary>
    public static CarInput Input(int tick, int slot, bool hotkeys = true, Func<int, int, CarCommands>? extra = null)
    {
        uint seg = Hash(tick / 9, slot, 1), t = Hash(tick, slot, 2);
        var b = CarButtons.None;
        short steer = 0, throttle = 0;
        if (((seg >> 26) & 7) == 0)
        {
            // a pad's axes now and then
            b |= CarButtons.Analog;
            steer = (short)((int)(seg % 65535) - 32767);
            throttle = (short)((int)((seg >> 7) % 50000) - 15000);
        }
        else
        {
            if ((seg & 7) < 6) b |= CarButtons.ThrottleUp; else if ((seg & 7) == 6) b |= CarButtons.ThrottleDown;
            switch ((seg >> 3) & 7) { case 0 or 1: b |= CarButtons.SteerLeft; break; case 2 or 3: b |= CarButtons.SteerRight; break; }
        }
        if (((seg >> 6) & 3) == 0) b |= CarButtons.Fire;
        if (((seg >> 8) & 15) == 0) b |= (CarButtons)((uint)CarButtons.Hardpoint1 << (int)((seg >> 12) % 5));
        if (((seg >> 16) & 31) == 0) b |= CarButtons.EBrake;
        if (((seg >> 21) & 7) == 0)
        {
            // in the cockpit, glancing out of a window: the arm comes up and its handgun takes the fire key
            b |= CarButtons.Cockpit;
            if ((seg & (1u << 24)) != 0) b |= CarButtons.RaiseRightArm;
            if ((seg & (1u << 25)) != 0) b |= CarButtons.RaiseLeftArm;
        }
        if (((seg >> 29) & 7) == 0) b |= CarButtons.Horn;
        var c = CarCommands.None;
        if (tick >= SkipFrom && (tick - SkipFrom) % 40 == 0) c |= CarCommands.Skip;
        if (hotkeys)
        {
            switch (t % 400)
            {
                case 0: case 1: c |= CarCommands.WeaponCycle; break;
                case 2: c |= CarCommands.WeaponLink; break;
                case 3: case 4: case 5: c |= CarCommands.TargetNearest; break;
                case 6: case 7: c |= CarCommands.TargetNext; break;
                case 8: c |= CarCommands.TargetFrontal; break;
                case 9: c |= CarCommands.RadarCamera; break;
                case 10: c |= CarCommands.RadarRange; break;
                case 11: c |= CarCommands.Special1; break;
                case 12: c |= CarCommands.Special2; break;
                case 13: c |= CarCommands.Special3; break;
                case 14: c |= CarCommands.Lights; break;
                case 15: c |= CarCommands.Poem; break;
                case 16: c |= CarCommands.TargetPrev; break;
                case 17: c |= CarCommands.ShiftDown; break;
                case 18: c |= CarCommands.ShiftUp; break;
                case 19: c |= CarCommands.TargetReset; break;
                case 20: c |= CarCommands.Reverse; break;
            }
            // the ignition key: the engine off, and on again a few seconds later
            if (tick % 1500 is 700 or 790) c |= CarCommands.StartEngine;
        }
        if (extra is not null) c |= extra(tick, slot);
        return new CarInput { Buttons = b, Commands = c, Steer = steer, Throttle = throttle };
    }

    /// <summary>One tick with every driver's keys, then what the views do after it: take the frame's events.</summary>
    public static void Tick(SimSession s, bool hotkeys = true, Func<int, int, CarCommands>? extra = null)
    {
        int t = s.Ticks;
        var inputs = new CarInput[s.Drivers.Count == 0 ? 0 : s.Drivers.Max(d => d.Slot) + 1];
        for (int i = 0; i < inputs.Length; i++) inputs[i] = Input(t, i, hotkeys, extra);
        s.Tick(inputs);
    }

    /// <summary>The views' part after a tick: they take the frame's events (CombatView, DamageView).</summary>
    public static void TakeEvents(SimSession s)
    {
        s.Combat?.Events.Clear();
        (s.Combat?.Damage as Damage.DamageModel)?.Events.Clear();
    }
}
