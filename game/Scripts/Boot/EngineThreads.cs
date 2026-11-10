using System.Runtime.InteropServices;
using Godot;

namespace I76.Game.Boot;

/// <summary>
/// The threads of our own that make engine resources (textures, meshes, materials) beside the main thread: the 3D garage's
/// warm-up on the main menu and a mission built under its briefing. The engine must not be torn down under them: a quit while
/// one ran (the main menu's QUIT a second after it came up, the window closed during a briefing) ended the process with a
/// segmentation fault or an abort, the thread going on calling into an engine that was gone.
///
/// So a quit waits for them with the frames still running (<see cref="QuitWhenDone"/>: the main menu's QUIT and Options' Exit,
/// the window's close button, SIGTERM), as their engine calls can need the main thread's frames: a material's first shader,
/// for one, waited for the main thread, which no flush of the renderer's queue alone let go. A quit asked some other way finds
/// them at the scene tree's end (<see cref="DrainAtExit"/>): a short wait, and if one is still running the process ends there,
/// before the engine's teardown, rather than crash in it.
/// </summary>
public static class EngineThreads
{
    private static readonly List<System.Threading.Thread> Running = [];

    /// <summary>The game is quitting: a thread stops at its next step and calls nothing of the engine's to say why.</summary>
    public static bool Quitting => _quitting;
    private static volatile bool _quitting;
    private static bool _quitPending;

    /// <summary>Starts <paramref name="thread"/>, kept until it ends so that the quit waits for it.</summary>
    public static void Start(System.Threading.Thread thread)
    {
        lock (Running)
        {
            Running.RemoveAll(t => !t.IsAlive);
            Running.Add(thread);
        }
        thread.Start();
    }

    /// <summary>One of the threads is still running.</summary>
    public static bool Busy
    {
        get { lock (Running) return Running.Any(t => t.IsAlive); }
    }

    /// <summary>The longest a quit waits for the threads (ms) before it goes ahead.</summary>
    public const int QuitWaitMs = 20000;

    /// <summary>
    /// Quits the game once the threads are done (at once when none runs), the frames going on meanwhile; they stop at their
    /// next step. Asked again meanwhile, nothing more.
    /// </summary>
    public static async void QuitWhenDone(SceneTree tree, int code = 0)
    {
        _quitting = true;
        if (_quitPending) return;
        _quitPending = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool waited = false;
        while (Busy && sw.ElapsedMilliseconds < QuitWaitMs)
        {
            waited = true;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        if (waited) GD.Print($"quit: waited {sw.ElapsedMilliseconds} ms for the threads making engine resources");
        ExitWatchdog.ExitCode = code;
        tree.Quit(code);
    }

    /// <summary>
    /// On the main thread, as the scene tree is finalized (whatever asked for the quit): a short wait for a thread still running
    /// (the renderer's queue flushed meanwhile), and if it is still running after it, the process ends here with
    /// <paramref name="code"/>: the engine's teardown under it would crash.
    /// </summary>
    public static void DrainAtExit(int code, int limitMs = 3000)
    {
        _quitting = true;
        System.Threading.Thread[] left;
        lock (Running) left = [.. Running.Where(t => t.IsAlive)];
        if (left.Length == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var t in left)
            while (!t.Join(5) && sw.ElapsedMilliseconds < limitMs) RenderingServer.ForceSync();
        var still = left.Where(t => t.IsAlive).Select(t => t.Name).ToList();
        if (still.Count == 0) return;
        System.Console.Error.WriteLine($"quit: {string.Join(", ", still)} still making engine resources after {sw.ElapsedMilliseconds} ms; ending the process before the engine's teardown (exit code {code})");
        System.Console.Error.Flush();
        System.Console.Out.Flush();
        End(code);
    }

    /// <summary>Ends the process at once: no teardown, no exit handlers.</summary>
    private static void End(int code)
    {
        if (System.OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), (uint)code);
        else if (System.OperatingSystem.IsMacOS()) ExitMac(code);
        else ExitLinux(code);
    }

    [DllImport("kernel32.dll")]
    private static extern System.IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(System.IntPtr process, uint code);

    [DllImport("libc.so.6", EntryPoint = "_exit")]
    private static extern void ExitLinux(int status);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_exit")]
    private static extern void ExitMac(int status);
}
