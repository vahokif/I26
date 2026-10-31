namespace I76.Sim.Missions;

/// <summary>
/// The CB radio: a queue whose head is the line playing. Priorities:
/// 1 flush everything and say it now; 2 interrupt the current line, keep the
/// rest; 3 queue; 4 queue but drop after 10 s waiting; 5 only when idle;
/// 6 ambient that may queue behind ambient. Lines of priority ≤ 4 flush a
/// queue whose head is ambient (5/6).
/// </summary>
public sealed partial class CbRadio(Func<string, float?> lengthSeconds, Func<object, bool> speakerDead)
{
    public sealed class Line(string name, object? speaker, int priority, double queuedMs)
    {
        public string Name { get; } = name;
        public object? Speaker { get; } = speaker;
        public int Priority { get; } = priority;
        public double QueuedMs { get; } = queuedMs;
        public double StartedMs { get; set; } = -1;
        public float Length { get; set; }
    }

    public const string StopName = "STOPCBXX";
    public const string SpeakerDiedLine = "cmike.wav";
    private const double ExpireMs = 10000;
    private readonly List<Line> _q = [];

    /// <summary>The clock lines are queued and timed by (GetTickCount in the original).</summary>
    public double NowMs { get; set; }
    /// <summary>stopCB: every later line is ignored for the rest of the mission.</summary>
    public bool Stopped { get; private set; }
    public bool Empty => _q.Count == 0;
    public Line? Current => _q.Count > 0 && _q[0].StartedMs >= 0 ? _q[0] : null;
    public IReadOnlyList<Line> Queue => _q;
    public event Action<Line>? Started;
    /// <summary>A playing line was cut off (flush or interrupt).</summary>
    public event Action<Line>? Cut;

    public void Say(string name, object? speaker, int prio)
    {
        if (Stopped) return;
        if (name == StopName) { Stopped = true; return; }
        if (speaker is not null && speakerDead(speaker)) return;
        if (_q.Count > 0)
        {
            var head = _q[0];
            if (prio == 5) return;
            if (prio == 6 && head.Priority < 5) return;
            if (prio == 1 || (prio < 5 && head.Priority > 4)) Kill();
        }
        var node = new Line(name.Length > 15 ? name[..15] : name, speaker, prio, NowMs);
        if (_q.Count > 0)
        {
            if (prio != 2) { _q.Add(node); return; }
            Stop(_q[0]);
            _q[0] = node;
        }
        else _q.Add(node);
        StartHead();
    }

    /// <summary>
    /// A network client's radio: the line the host's radio has just started (the host's queue and priorities decided it),
    /// on the air here at once in place of whatever was, with no test of its speaker.
    /// </summary>
    public void Play(string name, object? speaker, int prio)
    {
        Kill();
        _q.Add(new Line(name.Length > 15 ? name[..15] : name, speaker, prio, NowMs));
        StartHead();
    }

    /// <summary>killCB: flush the whole queue, stopping the current line.</summary>
    public void Kill()
    {
        if (_q.Count > 0) Stop(_q[0]);
        _q.Clear();
    }

    /// <summary>Advance the clock; a line whose sound has ended hands over to the next.</summary>
    public void Update(double nowMs)
    {
        NowMs = nowMs;
        while (Current is { } c && NowMs - c.StartedMs >= c.Length * 1000.0)
        {
            _q.RemoveAt(0);
            while (_q.Count > 0 && _q[0].Speaker is { } s && speakerDead(s)) _q.RemoveAt(0);
            StartHead();
        }
    }

    /// <summary>A speaker died; drop its lines, and if it was talking, cut in with static.</summary>
    public void SpeakerDied(object speaker)
    {
        bool talking = Current?.Speaker == speaker;
        for (int i = _q.Count - 1; i >= (talking ? 1 : 0); i--)
            if (_q[i].Speaker == speaker) _q.RemoveAt(i);
        if (talking) Say(SpeakerDiedLine, null, 2);
    }

    private void Stop(Line l) { if (l.StartedMs >= 0) Cut?.Invoke(l); }

    private void StartHead()
    {
        while (_q.Count > 0)
        {
            var h = _q[0];
            if (h.Priority == 4 && NowMs - h.QueuedMs > ExpireMs) { _q.RemoveAt(0); continue; }
            if (lengthSeconds(h.Name) is not { } len) { _q.RemoveAt(0); continue; }
            h.StartedMs = NowMs;
            h.Length = len;
            Started?.Invoke(h);
            return;
        }
    }
}
