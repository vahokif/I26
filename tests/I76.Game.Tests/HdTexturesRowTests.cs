using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's "HD Textures" row: a world, car and cockpit texture pack is drawn until the player turns it off.</summary>
public class HdTexturesRowTests
{
    [Fact]
    public void ThePacksTexturesAreOnByDefault() => Assert.True(new ShellSettings().HdTextures);
}