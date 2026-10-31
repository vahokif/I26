namespace I76.Formats.Bwd2;

/// <summary>
/// Port-side data patches to stock vehicle definitions, applied when the .vdf is read (the install is never touched).
/// Today only one: the Nitro Pack's hidden F4D Phantom II (<c>vuphantm.vdf</c>, helicopter class 9) has no HLOC
/// hardpoints, so it flies unarmed in the original. I26 gives it four, so a variant made in the garage can carry guns.
/// The original game drops a WEPN whose hardpoint the .vdf lacks, so a variant saved with Phantom guns loads unarmed there
/// (docs/deviations.md, docs/hidden-content.md).
/// The .vdf is recognised by its VDFC display name (a Bwd2File does not know its file name) and patched only while it has
/// no HLOC of its own. Positions were measured on the model's geometry (nose z 9.7, wings y 0.31..0.72 out to x 5.8,
/// belly y 0.08), the rotations are the cars' own conventions (a dropper as on a car; an S gun rolled a
/// quarter turn about +z, the right one the other way round from the left). The fourth is a P (style 1, turret) mount under the
/// belly, rolled half a turn so the turret hangs below the fuselage like a ball turret; its yaw and pitch are measured in that
/// upside-down frame, so it covers the lower hemisphere.
/// </summary>
public static class VehiclePatches
{
    /// <summary>VDFC display name of <c>vuphantm.vdf</c>.</summary>
    public const string PhantomName = "F4D Phantom II";

    private static readonly float[] Right = [0, -1, 0, 1, 0, 0, 0, 0, 1];   // S gun on the right side, as vcvan CV1_GSF1
    private static readonly float[] Left = [0, 1, 0, -1, 0, 0, 0, 0, 1];    // and on the left, CV1_GSF2
    private static readonly float[] Under = [-1, 0, 0, 0, -1, 0, 0, 0, 1];  // a roof (P) mount rolled half a turn about +z: hangs upside down below the belly, forward still +z
    private static readonly float[] Back = [-1, 0, 0, 0, 1, 0, 0, 0, -1];   // a dropper faces back (cars' GDB1)

    /// <summary>The Phantom's added hardpoints: name, id, facing, style, transform (x right, y up, z forward).</summary>
    public static readonly IReadOnlyList<Hardpoint> PhantomHardpoints =
    [
        new("UP1_GSF1", 0, 1, 2, new Transform(Left, [-2.5f, 0.15f, 0.5f])),  // under the left wing, S
        new("UP1_GSF2", 1, 1, 2, new Transform(Right, [2.5f, 0.15f, 0.5f])),  // under the right wing, S
        new("UP1_GDB1", 2, 2, 4, new Transform(Back, [0f, 0.08f, -1.0f])),    // belly, D (dropper)
        new("UP1_GPF1", 3, 1, 1, new Transform(Under, [0f, 0.1f, 2.0f])),    // under the belly (underside y 0.08), P (turret mount, style 1): the turret hangs below it on its stalk
    ];

    /// <summary>Hardpoints the file's own HLOC records lack but the port adds.</summary>
    public static IReadOnlyList<Hardpoint> ExtraHardpoints(Bwd2File vdf) =>
        vdf.DisplayName() == PhantomName && !vdf.All("HLOC").Any() ? PhantomHardpoints : [];
}
