using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>DELETE in the Nitro VARIANT popup: only the player's own variants go, the rest renumber.</summary>
public class VariantDeleteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("i26-variants").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private void Make(params int[] ns) { foreach (var n in ns) File.WriteAllText(Path.Combine(_dir, $"vppirna{n}.vcf"), $"car{n}"); }
    private string Read(int n) => File.ReadAllText(Path.Combine(_dir, $"vppirna{n}.vcf"));
    private bool Has(int n) => File.Exists(Path.Combine(_dir, $"vppirna{n}.vcf"));

    [Fact]
    public void Stock_variants_are_refused_and_kept()
    {
        Make(1, 2, 3, 4);       // a stock file of the same name in the folder must not go either
        Assert.Equal(VariantFiles.Result.Stock, VariantFiles.Delete(_dir, "vppirna", 3, 4, 2));
        Assert.True(Has(1) && Has(2) && Has(3) && Has(4));
    }

    [Fact]
    public void A_player_variant_is_removed_and_later_ones_shift_down()
    {
        Make(4, 5, 6);
        Assert.Equal(VariantFiles.Result.Deleted, VariantFiles.Delete(_dir, "vppirna", 3, 6, 4));
        Assert.Equal("car5", Read(4));
        Assert.Equal("car6", Read(5));
        Assert.False(Has(6));
    }

    [Fact]
    public void The_last_variant_just_goes()
    {
        Make(4, 5);
        Assert.Equal(VariantFiles.Result.Deleted, VariantFiles.Delete(_dir, "vppirna", 3, 5, 5));
        Assert.True(Has(4)); Assert.False(Has(5));
    }

    [Fact]
    public void A_variant_that_is_not_a_file_here_is_not_ours_to_delete()
    {
        Make(4);                // 5 comes from somewhere else (the install's addon folder)
        Assert.Equal(VariantFiles.Result.NotOwned, VariantFiles.Delete(_dir, "vppirna", 3, 5, 5));
        Assert.Equal(VariantFiles.Result.NotOwned, VariantFiles.Delete(_dir, "vppirna", 3, 5, 4));
        Assert.True(Has(4));
    }

    [Theory]
    [InlineData(4, 5, 4)]   // 4 of 5 deleted: the one that slid in
    [InlineData(5, 5, 4)]   // the last: the new last
    [InlineData(4, 4, 3)]   // the only saved one: the last stock
    public void The_selection_moves_to_a_neighbour(int n, int count, int expected) => Assert.Equal(expected, VariantFiles.Neighbour(n, count));

    [Fact]
    public void Cars_using_later_variants_follow_the_renumbering()
    {
        Assert.Equal(3, VariantFiles.Remap(3, 4, 5));
        Assert.Equal(4, VariantFiles.Remap(5, 4, 5));
        Assert.Equal(4, VariantFiles.Remap(4, 4, 5));    // the deleted one takes the one that replaced it
        Assert.Equal(4, VariantFiles.Remap(5, 5, 4));    // the last: the new last
    }

    // The base game's VARIANT popup (I26's addition; the original has no delete) uses the same rules on i76car.def's built-in
    // table: three stock variants per car, the Bus one.

    [Fact]
    public void Base_game_car_keeps_its_three_stock_variants_and_deletes_a_saved_fourth()
    {
        foreach (var n in new[] { 1, 2, 3, 4, 5 }) File.WriteAllText(Path.Combine(_dir, $"valepre{n}.vcf"), $"car{n}");
        for (int n = 1; n <= 3; n++) Assert.Equal(VariantFiles.Result.Stock, VariantFiles.Delete(_dir, "valepre", 3, 5, n));
        Assert.Equal(VariantFiles.Result.Deleted, VariantFiles.Delete(_dir, "valepre", 3, 5, 4));
        Assert.Equal("car5", File.ReadAllText(Path.Combine(_dir, "valepre4.vcf")));
        Assert.False(File.Exists(Path.Combine(_dir, "valepre5.vcf")));
        Assert.True(File.Exists(Path.Combine(_dir, "valepre3.vcf")));
    }

    [Fact]
    public void Base_game_bus_has_one_stock_variant_so_its_second_is_deletable()
    {
        File.WriteAllText(Path.Combine(_dir, "vxbus1.vcf"), "a");
        File.WriteAllText(Path.Combine(_dir, "vxbus2.vcf"), "b");
        Assert.Equal(VariantFiles.Result.Stock, VariantFiles.Delete(_dir, "vxbus", 1, 2, 1));
        Assert.Equal(VariantFiles.Result.Deleted, VariantFiles.Delete(_dir, "vxbus", 1, 2, 2));
        Assert.False(File.Exists(Path.Combine(_dir, "vxbus2.vcf")));
    }
}
