using Godot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The geometry of a cloud's shadow: a point of the world is slid up the sun onto the sheet the sky draws its
/// clouds on, which is a sphere of the sky's radius centred the sky's drop below the eye (docs/cloud-shadows.md).
/// The complaint this pins is that clouds appeared to render through high mountains a long way off, and the cause
/// was geometric — the projection ran up the sun onto the height of the dome's crown instead, so ground above
/// that height sampled the sheet in the wrong direction, and ground past the dome's edge sampled a sheet the sky
/// has no room to show over it. Both are numbers, so they are checked here rather than hoped for on a capture.
/// The mission's own dome (6000 radius, 5400 drop) is used throughout: crown 600 over the eye, edge 2615 out.
/// The eye is at the origin, as it is in the shaders, so nothing here depends on where the map has its own.
/// </summary>
public class CloudShadowSheetTests
{
    private const float R = 6000f;
    private const float Drop = 5400f;

    /// <summary>A sun 30° up, in the +X direction, which is all the maths needs. Unit, as the shaders have it.</summary>
    private static Vector3 LowSun => new(0.8660254f, 0.5f, 0f);

    /// <summary>The sheet's centre, the same 5400 below the eye the sky puts its dome at.</summary>
    private static Vector3 Centre => new(0f, -Drop, 0f);

    /// <summary>
    /// Under the car nothing changes: the sun straight up from the eye meets the sheet at the crown, which is
    /// exactly where the old projection put it, so a shadow already falling under a cloud keeps falling there.
    /// That identity is what the rest of the change is allowed to move.
    /// </summary>
    [Fact]
    public void TheCrownIsWhereItWas()
    {
        var hit = I76.Game.World.CloudShadow.SheetHit(Vector3.Zero, Vector3.Up, R, Drop);
        Assert.NotNull(hit);
        Assert.Equal(R - Drop, hit.Value.Y, 1);     // 600: the crown of the dome, over the eye
        Assert.Equal(0f, hit.Value.X, 1);
        Assert.Equal(0f, hit.Value.Z, 1);
    }

    /// <summary>
    /// The sheet is a dome, not a ceiling: it stops being over the ground about 2.6 km from the eye. Anything
    /// beyond has its sun coming from the sky behind it, and a shade there is the artifact — the 3D horizon's
    /// ring stands 1 to 3 km up and from 6 km out, which is where the old maths shaded anyway, in the wrong
    /// direction as well.
    /// </summary>
    [Theory]
    [InlineData(0f, 2000f, true)]       // the plain the player drives on, near
    [InlineData(300f, 1800f, true)]     // a hill, still under the sheet and still lit by a cloud over it
    [InlineData(1170f, 6000f, false)]   // the nearest ring of the 3D horizon
    [InlineData(1200f, 12000f, false)]  // and its far mountains
    public void TheSheetIsOnlyOverTheNearGround(float high, float away, bool covered)
    {
        var hit = I76.Game.World.CloudShadow.SheetHit(new Vector3(away, high, 0f), LowSun, R, Drop);
        Assert.Equal(covered, hit.HasValue);
    }

    /// <summary>
    /// Ground taller than the crown's height is where the old projection went backwards: up the sun onto a plane
    /// 600 up from ground 700 up is a negative distance, so the sampled cloud was the one on the far side of the
    /// sun and a drifting shade crossed the high ground the wrong way round. The dome is above the ground it
    /// shades, always, and throws the sampled point toward the sun and never away from it.
    /// </summary>
    [Fact]
    public void HighGroundIsStillUnderTheSheet()
    {
        var at = new Vector3(0f, 300f, 0f);     // a rise 300 m above the eye, half a crown below the sheet
        var hit = I76.Game.World.CloudShadow.SheetHit(at, LowSun, R, Drop);
        Assert.NotNull(hit);
        Assert.True(hit.Value.Y > at.Y);        // above the ground it is shading, not below it
        var off = hit.Value - at;
        Assert.True(off.X * LowSun.X + off.Z * LowSun.Z > 0f);   // and thrown toward the sun, never away from it
    }

    /// <summary>
    /// The shade fades out as the sheet it samples drops toward the sky's own horizon, where the sky fades its
    /// clouds away — so the edge of the dome's footprint is not a circle of shaded ground with sun outside it.
    /// Overhead the fade is worth nothing; at the rim it is worth nothing; between the two it goes on counting.
    /// </summary>
    [Fact]
    public void TheShadeEndsRatherThanBeingCut()
    {
        Assert.Equal(1f, I76.Game.World.CloudShadow.SheetShows(new Vector3(0f, 600f, 0f)), 3);
        var rim = I76.Game.World.CloudShadow.SheetHit(new Vector3(2500f, 0f, 0f), LowSun, R, Drop);
        Assert.NotNull(rim);                                        // there is still a sheet over it
        Assert.True(I76.Game.World.CloudShadow.SheetShows(rim.Value) < 0.1f);   // and the sky has no cloud to show there
        var mid = I76.Game.World.CloudShadow.SheetHit(new Vector3(1000f, 0f, 0f), LowSun, R, Drop);
        Assert.True(I76.Game.World.CloudShadow.SheetShows(mid!.Value) > 0.3f);
    }

    /// <summary>
    /// A low sun used to be divided by, and a sunset's shadow ran off the map for want of a floor under that
    /// division. The chord of a sphere cannot run away: whatever the hour, the crossing is on the dome the sky
    /// draws, which is all the pattern is measured against.
    /// </summary>
    [Theory]
    [InlineData(0.03f)]     // two degrees up: the sun on the horizon line
    [InlineData(0.15f)]     // and the hour the strength smoothstep hands over at
    [InlineData(1f)]        // and noon, to show the bound is not doing the work at other hours
    public void AGrazingSunStaysOnTheDome(float up)
    {
        var sun = new Vector3(System.MathF.Sqrt(1f - up * up), up, 0f);
        var hit = I76.Game.World.CloudShadow.SheetHit(new Vector3(500f, 0f, 500f), sun, R, Drop);
        Assert.NotNull(hit);
        Assert.True(float.IsFinite(hit.Value.X) && float.IsFinite(hit.Value.Y));
        Assert.Equal(R, (hit.Value - Centre).Length(), 1);      // on the sheet, and not one the eye cannot see
    }

    /// <summary>
    /// Whatever the point and the hour, the crossing the shaders sample is above the ground they are shading and
    /// on the sky's own sheet: two invariants that mean no shade can fall where the sky has no cloud to account
    /// for it. The third case the shader has for returning 1 is the one <see cref="TheSheetIsOnlyOverTheNearGround"/>
    /// holds, out past the dome, and is skipped here rather than asserted twice.
    /// </summary>
    [Fact]
    public void TheCrossingIsAlwaysAboveTheGroundItShades()
    {
        for (var i = 0; i <= 12; i++)
        {
            var away = 200f * i;                        // up to 2.4 km, the widest the sheet reaches
            var up = 0.12f + 0.07f * i;                 // near sunrise to past noon
            var sun = new Vector3(System.MathF.Sqrt(1f - up * up), up, 0.2f * i);
            sun /= sun.Length();
            for (var high = 0f; high <= 500f; high += 250f)
            {
                var at = new Vector3(away, high, 0f);
                var hit = I76.Game.World.CloudShadow.SheetHit(at, sun, R, Drop);
                if (hit is null) continue;              // outside the sheet, which is its own tested case
                Assert.True(hit.Value.Y > at.Y, $"sheet under the ground at {away} m out, {high} m up");
                Assert.Equal(R, (hit.Value - Centre).Length(), 1);
                var off = hit.Value - at;
                Assert.True(off.X * sun.X + off.Z * sun.Z >= 0f, $"shade thrown away from the sun at {away} m out");
            }
        }
    }
}
