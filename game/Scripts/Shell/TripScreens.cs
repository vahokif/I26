using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>A screen of the trip between missions: the Options Menu offers Save Bookmark and Exit Trip over it.</summary>
public abstract class TripScreen : ShellScreen
{
    /// <summary>A strict hit region {x0..x1, y0..y1}.</summary>
    protected static bool In(Vector2I m, int x0, int x1, int y0, int y1) => x0 < m.X && m.X < x1 && y0 < m.Y && m.Y < y1;

    /// <summary>The next mission in its designers' stock car (vppt%02d.vcf), which becomes the trip's car.</summary>
    protected void LaunchStock() => Shell.LaunchTrip(Shell.Trip.StockCar(Shell, $"vppt{Shell.Trip.Mission:00}.vcf"));

    /// <summary>SAVE BOOKMARK: overlay 1; its Cancel comes back here.</summary>
    protected void SaveBookmark() => Shell.Overlay(new SaveBookmarkScreen { Direct = true });
}

/// <summary>
/// 0xc01e, the post-mission router,
/// with the trip already advanced to the next mission: before T17 CHOOSE
/// YOUR VEHICLE; with "No Salv. Manag." the record-29 notice; before T13 YOU
/// ARE UNCONSCIOUS; otherwise field salvage, then the garage, whose DONE
/// launches.
/// </summary>
public sealed class TripRouterScreen : TripScreen
{
    public override void Enter() { }

    public override void Tick()
    {
        int next = Shell.Trip.Mission;
        if (next == 17) Shell.Go(new ChooseVehicleScreen());
        else if ((Shell.Settings.PlayOptions & 2) != 0) Shell.Go(new NoRepairScreen { Record = 29 });
        else if (next == 13) Shell.Go(new NoRepairScreen { Record = 31 });
        else
        {
            // no trip car (an old trip state): the mission's stock car, with its garage, rather than straight into the mission
            if (Shell.Trip.Inventory(Shell) is null) Shell.Trip.StockCar(Shell, $"vppt{next:00}.vcf");
            Shell.Go(new FieldSalvageScreen());
        }
    }
}

/// <summary>
/// 0xc015: record 29, the build-and-repair form stamped
/// "SALVAGE AND REPAIR IS NOT AVAILABLE WITH THE SALVAGE MANAGEMENT OPTION
/// OFF", or record 31, YOU ARE UNCONSCIOUS (before T13). Entering record 31
/// repairs the car and fits the 595ci V-10 (TripInventory.WakeUp, measured in
/// the real game); NEXT MISSION launches that car. Record 29 launches in the
/// mission's stock car (vppt13 has the 432ci: no V-10 with salvage management off).
/// </summary>
public sealed class NoRepairScreen : TripScreen
{
    public required int Record { get; init; }

    private bool Unconscious => Record == 31 && (Shell.Settings.PlayOptions & 2) == 0;

    public override void Enter()
    {
        // an old trip state with no car: the mission's stock car, which gets the same treatment
        if (Unconscious) (Shell.Trip.Inventory(Shell) ?? Shell.Trip.StockCar(Shell, $"vppt{Shell.Trip.Mission:00}.vcf")).WakeUp(Shell.Parts);
        Repaint();
    }

    public override void Repaint() => Frame.LoadBackground(Shell.Db, Record);

    /// <summary>NEXT MISSION: before T13 the car as entering left it; with salvage management off the stock car.</summary>
    private void NextMission()
    {
        if (Unconscious && Shell.Trip.Inventory(Shell) is { } inv) Shell.LaunchTrip(inv);
        else LaunchStock();
    }

    public override IReadOnlyList<PadRegion> PadRegions => [PadRegion.Of((182, 296, 273, 291)), PadRegion.Of((308, 447, 273, 291))];

    public override void Tick()
    {
        if (!Shell.Clicked) return;
        var m = Shell.Mouse;
        if (In(m, 182, 296, 273, 291)) { Shell.Sound(82); NextMission(); }
        else if (In(m, 308, 447, 273, 291)) { Shell.Sound(82); SaveBookmark(); }
    }
}

/// <summary>
/// 0xc00d CHOOSE YOUR VEHICLE, before T17: record 27, a
/// rendered garage; hovering a car draws its highlight from bank 0x40 and
/// leaving puts the picture back. A click launches with that car as its file has it,
/// weapons and all.
/// </summary>
public sealed class ChooseVehicleScreen : TripScreen
{
    private SpriteBank _bank = null!;
    private int _hot = -1;
    // hit rect (x0, x1, y0, y1), highlight sprite and where, the car
    private static readonly (int X0, int X1, int Y0, int Y1, int Sprite, int X, int Y, string Car)[] Cars =
    [
        (8, 198, 195, 294, 0, 8, 195, "vcgcourf.vcf"),
        (191, 386, 195, 305, 1, 191, 195, "vgoonf.vcf"),
        (304, 590, 160, 282, 2, 304, 160, "vstankf.vcf"),
        (23, 57, 60, 79, -1, 0, 0, "vhelif.vcf"),        // the helicopter in the sky: no highlight
    ];

    public override void Enter()
    {
        _bank = Shell.Db.Bank(0x40);
        Repaint();
    }

    public override void Repaint()
    {
        Frame.LoadBackground(Shell.Db, 27);
        _hot = -1;
    }

    public override bool HoverShows => true;
    public override IReadOnlyList<PadRegion> PadRegions => [.. Cars.Select(c => PadRegion.Of((c.X0, c.X1, c.Y0, c.Y1)))];

    public override void Tick()
    {
        var m = Shell.Mouse;
        int over = Array.FindIndex(Cars, c => In(m, c.X0, c.X1, c.Y0, c.Y1));
        if (over != _hot)
        {
            if (_hot >= 0 && Cars[_hot].Sprite >= 0 && _bank[Cars[_hot].Sprite] is { } old) Frame.Restore(Cars[_hot].X, Cars[_hot].Y, old.Width, old.Height);
            _hot = over;
            if (_hot >= 0 && Cars[_hot].Sprite >= 0) Frame.Blit(_bank[Cars[_hot].Sprite], Cars[_hot].X, Cars[_hot].Y);
        }
        if (Shell.Clicked && over >= 0)
        {
            // the shell clears the slot names, then loads the file into slot 0, so the car arrives with the
            // weapons its file gives it (the tank's turret is its cannon's gizmo: an emptied slot has none)
            Shell.LaunchTrip(Shell.Trip.StockCar(Shell, Cars[over].Car));
        }
    }
}
