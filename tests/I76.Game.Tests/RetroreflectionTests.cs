using System.Globalization;
using System.Text.RegularExpressions;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's road signs at night: retroreflective sheeting (Retroreflection, object.gdshaderinc's retro_light). Bright
/// to the driver whose lamps light them, out to a few hundred metres; next to nothing from another car's lamps; dimmer off the
/// beam's hot spot, dark outside the beam.
/// </summary>
public class RetroreflectionTests
{
    private static readonly float Edge = MathF.Cos(42f * MathF.PI / 180f);   // the modern look's headlamp cone (CarNode.HeadEdgeCos)

    // a sign straight down the beam at d metres, the eye `offset` metres from the lamp across the line of sight
    private static float Ahead(float d, float offset = 1f) => Retroreflection.Factor(d, MathF.Atan2(offset, d), 1f, 1f, Edge);

    [Fact]
    public void TheDriversOwnLampsLightASignFarDownTheRoad()
    {
        // the driver's eye a metre or so from the lamps: at 50, 150 and 300 m the sheeting returns most of its best
        Assert.InRange(Ahead(50), 0.7f, 0.8f);
        Assert.InRange(Ahead(150), 0.45f, 0.5f);
        Assert.InRange(Ahead(300), 0.3f, 0.34f);
        // far less falloff than a lamp's light on a diffuse face (1/d², 36 times from 50 to 300 m): not three times
        Assert.True(Ahead(300) / Ahead(50) > 1f / 3f);
        // and none past the reach
        Assert.Equal(0f, Ahead(600));
    }

    [Fact]
    public void AnotherCarsLampsHardlyBrightenItForUs()
    {
        // the lamp 10 m across from our eye (a car in the next lane, or oncoming) lights a sign 50 m off: its return goes back to it
        Assert.True(Ahead(50, 10f) < 0.01f * Ahead(50));
        // the chase camera, a few metres behind and above the lamps, still sees most of it far off
        Assert.True(Ahead(150, 4f) > 0.6f * Ahead(150));
    }

    [Fact]
    public void OffTheBeamsHotSpotItIsDimmerAndOutsideTheBeamDark()
    {
        float Off(float deg) => Retroreflection.Factor(150, 0.005f, 1f, MathF.Cos(deg * MathF.PI / 180f), Edge);
        Assert.True(Off(12) < 0.6f * Off(0));
        Assert.True(Off(20) < 0.2f * Off(0));
        Assert.Equal(0f, Off(45));
    }

    [Fact]
    public void TheEntranceAngleMattersOnlyMildlyAndTheBackNotAtAll()
    {
        float At(float deg) => Retroreflection.Factor(100, 0.005f, MathF.Cos(deg * MathF.PI / 180f), 1f, Edge);
        Assert.True(At(45) > 0.8f * At(0));
        Assert.True(At(70) > 0.5f * At(0));
        Assert.Equal(0f, Retroreflection.Factor(100, 0.005f, -0.5f, 1f, Edge));
    }

    [Fact]
    public void TheShaderUsesTheSameConstants()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", "object.gdshaderinc"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var shader = File.ReadAllText(Path.Combine(dir!, "game", "Shaders", "object.gdshaderinc"));
        float Const(string name) => float.Parse(Regex.Match(shader, $@"const float {name} = ([0-9.]+);").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(Retroreflection.Hot, Const("RETRO_HOT"));
        Assert.Equal(Retroreflection.Lobe, Const("RETRO_LOBE"));
        Assert.Equal(Retroreflection.Reach, Const("RETRO_REACH"));
        Assert.Equal(Retroreflection.Far, Const("RETRO_FAR"));
    }
}
