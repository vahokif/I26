using System.Globalization;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;

namespace I76.Cli;

/// <summary>
/// Sleeping cars against their scripts, over missions (issue #43):
///
///   i76 wakecheck [SECONDS] [MISSION...]     (default 120 s; every scripted mission of the install)
///
/// Each mission runs headless with nobody at the wheel after the intro, and <see cref="WakeAudit"/> reports, per car,
/// when the script first woke it and first fired, and any finding: moving or firing while its order is none, sit or
/// hide, or a radar contact while hidden.
/// </summary>
public static class WakeCheck
{
    public static int Run(GameInstall install, List<string> args)
    {
        float seconds = args.Count > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 120;
        var names = args.Skip(1).Where(a => !float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _)).ToList();
        var vfs = new AssetVfs(install);
        if (names.Count == 0)
            names = install.Resolve("miss16") is { } dir
                ? Directory.EnumerateFiles(dir, "*.msn", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                    .Select(n => Path.GetFileNameWithoutExtension(n).ToLowerInvariant()).OrderBy(n => n).ToList()
                : [];
        int findings = 0;
        foreach (var name in names)
        {
            var msn = install.FindMission(name);
            if (msn is null) { Console.WriteLine($"{name}: not found"); continue; }
            var asm = new ModelAssembler(vfs);
            MissionSetup setup;
            try { setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, msn), asm, rand: new I76.Sim.Rng.CrtRand(1)); }
            catch (Exception ex) { Console.WriteLine($"{name}: did not build ({ex.Message})"); continue; }
            if (setup.Script is null) { Console.WriteLine($"{name}: no script"); continue; }
            var session = new SimSession(setup);
            if (setup.World.Player is { } p) session.AddDriver(p);
            var audit = new WakeAudit(setup);
            var h = setup.Script.Host;
            var input = new CarInput();
            for (int f = 0; f < (int)(seconds / session.TickSeconds); f++)
            {
                session.Tick([input]);
                audit.Tick(h.Now, session.TickSeconds);
                setup.Combat.Events.Clear();
            }
            var bad = audit.Findings.ToList();
            findings += bad.Count;
            Console.WriteLine($"{name}: {audit.Cars.Count} cars, {audit.Cars.Count(c => c.Woke is not null)} woke, {bad.Count} findings");
            foreach (var c in audit.Cars)
            {
                string F(float? t) => t is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "-";
                string flag = (c.MovedAsleep is not null ? $" MOVED asleep at {F(c.MovedAsleep)} ({c.MovedOrder})" : "")
                    + (c.FiredAsleep is not null ? $" FIRED asleep at {F(c.FiredAsleep)}" : "")
                    + (c.LiveHidden is not null ? $" ON RADAR hidden at {F(c.LiveHidden)}" : "");
                if (flag.Length > 0 || Environment.GetEnvironmentVariable("I76_WAKE_ALL") is not null)
                    Console.WriteLine($"  {c.Entity.Name,-10} woke {F(c.Woke)} ({c.WokeBy?.ToString() ?? "-"}) first shot {F(c.FirstShot)}{flag}");
            }
        }
        Console.WriteLine($"{names.Count} missions, {findings} findings");
        return findings == 0 ? 0 : 1;
    }
}
