using System.Runtime.InteropServices;
using Godot;

namespace I76.Game.Boot;

/// <summary>
/// Sees the program out once it has decided to quit, past an engine hang on
/// Wayland.
///
/// Godot 4.7's Wayland display server can hang for good on exit
/// (WaylandThread::destroy, platform/linuxbsd/wayland/wayland_thread.cpp): it
/// sets the events thread's done flag and makes a roundtrip so that the
/// thread's poll() wakes and sees it, then joins the thread. But the main
/// thread's roundtrip can read the compositor's answer itself; if the events
/// thread was between dispatching and its poll() just then, it goes into
/// poll(fd, -1) after the answer has been read, with the flag checked only
/// after poll returns. A compositor that sends nothing more (the headless
/// Weston of tools/shot.sh, a window nobody touches) never wakes it, and the
/// main thread waits in ~DisplayServerWayland forever: about one capture in
/// forty, after its screenshot was written. (The X11 display server, the
/// default for a player, has no such thread.)
///
/// This node lives under the root, outside the scene, so it leaves the tree
/// only when the SceneTree is finalized, whatever asked for the quit (F10,
/// Exit Game, --quit-after, the window's close button). Then a background
/// thread waits: a normal exit ends the process within it. On Wayland it then
/// interrupts the events thread's poll() with a signal (SIGWINCH, given a
/// handler here, so the signal is delivered rather than discarded), and poll
/// returns EINTR, the thread sees its done flag and ends, and the engine's
/// cleanup goes on. If the process is still alive long after, it is ended
/// with _exit and the exit code it was quitting with.
///
/// It also makes SIGTERM (timeout(1), a session ending, kill) a quit like any
/// other. Left to .NET, SIGTERM runs the runtime's exit, and so the C++ static
/// destructors, on .NET's signal thread while the engine's main loop goes on:
/// "pure virtual method called", an abort and a core dump (the capture that
/// "hung until the timeout and crashed on exit"). Now the signal is taken, the
/// main loop quits at its next frame (exit code 143, as for a SIGTERM), and the
/// watch above starts at once, in case the main loop is not coming back.
/// </summary>
public partial class ExitWatchdog : Node
{
    private const int NudgeAfterMs = 1500, NudgeEveryMs = 250, ExitAfterMs = 15000;
    private static bool _added;
    private static PosixSignalRegistration? _winch, _term;
    private static volatile bool _wayland, _termRequested;
    private static int _watching;
    private static bool _off;
    private bool _quitting;

    /// <summary>The code the program is quitting with, for _exit (Godot's OS keeps its own out of reach of C#): set it beside a Quit(code).</summary>
    public static int ExitCode { get => _exitCode; set => _exitCode = value; }
    private static volatile int _exitCode;

    /// <summary>Puts the watchdog under the root once a run (from a node's _Ready: the root is busy then, so deferred).</summary>
    public static void Install(SceneTree tree)
    {
        // I76_EXIT_WATCHDOG=0 leaves the watch out (to see the engine's exit as it is); the node stays for the quit's wait for our threads
        if (_added) return;
        _added = true;
        _off = System.Environment.GetEnvironmentVariable("I76_EXIT_WATCHDOG") == "0";
        tree.Root.CallDeferred(Node.MethodName.AddChild, new ExitWatchdog { Name = "ExitWatchdog" });
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        // the window's close button quits once our threads that make engine resources are done (EngineThreads)
        GetTree().AutoAcceptQuit = false;
        if (_off) return;
        if (OperatingSystem.IsLinux() && DisplayServer.GetName() == "Wayland")
        {
            // a handler (which does nothing) so that the signal interrupts poll(); SIGWINCH's default is to ignore it,
            // and an ignored signal is dropped without waking anything
            try { _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => { }); _wayland = true; }
            catch (System.Exception ex) { GD.PushWarning($"exit watchdog: no SIGWINCH handler ({ex.Message})"); }
        }
        // (on .NET's signal thread: nothing of Godot's here, the next frame quits)
        try
        {
            _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                if (_termRequested) return;
                _termRequested = true;
                ExitCode = 143;
                StartWatch();
            });
        }
        catch (System.Exception ex) { GD.PushWarning($"exit watchdog: no SIGTERM handler ({ex.Message})"); }
    }

    public override void _Process(double delta)
    {
        if (!_termRequested || _quitting) return;
        _quitting = true;
        GD.Print("exit watchdog: SIGTERM, quitting");
        EngineThreads.QuitWhenDone(GetTree(), 143);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) EngineThreads.QuitWhenDone(GetTree());
    }

    // the SceneTree is being finalized: our threads that make engine resources are waited for (EngineThreads), then what
    // remains is the engine's own cleanup
    public override void _ExitTree()
    {
        EngineThreads.DrainAtExit(ExitCode);
        if (!_off) StartWatch();
    }

    private static void StartWatch()
    {
        if (System.OperatingSystem.IsMacOS()) return;   // the watch ends the process through libc.so.6 (Linux): nothing of it on macOS
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) != 0) return;
        new System.Threading.Thread(Watch) { IsBackground = true, Name = "I76 exit watchdog" }.Start();
    }

    /// <summary>Off the main thread, calling nothing of Godot's (it is being torn down): the console and libc only.</summary>
    private static void Watch()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Thread.Sleep(NudgeAfterMs);
        bool told = false;
        while (sw.ElapsedMilliseconds < ExitAfterMs)
        {
            if (_wayland && FindThread("Wayland Events") is int tid)
            {
                if (!told) System.Console.Error.WriteLine($"exit watchdog: the exit is held up {sw.ElapsedMilliseconds} ms; waking the Wayland events thread");
                told = true;
                Tgkill(System.Environment.ProcessId, tid, 28 /* SIGWINCH */);
            }
            System.Threading.Thread.Sleep(NudgeEveryMs);
        }
        System.Console.Error.WriteLine($"exit watchdog: still not out after {ExitAfterMs} ms; ending the process (exit code {ExitCode})");
        Exit(ExitCode);
    }

    /// <summary>This process's thread of that name (/proc/self/task/*/comm), or null.</summary>
    private static int? FindThread(string name)
    {
        try
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories("/proc/self/task"))
                if (System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "comm")).TrimEnd('\n') == name
                    && int.TryParse(System.IO.Path.GetFileName(dir), out int tid))
                    return tid;
        }
        catch (System.Exception) { }   // a thread gone between the listing and the read
        return null;
    }

    [DllImport("libc.so.6", EntryPoint = "tgkill", SetLastError = true)]
    private static extern int Tgkill(int tgid, int tid, int sig);

    [DllImport("libc.so.6", EntryPoint = "_exit")]
    private static extern void Exit(int status);
}
