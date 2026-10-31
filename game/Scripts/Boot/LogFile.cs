using System;
using System.IO;
using System.Linq;
using Godot;

namespace I76.Game.Boot;

/// <summary>
/// The log file (user://logs/godot.log, Godot's own). Off unless the player turns on Graphic Detail's Log File or forces it for a
/// bug report with --log or I76_LOG=1 (docs/dev-hooks.md). Godot reads file logging only at start, from project settings, so the
/// setting is written to the override file project.godot names (application/config/project_settings_override; <see cref="StartupSettings"/>) and takes effect on
/// the next start. A forced run gets the same file from <see cref="ForcedLogger"/>, which needs no restart. Stdout is unaffected.
/// </summary>
public static partial class LogFile
{
    public const string LogDir = "user://logs";

    /// <summary>--log or I76_LOG (anything but empty or 0) on this run.</summary>
    public static bool Forced =>
        OS.GetCmdlineUserArgs().Contains("--log") || System.Environment.GetEnvironmentVariable("I76_LOG") is { Length: > 0 } v && v != "0";

    /// <summary>Whether Godot itself is writing the log this run.</summary>
    public static bool NativeOn => (bool)ProjectSettings.GetSetting("debug/file_logging/enable_file_logging", false);

    /// <summary>A forced run whose Godot is not logging gets a logger of ours, before the first line.</summary>
    public static void InstallForced()
    {
        if (!Forced || NativeOn) return;
        try { OS.AddLogger(new ForcedLogger()); }
        catch (Exception e) { GD.PrintErr($"--log: {e.Message}"); }
    }

    public partial class ForcedLogger : Logger
    {
        private readonly object _lock = new();
        private readonly StreamWriter? _w;

        public ForcedLogger()
        {
            string dir = ProjectSettings.GlobalizePath(LogDir);
            try
            {
                Directory.CreateDirectory(dir);
                string cur = System.IO.Path.Combine(dir, "godot.log");
                // as Godot's own rotation: the last log keeps a timestamp, and only the newest few stay
                if (File.Exists(cur)) File.Move(cur, System.IO.Path.Combine(dir, $"godot{DateTime.Now:yyyy-MM-ddTHH.mm.ss}.log"), true);
                foreach (var old in new DirectoryInfo(dir).GetFiles("godot*.log").OrderByDescending(f => f.Name).Skip(2)) old.Delete();
                _w = new StreamWriter(cur, false) { AutoFlush = true };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _w = null; }
        }

        public override void _LogMessage(string message, bool error)
        {
            if (_w is null) return;
            lock (_lock) _w.Write(message.EndsWith('\n') ? message : message + "\n");
        }

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if (_w is null) return;
            lock (_lock) _w.Write($"{(errorType == 1 ? "WARNING" : "ERROR")}: {(rationale.Length > 0 ? rationale : code)}\n   at: {function} ({file}:{line})\n");
        }
    }
}
