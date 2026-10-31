using I76.Net.Hud;
using I76.Sim.Missions;

namespace I76.Game.Net;

/// <summary>
/// What the network HUD shows this frame, gathered by <see cref="NetHud"/> from the client (or made up by
/// I76_NET_HUD_DEMO) and drawn by one of the two looks: <see cref="NetHudClassic"/> (the 1997 look) or
/// <see cref="NetHudModern"/>. The looks only draw; the keys and the rules stay in NetHud.
/// </summary>
public sealed class NetHudFrame
{
    public double T;
    public MeleeKind Kind;
    public MeleeGoal Goal;
    public bool TeamPlay;
    public int GoalValue;
    public float TimeLimit;
    public float Clock;
    public bool HasScores;
    public int MySlot = -1, MyTeam;
    /// <summary>The original's goal line ("N of M Points", the time left), the 1997 look's.</summary>
    public string GoalLine = "";
    /// <summary>The leader's count toward the goal, this player's (or team's) own, and the place it holds (races: by laps).</summary>
    public int Leader, Mine, Place, Field;
    public List<(int Team, byte State, string? Carrier, int Captures)> Flags = [];
    public int Ping;
    /// <summary>The connection's state when it is not simply joined ("Migrating", "Disconnected: ...").</summary>
    public string? Connection;
    public string? HostStatus;
    public string? Debug;
    /// <summary>The standings while their key is held: the players' page, grouped under their teams' totals in team play.</summary>
    public ScoreView? Board;
    /// <summary>The key that holds the standings, as the player's bindings name it (a keycap's label), and whether to remind of it now (until it has been used).</summary>
    public string ScoresKey = "Tab";
    public bool ScoresHint;
    public HudFeed Feed = new();
    /// <summary>The keys that chat and bring a new car (self-destruct) as the bindings name them, and whether each applies here (the standings list them).</summary>
    public string ChatKey = "Backspace", NewCarKey = "Ctrl+Delete";
    public bool ChatHint, NewCarHint;
    public bool ChatOpen;
    public string ChatText = "";
    /// <summary>The CB radio (voice chat): this player is transmitting, who else is on the air (name, team), the key that talks, and whether the CB is on (the standings list its key).</summary>
    public bool CbTx;
    public List<(string Name, int Team)> CbTalking = [];
    public string CbKey = "K";
    public bool CbHint;
    /// <summary>The host has muted this player on the CB for everyone.</summary>
    public bool CbMuted;
    public DeathInfo? Death;
    public EndInfo? End;
    public FinalInfo? Final;
    public PromptInfo? Prompt;
    /// <summary>A pad is connected: the prompts show its buttons too.</summary>
    public bool Pad;
    public bool Host;
    /// <summary>A co-op story mission: no goal, no places; a death comes back by itself beside a teammate.</summary>
    public bool Coop;

    public sealed record DeathInfo(string? Killer, int KillerTeam, bool Self, double Left, double Delay, string RaiseKey, string? NextCar);
    /// <summary>The goal was reached (or the time ran out): the original's line on this machine, and who won.</summary>
    public sealed record EndInfo(string? Line, bool Won, bool TimeUp, string? Winner, int WinnerTeam, int Count, double At);
    public sealed record Standing(int Rank, string Name, int Team, int Score, int Kills, int Deaths, bool Me);
    public sealed record FinalInfo(IReadOnlyList<Standing> Standings, double? NextRound);
    public enum PromptKind { Invite, Boot }
    public sealed record PromptInfo(PromptKind Kind, string Name);

    public string TeamName(int team) => MeleeGame.TeamName(Kind, team);
}
