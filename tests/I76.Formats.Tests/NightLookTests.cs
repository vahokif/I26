using System.Numerics;
using I76.Formats.Geo;
using I76.Formats.Imaging;
using I76.Formats.Mission;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The 1997 look's night (and the parts of it that show by day): the horizon ring's height, the drop shadows' colour,
/// the faces the engine draws unlit, and the cars' night lights (M04 measured in the original game and on the
/// real Glide plugin).
/// </summary>
public class NightLookTests
{
    [Fact]
    public void TheHorizonRingSinksWithTheEyesHeight()
    {
        // The ring stands at -((6000 - 0.8F)/F)·camY; relative to the eye 10.2·camY lower at F = 600
        Assert.Equal(0f, Lighting.HorizonFoot(0f));
        Assert.Equal(-10.2f * 121.15f, Lighting.HorizonFoot(121.15f), 2);
        // M04's eye at 121 puts the whole 1170.6-tall panel below eye level (the terrain hides it); T06's at 5 leaves it standing
        Assert.True(Lighting.HorizonFoot(121.15f) + HorizonList.PanelHeight < 0);
        Assert.True(Lighting.HorizonFoot(4.95f) + HorizonList.PanelHeight > 1000);
    }

    [Theory]
    [InlineData(5f, 1000f, 300f)]
    [InlineData(121.15f, 2600f, 800f)]
    [InlineData(40f, 5700f, 1170f)]
    public void ARingStoodOnTheFootConeShowsThePanelsSkyline(float camY, float rho, float h)
    {
        // the cone from the eye through the panels' foot: a point on it at rho, plus a feature h·rho/6000 tall, is seen at the
        // elevation of a panel h tall at 6000 (the modern look's ring is placed so)
        float ring = MathF.Atan2(Lighting.HorizonCone(camY, rho) + h * rho / HorizonList.Radius - camY, rho);
        float panel = MathF.Atan2(h + Lighting.HorizonFoot(camY), HorizonList.Radius);
        Assert.Equal(panel, ring, 5);
        Assert.Equal(Lighting.HorizonFoot(camY) + camY, Lighting.HorizonCone(camY, HorizonList.Radius), 1);
    }

    [InstallTheory]
    [InlineData("t01_16.act", "t01.lum", 7)]   // 27 through the original game's γ 1.6, as T01's frame shows
    [InlineData("t17_16.act", "t17.lum", 3)]   // M01: 19 measured at γ 1.7
    [InlineData("t06.act", "t06.lum", 0)]      // M04, night: black
    public void TheShadowIsPaletteFeAtTheRampsFloor(string act, string lum, int grey)
    {
        var vfs = TestInstall.Vfs;
        var pal = new Palette(vfs.Read(act));
        var (r, g, b) = LumaTable.ShadowColour(pal, LumaTable.Build(pal, vfs.Read(lum)));
        Assert.Equal(grey, (int)MathF.Round(r * 255));
        Assert.Equal(grey, (int)MathF.Round(g * 255));
        Assert.InRange(b * 255, grey - 0.01f, grey + 1.01f);
    }

    [Theory]
    [InlineData(5, 1, 0, false)]   // the car body: lit (0x15)
    [InlineData(5, 5, 0, false)]   // keyed, row 5: lit (0x55)
    [InlineData(5, 3, 0, false)]   // 0x17: lit
    [InlineData(5, 7, 0, true)]    // 0x57: 0xff
    [InlineData(4, 5, 0, true)]    // the lamps: 0x54, 0xff
    [InlineData(4, 1, 0, true)]    // 0x14: 0xff
    [InlineData(4, 3, 0, false)]   // 0x16: lit
    [InlineData(4, 7, 0, true)]    // 0x56: 0xff
    [InlineData(0, 1, 0, true)]    // a row of 0 is taken as 4
    [InlineData(5, 5, 2, true)]    // translucent: 0x428fc0, 0xff
    [InlineData(1, 1, 0, false)]   // mode 0xe3
    public void FacesTheEngineDrawsUnlit(byte row, byte flags, byte translucent, bool unlit) =>
        Assert.Equal(unlit, FaceLight.Unlit(row, flags, translucent));

    [InstallFact]
    public void ThePiranhasLampsAreUnlitAndItsBodyLit()
    {
        var vfs = TestInstall.Vfs;
        Assert.All(GeoMesh.Parse(vfs.Read("PP11TLGT.GEO")).Faces, f => Assert.True(FaceLight.Unlit(f)));
        Assert.All(GeoMesh.Parse(vfs.Read("PP11HLGT.GEO")).Faces, f => Assert.True(FaceLight.Unlit(f)));
        Assert.All(GeoMesh.Parse(vfs.Read("PP11BDYB.GEO")).Faces, f => Assert.False(FaceLight.Unlit(f)));
    }

    [InstallFact]
    public void ThePoolIsTheNarrowFan()
    {
        // The headlight plugin names the object hdlt_msk: its shape, 20 long and 15.5 wide, as the real plugin shows it
        var g = GeoMesh.Parse(TestInstall.Vfs.Read("hdlt_msk.geo"));
        Assert.Single(g.Faces);
        Assert.Equal(20f, g.Vertices.Max(v => v.Z), 2);
        Assert.Equal(15.5f, g.Vertices.Max(v => v.X) - g.Vertices.Min(v => v.X), 0);
    }

    // a car at the origin looking along +Z with its light at the bumper, on flat ground at 0
    private static readonly Vector3 Light = new(0, 0, 2.29f), Nose = Vector3.UnitZ;

    [Fact]
    public void ASpotLightsWhatFacesTheCarInItsBeam()
    {
        // a wall 8 ahead facing back at the car: cone ((1 + 8/8.5)/2)^18 (the + 0.5 keeps it under 1 on the axis),
        // attenuation 1/(1 + 64/400), facing 1, x1.25
        float ahead = NightSpot.AtVertex(Light + new Vector3(0, 0, 8), -Vector3.UnitZ, Light, Nose);
        Assert.Equal(MathF.Pow((1 + 8 / 8.5f) / 2, 18) / 1.16f * 1.25f, ahead, 4);
        // the same wall turned away from the car: nothing (the turn is to the nose, not to the light)
        Assert.Equal(0f, NightSpot.AtVertex(Light + new Vector3(0, 0, 8), Vector3.UnitZ, Light, Nose));
        // off the beam by 45 degrees: the exponent 18 cone leaves next to nothing
        Assert.True(NightSpot.AtVertex(Light + new Vector3(8, 0, 8), -Vector3.UnitZ, Light, Nose) < 0.05f);
        // behind the light: next to nothing
        Assert.True(NightSpot.AtVertex(Light - new Vector3(0, 0, 3), -Vector3.UnitZ, Light, Nose) < 1e-9f);
        // the object picking: stronger ahead and near
        Assert.True(NightSpot.Strength(new(0, 0, 10), Light, Nose) > NightSpot.Strength(new(0, 0, 30), Light, Nose));
    }

    [Fact]
    public void ACarInAnothersBeamCastsItsShadowAwayFromIt()
    {
        // The lighting for a car 8 ahead of the light (centre 0.7 up): the light, raised 25 over the bumper, wins over
        // the moon (0.09375 x 1.5); the direction aims 15 x its cone below it, so the shadow falls forward and long
        var c = new Vector3(0, 0.7f, 10.29f);
        var l = NightSpot.ShadowLight(c, [(Light, Nose)], (_, _) => 0f, Lighting.Presets[0].Intensity);
        Assert.NotNull(l);
        float cone = MathF.Pow((8 / new Vector2(0.7f, 8).Length() + 1) / 2, 4.5f);
        var want = Vector3.Normalize(new Vector3(0, 25 - 0.7f - 15 * cone, -8));
        Assert.True(Vector3.Distance(want, l.Value) < 1e-4f);
        // the car carrying the light is behind its cone: no shadow from its own light, the moon casts it
        Assert.Null(NightSpot.ShadowLight(new Vector3(0, 0.7f, 0), [(Light, Nose)], (_, _) => 0f, Lighting.Presets[0].Intensity));
        // far from any light, too
        Assert.Null(NightSpot.ShadowLight(new Vector3(60, 0.7f, 0), [(Light, Nose)], (_, _) => 0f, Lighting.Presets[0].Intensity));
        // by day nothing beats 1.5 times the sun's intensity
        Assert.Null(NightSpot.ShadowLight(c, [(Light, Nose)], (_, _) => 0f, Lighting.Presets[5].Intensity));
    }
}
