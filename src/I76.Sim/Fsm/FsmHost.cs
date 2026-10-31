using System.Numerics;

namespace I76.Sim.Fsm;

/// <summary>AI order ids the scripts issue (as the script's AI order takes them).</summary>
public enum AiOrder { Attack = 0, Goto = 0x16, Evade = 0x18, Race = 0x1a, Follow = 0x1e, Sit = 0x1f, Guard = 0x20, Hide = 0x21 }

/// <summary>
/// Everything a mission script touches outside its VM: objects, AI orders,
/// the CB radio, objectives, cameras, the mission's end. Objects are opaque
/// handles the host resolves (sim vehicles, scenery).
/// </summary>
public interface IFsmHost
{
    /// <summary>Mission time in float seconds (the mission clock; paused with the game).</summary>
    float Now { get; }
    /// <summary>The actor table's binding: placement instance id (raw 8 name bytes) → object.</summary>
    object? FindByInstanceId(byte[] id);
    bool IsRemoved(object o);
    bool IsDead(object o);
    bool IsVehicle(object o);
    bool IsStructure(object o);
    bool IsAirborne(object o);
    /// <summary>The health percentage: 0–100.</summary>
    float HealthPercent(object o);
    Vector3 Position(object o);
    int Team(object o);
    IEnumerable<object> Vehicles { get; }
    /// <summary>The AI's vehicle lists: every vehicle but the gun emplacements, by team, then as registered.</summary>
    IEnumerable<object> AiVehicles => Vehicles;
    /// <summary>The root colliders the structure test accepts: what nearestBlg and allBlgDead walk.</summary>
    IEnumerable<object> Structures { get; }

    void Order(object actor, object? target, AiOrder order, IReadOnlyList<Vector3>? path, float speed, int followFlag = 0, float f1 = 0, float f2 = 0, object? reference = null);
    /// <summary>Arrived at the end of the ordered path; reading consumes it.</summary>
    bool ConsumeArrived(object actor);
    bool ConsumeAtFollow(object actor);
    /// <summary>Damage events this or the previous FSM tick; a true result consumes them.</summary>
    bool ConsumeShot(object o);
    bool ConsumeRammed(object o);
    bool ConsumeAttacked(object o);
    object? LastAttacker(object o, int kind);   // 0 rammer, 1 shooter, 2 any attacker
    bool KilledByPlayer(object o);
    void SetSkill(object o, int a, int b);
    void SetAggression(object o, int n);
    void SetAvoid(object o, int? on);           // null toggles
    void SetMaxAttackers(object o, int n);
    /// <summary>setHeliHeight (the script's helicopter-height action): a helicopter's height; anything else warns.</summary>
    void SetHeliHeight(object o, int height) { }
    /// <summary>setArtillery (op 93): an emplacement's fire pass on or off.</summary>
    void SetArtillery(object o, int on) { }
    /// <summary>setArtilleryRate (op 94): the fire pass's rate.</summary>
    void SetArtilleryRate(object o, int rate) { }

    void CbSay(string sound, object? speaker, int priority);
    bool CbEmpty { get; }
    void CbKill();
    void CbStop();

    void ObjectiveSuccess(int n);
    void ObjectiveFail(int n);
    void ObjectiveReveal(int n);
    /// <summary>successAll / failAll / failAllObj: end the mission after a delay (seconds); failMessage −2 none.</summary>
    void EndMission(bool success, float delay, int failMessage);

    void Teleport(object o, Vector3 at, int angleDegrees, float speed);
    void Destroy(object o);
    void StartCar(object o);
    void SetLights(object o, bool on);
    void SetSiren(object o, bool on);
    void TriggerGate(object o);
    void Camera(string op, IReadOnlyList<int> args, IReadOnlyList<object?> objects, IReadOnlyList<Vector3>? path);
    bool CameraArrived();
    // ---- co-op personal camera scenes (CoopCameras): a seat's own view, the script's camera for that human alone ----
    /// <summary>A camera op of a personal scene: the seat's own camera (a host without them has the one).</summary>
    void PersonalCamera(int seat, string op, IReadOnlyList<int> args, IReadOnlyList<object?> objects, IReadOnlyList<Vector3>? path) => Camera(op, args, objects, path);
    bool PersonalCameraArrived(int seat) => CameraArrived();
    /// <summary>Whether the seat's personal camera still holds its view.</summary>
    bool PersonalSceneActive(int seat) => false;
    /// <summary>The scene's thread is done with the seat's camera: it is given back.</summary>
    void EndPersonalScene(int seat) { }
    /// <summary>The menu/skip key this frame (isKeypress).</summary>
    bool SkipPressed();
    void Control(object o, int braccel, int steer, int? ebrake, int? reverse);
    void DriveControl(object o);
    bool ControlDone(object o);
    void PlayMovie(string name);
    /// <summary>canSee (Nitro): a's position + 1.5 to b's + 1.5 clear of the terrain and of every object but vehicles and b.</summary>
    bool CanSee(object a, object b) => true;
    /// <summary>salvage (the salvage list's add with health −1, random condition).</summary>
    void Salvage(string file) { }
    void Log(string message);

    // ---- co-op (docs/multiplayer.md "Co-op"): the human group the player actor stands for; single player has no seats ----
    /// <summary>How many humans play (their seats 0..n−1); 0 outside co-op, where the player actor is one car as the original's.</summary>
    int CoopSeats => 0;
    /// <summary>Whether a handle is the player placement's (an actor slot holding it names the group).</summary>
    bool IsPlayerHandle(object o) => false;
    /// <summary>A seat's human (their entity), alive or not.</summary>
    object? Human(int seat) => null;
    /// <summary>The seat's human is in the game and alive.</summary>
    bool HumanAlive(int seat) => false;
    /// <summary>The seat of a human's handle; −1 for anything else.</summary>
    int HumanSeat(object o) => -1;
    /// <summary>The lead: the first living human (seat 0 with nobody alive). Cut-scenes drive this car.</summary>
    int LeadSeat => 0;
    /// <summary>The living human an order of <paramref name="commanded"/>'s at "the player" aims at: the nearest, with hysteresis.</summary>
    object? NearestHuman(object commanded) => null;
    /// <summary>whoX on the group: the attacker of the human attacked last (kind as <see cref="LastAttacker"/>).</summary>
    object? HumansLastAttacker(int kind) => null;
    /// <summary>The script teleported the lead: the other humans into formation behind it.</summary>
    void FormUp(int leadSeat) { }
    /// <summary>
    /// A human reached the leave-map routine's fail radius: true when they were brought back beside a teammate who is
    /// <paramref name="inBounds"/>, false (nobody to come back to) to fail the mission.
    /// </summary>
    bool Abandon(int seat, Func<int, bool> inBounds) => false;
    /// <summary>The leave-map routine warns a human (its CB line, for their screen alone).</summary>
    void LeaveWarning(int seat, string sound, int priority) { }
    /// <summary>The seat of the human whose hit made <paramref name="victim"/> the player's kill (isGroovesFault), −1 for none.</summary>
    int KillerSeat(object victim) => -1;
    /// <summary>
    /// An object's gate standing open, so that a trigger now shuts it: a point on its line and the way it slides
    /// (along the line, in the world); null for anything else (no gate, shut, or sliding).
    /// </summary>
    (Vector3 At, Vector3 Slide)? OpenGate(object o) => null;
    /// <summary>A human left on the near side of a gate the script shuts: put beside a teammate who is through it.</summary>
    void BringThrough(int seat, int beside) { }
}
