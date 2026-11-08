using I76.Game.Shell;
using I76.Game.Sim;
using I76.Game.Ui;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The Developer panel's Settings (the [port] keys no pad sets, each value reading back, the defaults the file's) and its
/// Signals (the traces' window, the pad's motors and the telemetry feed).
/// </summary>
public class DevSettingsSignalsTests
{
    private static string RepoFile(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "I76.sln"))) return Path.Combine(d.FullName, rel);
        throw new FileNotFoundException(rel);
    }

    [Fact]
    public void EveryKeyIsOneTheSettingsFileReads()
    {
        var shell = File.ReadAllText(RepoFile(Path.Combine("game", "Scripts", "Shell", "Shell.cs")));
        foreach (var e in DevSettings.All) Assert.Contains($"GetValue(\"port\", \"{e.Key}\"", shell);
        Assert.Equal(DevSettings.All.Length, DevSettings.All.Select(e => e.Key).Distinct().Count());
    }

    [Fact]
    public void EveryValueReadsBack()
    {
        foreach (var e in DevSettings.All)
        {
            var s = new ShellSettings();
            for (int i = 0; i < e.Values.Length; i++)
            {
                e.Set(s, i);
                Assert.Equal(i, e.Get(s));
            }
        }
    }

    [Fact]
    public void TheDefaultsAreTheFilesAndTheOriginalsAreUnmarked()
    {
        var s = new ShellSettings();
        string Shown(string key) { var e = DevSettings.All.Single(d => d.Key == key); return e.Values[e.Get(s)]; }
        Assert.Equal("true*", Shown("net_names"));
        Assert.Equal("i26", Shown("telemetry_format"));
        Assert.Equal("false", Shown("developer"));
        foreach (var e in DevSettings.All)
            Assert.Single(e.Values, v => !v.EndsWith(DevSettings.PortMark, StringComparison.Ordinal));
    }

    [Fact]
    public void ATelemetryFormatReadsAsTheFeedReadsIt()
    {
        var e = DevSettings.All.Single(d => d.Key == "telemetry_format");
        var s = new ShellSettings();
        e.Set(s, 2);
        Assert.Equal(TelemetryFormat.OutGauge, TelemetryPackets.ParseFormat(s.TelemetryFormat));
        e.Set(s, 0);
        Assert.Null(s.TelemetryFormat);
    }

    [Fact]
    public void ATraceKeepsItsNewestAndShowsItsWindow()
    {
        var t = new SignalTrace("x", 0, 1, capacity: 4);
        for (int i = 0; i < 6; i++) t.Add(i, i / 10f);
        Assert.Equal(4, t.Count);
        Assert.Equal(0.5f, t.Last);
        Assert.Equal([3.0, 4.0, 5.0], t.Since(3).Select(s => s.T));
        Assert.Equal(0.5f, t.Peak(0));
    }

    [Fact]
    public void ThePadsMotorsAndTheTelemetryRateAreSampled()
    {
        DevSignals.Reset();
        DevSignals.Pad(0.25f, 0.75f);
        for (int i = 0; i < 30; i++) DevSignals.Telemetry(i / 60.0, 0.1f, -0.2f, 1, 2, 3);
        DevSignals.Sample(0.5);
        Assert.Equal(0.75f, DevSignals.PadLow.Last);
        Assert.Equal(0.25f, DevSignals.PadHigh.Last);
        Assert.Equal(30, DevSignals.TelRate.Last);     // packets in the last second
        Assert.Equal(-0.2f, DevSignals.TelRoll.Last);
        DevSignals.Reset();
    }
}
