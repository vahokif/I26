namespace I76.Sim.Missions;

public sealed partial class ScriptHost
{
    /// <summary>
    /// The car a seat drives is the player's own choice (docs/multiplayer.md "Co-op over the network"): the seat's car as the
    /// mission built it is made again in place from <paramref name="car"/> where it stands (the vehicle, its entity and its
    /// serial the same), as the car of a player the shell gave one (its body's sounds, the driver's handguns its own), for the
    /// first player's choice before the mission starts. A later player's comes in through <see cref="CoopJoin"/>, which builds
    /// from the seat's <see cref="CoopSeat.CarFile"/>.
    /// </summary>
    public void CoopRecar(int seat, string car)
    {
        if (Coop is not { } c || seat < 0 || seat >= c.Seats.Count) return;
        var s = c.Seats[seat];
        var v = s.Car;
        var (pos, rot) = (v.Pos, v.Rot);
        s.CarFile = car;
        // the build of a later seat (sounds, handguns): the first seat's own car had them fitted by the cockpit, a chosen one has not
        MeleeGame.Rebuild(v, c.Build(car, System.Math.Max(1, seat)), World.Rand);
        (v.Pos, v.Rot) = (pos, rot);
        I76.Sim.Vehicles.VehicleStep.PrimeWheels(v, World.Ground);   // the rebuilt car stands where the old one stood: its wheels from that ground
        Log($"co-op: seat {seat} drives {car}");
    }
}
