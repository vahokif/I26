using Godot;

namespace I76.Game.Boot;

/// <summary>
/// The start-up check for a newer release (<see cref="UpdateCheck"/>), once per run, in the background: when it finds one, a small line
/// "version X available" shows under the version in the corner of the game chooser and the main menu, and a click opens the release page.
/// Nothing is stored; any failure (offline, timeout, rate limit, bad answer) is silent apart from one line in the log.
/// I76_UPDATE_FAKE (a release answer as JSON) stands in for the network and I76_UPDATE_VERSION for the running version, for captures.
/// </summary>
public static class UpdateNotice
{
    private static bool _started;
    private static string? _tag, _url;

    /// <summary>"version 0.12.0 available", or null while nothing newer is known.</summary>
    public static string? Line => _tag is null ? null : $"version {UpdateCheck.Display(_tag)} available";
    public static event System.Action? Changed;

    private static string Running => System.Environment.GetEnvironmentVariable("I76_UPDATE_VERSION") is { Length: > 0 } v ? v : BuildInfo.Version;

    /// <summary>Asks once per run (later calls do nothing), unless the player turned the check off or this is a build without a release version.</summary>
    public static void Start(Node anywhere)
    {
        if (_started) return;
        string running = Running;
        string? fake = System.Environment.GetEnvironmentVariable("I76_UPDATE_FAKE");
        if (!UpdateCheck.HasVersion(running) || !I76.Game.Shell.ShellSettings.Load().UpdateCheck) return;
        _started = true;
        if (!string.IsNullOrEmpty(fake)) { Found(UpdateCheck.ParseRelease(fake), running); return; }
        var http = new HttpRequest { Timeout = UpdateCheck.TimeoutSeconds, Name = "UpdateCheck" };
        anywhere.GetTree().Root.CallDeferred(Node.MethodName.AddChild, http);
        http.RequestCompleted += (result, code, _, body) =>
        {
            if (result != (long)HttpRequest.Result.Success || code != 200) GD.Print($"update check: no answer ({(HttpRequest.Result)result}, HTTP {code})");
            else Found(UpdateCheck.ParseRelease(System.Text.Encoding.UTF8.GetString(body)), running);
            http.QueueFree();
        };
        http.Ready += () =>
        {
            var err = http.Request(UpdateCheck.Url, ["User-Agent: I26/" + UpdateCheck.Short(running), "Accept: " + UpdateCheck.Accept]);
            if (err != Error.Ok) { GD.Print("update check: could not start (" + err + ")"); http.QueueFree(); }
        };
    }

    private static void Found(( string Tag, string Url)? release, string running)
    {
        if (release is not { } r || !UpdateCheck.IsNewer(r.Tag, running)) return;
        _tag = r.Tag; _url = r.Url;
        Changed?.Invoke();
    }

    /// <summary>The line as a small dim link for a corner of the screen (hidden until a newer release is known; a click opens its page).</summary>
    public static LinkButton Link()
    {
        var l = new LinkButton { Name = "Update", Visible = Line is not null, Text = Line ?? "", FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, Modulate = new Color(1, 1, 1, 0.5f) };
        l.AddThemeFontSizeOverride("font_size", 11);
        l.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize);
        l.GrowHorizontal = Control.GrowDirection.Begin;
        l.GrowVertical = Control.GrowDirection.Begin;
        l.OffsetRight = -6; l.OffsetBottom = -22;
        l.Pressed += () => { if (_url is not null) OS.ShellOpen(_url); };
        void Update() { l.Visible = Line is not null; l.Text = Line ?? ""; }
        Changed += Update;
        l.TreeExiting += () => Changed -= Update;
        return l;
    }
}
