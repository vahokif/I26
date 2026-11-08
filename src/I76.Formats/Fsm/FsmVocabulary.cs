namespace I76.Formats.Fsm;

/// <summary>What an operation's argument is: it decides how the source form shows a value passed there.</summary>
public enum FsmSlot
{
    /// <summary>A number (a speed, a radius, a priority, a time...).</summary>
    Value,
    /// <summary>An actor: an index into the actor table.</summary>
    Actor,
    /// <summary>A nav path: an index into the path table.</summary>
    Path,
    /// <summary>A sound or movie: an index into the sounds table.</summary>
    Sound,
    /// <summary>A cell the operation writes a number into.</summary>
    Out,
    /// <summary>A cell the operation writes an actor index into.</summary>
    OutActor,
}

/// <summary>
/// The scripts' call vocabulary: the 95 operation names every stock mission declares, in their table order (a script names an
/// operation by its index in its own mission's table), plus playScene, which the engine knows and no mission declares; and
/// each operation's arguments.
/// </summary>
public static class FsmVocabulary
{
    /// <summary>The prototype table of every stock mission.</summary>
    public static readonly IReadOnlyList<string> Standard =
    [
        "null", "behave", "attack", "sit", "goto", "follow", "guard", "evade", "driveControl", "camObjObj", "camObjDir", "camPosObj",
        "camPosDir", "camTransObj", "camTransDir", "do", "set", "inc", "dec", "rand", "success", "successAll", "fail", "failAll", "reveal",
        "playMovie", "setId", "setHeliHeight", "setSkill", "setAgg", "setUserRadar", "setSiren", "setAvoid", "whoAttacked", "whoRammed",
        "whoShot", "nearestEnemy", "nearestBlg", "destroy", "control", "controlAll", "startTimer", "cb", "teleport", "teleportOffset",
        "triggerGate", "toggleAvoid", "cbFrom", "salvage", "pushCam", "popCam", "setMaxAttackers", "true", "false", "failAllObj",
        "isEqual", "isGreater", "isLesser", "isEqualId", "timeLesser", "timeGreater", "test", "isWithin", "isWithinNav", "isWithinSqNav",
        "isWithinEnemy", "isAttacked", "isShot", "isRammed", "hpLesser", "isDead", "allBlgDead", "allEnemyDead", "ammoLesser", "isArrived",
        "isAtFollow", "camIsArrived", "controlDone", "isLit", "isCBEmpty", "canSee", "isAirborne", "isKeypress", "camF12", "killCB",
        "cbPrior", "cbFromPrior", "isGroovesFault", "setLights", "hide", "startCar", "race", "setArtillery", "setArtilleryRate", "stopCB",
    ];

    // O actor, N path, S sound, V value, W written number, A written actor index
    private static readonly Dictionary<string, string> Sig = new(StringComparer.OrdinalIgnoreCase)
    {
        ["null"] = "", ["behave"] = "OV", ["attack"] = "OOV", ["sit"] = "O", ["goto"] = "ONV", ["guard"] = "OO", ["do"] = "OV",
        ["set"] = "WV", ["inc"] = "W", ["dec"] = "W", ["rand"] = "WV", ["whoRammed"] = "OA", ["whoShot"] = "OA", ["startTimer"] = "W",
        ["success"] = "V", ["successAll"] = "V", ["fail"] = "V", ["failAll"] = "V", ["failAllObj"] = "VV", ["cb"] = "S", ["cbPrior"] = "SV",
        ["cbFromPrior"] = "SOV", ["playScene"] = "", ["nearestEnemy"] = "OA", ["nearestBlg"] = "OA", ["true"] = "", ["false"] = "",
        ["isEqual"] = "VV", ["isGreater"] = "VV", ["isLesser"] = "VV", ["test"] = "OV", ["isWithin"] = "OOV", ["isShot"] = "O",
        ["hpLesser"] = "OVV", ["ammoLesser"] = "OVV", ["isRammed"] = "O", ["isDead"] = "O", ["isLit"] = "O", ["timeGreater"] = "VV",
        ["timeLesser"] = "VV", ["isArrived"] = "O", ["isEqualId"] = "OO", ["isWithinNav"] = "NOV", ["isWithinSqNav"] = "NOV",
        ["isWithinEnemy"] = "OV", ["isAttacked"] = "O", ["whoAttacked"] = "OA", ["setId"] = "WV", ["follow"] = "OOOVVV",
        ["teleport"] = "ONVV", ["destroy"] = "O", ["playMovie"] = "S", ["pushCam"] = "", ["popCam"] = "", ["camObjObj"] = "OVVVO",
        ["camObjDir"] = "OVVVVVV", ["camPosObj"] = "NVO", ["camPosDir"] = "NVVVV", ["camTransObj"] = "NVVO", ["camF12"] = "OOVV",
        ["camTransDir"] = "NVVVVV", ["camIsArrived"] = "", ["isKeypress"] = "", ["triggerGate"] = "O", ["evade"] = "ONVO",
        ["toggleAvoid"] = "O", ["allBlgDead"] = "", ["allEnemyDead"] = "O", ["setHeliHeight"] = "OV", ["setSkill"] = "OVV",
        ["isAtFollow"] = "O", ["control"] = "OVV", ["controlAll"] = "OVVVVVV", ["driveControl"] = "O", ["controlDone"] = "O",
        ["reveal"] = "V", ["isCBEmpty"] = "", ["setAgg"] = "OV", ["setUserRadar"] = "V", ["cbFrom"] = "SO", ["killCB"] = "",
        ["salvage"] = "S", ["setSiren"] = "OV", ["setAvoid"] = "OV", ["setMaxAttackers"] = "OV", ["canSee"] = "OO", ["isAirborne"] = "O",
        ["teleportOffset"] = "ONVVVV", ["isGroovesFault"] = "O", ["hide"] = "O", ["startCar"] = "O", ["setLights"] = "OV", ["race"] = "ONVO",
        ["setArtillery"] = "OV", ["setArtilleryRate"] = "OV", ["stopCB"] = "",
    };

    /// <summary>Every operation the engine knows, with playScene.</summary>
    public static IEnumerable<string> Known => Standard.Append("playScene");

    public static bool IsKnown(string name) => Sig.ContainsKey(name);

    /// <summary>The operation's argument count, or −1 for a name the engine does not know.</summary>
    public static int Arity(string name) => Sig.TryGetValue(name, out var s) ? s.Length : -1;

    /// <summary>What argument <paramref name="i"/> of <paramref name="name"/> is (a value for unknown operations and extra arguments).</summary>
    public static FsmSlot SlotOf(string name, int i) =>
        Sig.TryGetValue(name, out var s) && i < s.Length ? s[i] switch
        {
            'O' => FsmSlot.Actor, 'N' => FsmSlot.Path, 'S' => FsmSlot.Sound, 'W' => FsmSlot.Out, 'A' => FsmSlot.OutActor, _ => FsmSlot.Value,
        } : FsmSlot.Value;

    /// <summary>The table's spelling of a known name, matched without case.</summary>
    public static string? Canonical(string name) => Known.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
}
