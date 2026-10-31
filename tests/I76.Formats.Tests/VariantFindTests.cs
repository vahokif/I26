using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Reconfigure opens on the car the player is in: its file (the server's or the launch's) back to the list's model and
/// variant. It used to open on the model's first variant whatever the player drove.
/// </summary>
public class VariantFindTests
{
    private static readonly string[] Stems = ["vppirna", "vd", "vdillo", "vcgcour"];
    private static readonly int[] Counts = [3, 2, 4, 5];

    [Theory]
    [InlineData("vcgcour2.vcf", 3, 2)]
    [InlineData("VCGCOUR5.VCF", 3, 5)]          // any case, and a saved variant past the stock ones
    [InlineData("vdillo3.vcf", 2, 3)]            // the longest stem: not vd's "illo3"
    [InlineData("vd2.vcf", 1, 2)]
    [InlineData("addon/vppirna1.vcf", 0, 1)]
    public void A_car_file_is_its_model_and_variant(string file, int model, int variant) =>
        Assert.Equal((model, variant), VariantFiles.Find(Stems, Counts, file));

    [Theory]
    [InlineData("vcgcour6.vcf")]                 // past the model's variants
    [InlineData("vcgcour0.vcf")]
    [InlineData("vcgcour.vcf")]
    [InlineData("vcgcour2.vsf")]
    [InlineData("zzcar1.vcf")]
    [InlineData("")]
    [InlineData("vcgcour99999999999.vcf")]
    public void Anything_else_is_none(string file) => Assert.Null(VariantFiles.Find(Stems, Counts, file));
}
