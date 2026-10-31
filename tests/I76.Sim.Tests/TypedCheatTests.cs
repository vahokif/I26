using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The words typed with Ctrl+Shift held and what they do.</summary>
public class TypedCheatTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private const int CtrlShift = 0x300;

    private static Cheat Type(CheatTyper t, string s, int mods = CtrlShift)
    {
        var last = Cheat.None;
        foreach (char ch in s) last = t.Push(ch | mods);
        return last;
    }

    [Fact]
    public void EachWordActsOnItsLastLetter()
    {
        foreach (var (word, cheat) in CheatTyper.Words)
        {
            var t = new CheatTyper();
            Assert.Equal(Cheat.None, Type(t, word[..^1]));
            Assert.Equal(cheat, t.Push(word[^1] | CtrlShift));
        }
    }

    [Fact]
    public void OnlyExactlyCtrlShiftCodesEnterTheBuffer()
    {
        var t = new CheatTyper();
        Type(t, "get");
        // plain, Ctrl, Shift and Ctrl+Shift+Alt keys neither enter nor clear it: the word survives them
        Assert.Equal(Cheat.None, Type(t, "xyz", 0));
        Assert.Equal(Cheat.None, Type(t, "xyz", 0x100));
        Assert.Equal(Cheat.None, Type(t, "xyz", 0x200));
        Assert.Equal(Cheat.None, Type(t, "own", 0x700));
        Assert.Equal(Cheat.GetDown, Type(t, "down"));
        // the same word with Ctrl alone never acts
        Assert.Equal(Cheat.None, Type(new CheatTyper(), "getdown", 0x100));
    }

    [Fact]
    public void AnotherCtrlShiftKeyInTheMiddleBreaksTheWord()
    {
        var t = new CheatTyper();
        Type(t, "get");
        t.Push(0xb9 | CtrlShift);   // Ctrl+Shift+F9: its code goes in too
        Assert.Equal(Cheat.None, Type(t, "down"));
        // leading junk does not matter, only the end of the buffer
        Assert.Equal(Cheat.FlatBackRight, Type(t, "qqqqqqqqqqqqqqqqbrflat"));
        // and it can be typed again at once (wiggleburger toggles back)
        Assert.Equal(Cheat.WiggleBurger, Type(t, "wiggleburger"));
        Assert.Equal(Cheat.WiggleBurger, Type(t, "wiggleburger"));
    }

    [Fact]
    public void TheFlatWordsNameTheirTyres()
    {
        Assert.Equal(CarCommands.CheatFlatFL, CheatTyper.Command(Cheat.FlatFrontLeft));
        Assert.Equal(CarCommands.CheatGetDown, CheatTyper.Command(Cheat.GetDown));
        Assert.Equal(CarCommands.None, CheatTyper.Command(Cheat.Freelance));
        Assert.Equal(CarCommands.None, CheatTyper.Command(Cheat.ThirdNostril));
        Assert.Equal(CarCommands.None, CheatTyper.Command(Cheat.WiggleBurger));
        // the four tyre slots: fl, fr, bl, br
        Assert.Equal([1, 0, 5, 4], CheatTyper.Tyres.Select(t => t.Slot));
    }

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void AFlatWordBlowsTheTyreOnceAtZeroOfEighty()
    {
        var car = new Vehicle { Pos = new Double3(0, 0, 0) };
        car.FrontLeft.Offset = new Vector3(-1, 0.5f, 1.5f);
        car.FrontLeft.InitSuspension();
        car.Health = new VehicleDamage(car, true);
        var wp = new WheelPart(car.FrontLeft, 30);
        car.Health.Slots[1] = wp;
        var world = new SimWorld(new Flat(), new CrtRand(1));
        world.Vehicles.Add(car);
        world.Player = car;
        var dm = new DamageModel(world, new Combat(world, _ => null));
        float grip = car.FrontLeft.Grip;
        dm.FlattenTyre(car, 1);
        Assert.Equal((0, 80), (wp.Hp, wp.Max));
        Assert.True(car.FrontLeft.Blown);
        Assert.Equal(0.5f * 0.688f, car.FrontLeft.Rest, 5);
        Assert.Equal(grip, car.FrontLeft.Grip);   // a caltrop hit leaves grip as it was
        Assert.Equal(new TyreBlown(car, 1), Assert.Single(dm.Events));
        dm.FlattenTyre(car, 1);
        Assert.Single(dm.Events);                 // blown once
        Assert.Equal(0.5f * 0.688f, car.FrontLeft.Rest, 5);
        dm.FlattenTyre(car, 2);                   // no wheel in that slot: nothing
        Assert.Single(dm.Events);
    }

    private static (MissionScript Script, Entity Player, Entity[] Others) Mission(params (uint Team, bool Heli)[] others)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var pv = new Vehicle { Pos = new Double3(0, 0, 0) };
        world.Vehicles.Add(pv);
        world.Player = pv;
        var player = new Entity("player", [], 1, 0x10010) { Vehicle = pv };
        var list = new List<Entity> { player };
        for (int i = 0; i < others.Length; i++)
        {
            var v = others[i].Heli ? new Vehicle { Heli = new HeliState(), ObjectClass = 9 } : new Vehicle();
            v.Pos = new Double3(40 + 10 * i, 0, 0);
            world.Vehicles.Add(v);
            list.Add(new Entity($"e{i}", [], 1, others[i].Team << 16) { Vehicle = v });
        }
        var host = new ScriptHost(world, list, (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => 1f) { Tactics = TacticsTableTests.Installed };
        return (new MissionScript(null, host, new CrtRand(1)), player, [.. list.Skip(1)]);
    }

    [Fact]
    public void GetDownSendsTheHelicoptersUpByTeamAndCallsItIn()
    {
        var (script, player, o) = Mission((2, true), (1, false), (1, true), (2, true));
        var said = new List<string>();
        script.Host.Cb.Started += l => said.Add(l.Name);
        Assert.True(script.GetDown());
        // the AI lists' order: team 1 first, then team 2 as registered
        Assert.Equal(25f, o[2].Vehicle!.Heli!.Height);
        Assert.Equal(35f, o[0].Vehicle!.Heli!.Height);
        Assert.Equal(45f, o[3].Vehicle!.Heli!.Height);
        Assert.Equal(["gdsgc69.wav"], said);
        Assert.Equal(1000, player.MaxAttackers);
        Assert.False(script.GetDown());   // once a mission
    }

    [Fact]
    public void AfterGetDownTheScriptStopsAndEveryoneAttacks_TheMissionIsWonFiveSecondsAfterThePlayerDies()
    {
        if (TacticsTableTests.Installed is null) return;   // the attack orders run the AI's table, read from an install's executable
        var (script, player, o) = Mission((2, false), (2, false));
        script.GetDown();
        int tick = script.Host.FsmTick;
        script.AfterFrame(0.05f);
        Assert.All(o, e => Assert.Equal((AiOrder.Attack, player), (e.Order, e.OrderTarget)));
        Assert.Equal(tick + 1, script.Host.FsmTick);   // the tick counter goes on
        for (int f = 0; f < 200; f++) script.AfterFrame(0.05f);
        Assert.Null(script.Result);                    // alive: no end
        player.Vehicle!.Flags |= VehicleFlags.Dead;
        float died = script.Host.Now;
        while (script.Result is null && script.Host.Now < died + 10f) script.AfterFrame(0.05f);
        Assert.True(script.Result);
        Assert.InRange(script.Host.Now - died, 5f, 5.2f);
    }

    /// <summary>T01 for real: getdown, then the player self-destructs; 5 s later the mission is won (measured in the original: getdown at frame 1200, Ctrl+Del at 1350, game state 1 at 1492, the outro).</summary>
    [Fact]
    public void T01IsWonFiveSecondsAfterGetDownAndASelfDestruct()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t01.msn")!), asm);
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        var none = new CarInput();
        for (int t = 0; t < 40; t++) s.Tick([none]);
        s.Tick([none with { Commands = CarCommands.CheatGetDown | CarCommands.CheatFlatFL }]);
        Assert.True(setup.Script!.GotDown);
        Assert.True(setup.World.Player!.FrontLeft.Blown);
        s.Tick([none with { Commands = CarCommands.SelfDestruct }]);
        float died = setup.Script.Host.Now;
        while (setup.Script.Result is null && setup.Script.Host.Now < died + 10f) s.Tick([none]);
        Assert.True(setup.Script.Result);
        Assert.InRange(setup.Script.Host.Now - died, 5f, 5.3f);
    }
}
