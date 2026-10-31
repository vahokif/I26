using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Game.Shell;

/// <summary>
/// Where a car file's custom paint colours are read from (I26's respray): the variant's sidecar (<c>&lt;variant&gt;.i26</c>, beside its
/// .vcf, <see cref="CarSidecar"/>) as the addon overlay serves it (the player's own, loaded by <see cref="MeleeData"/>) or the install's
/// addon\ folder has it (a community car's). A stock car has none: the factory paint.
/// </summary>
public static class CarPaints
{
    /// <summary>By the addon overlay alone (where the lists have put every variant's sidecar): for the network's hooks, which have no VFS at hand.</summary>
    public static CarPaint? Of(string? vcf) =>
        string.IsNullOrEmpty(vcf) || vcf == "*mission" ? null : CarSidecar.Read(AssetVfs.Addon.GetValueOrDefault(CarSidecar.NameOf(vcf)));

    public static CarPaint? Of(AssetVfs vfs, string? vcf) =>
        string.IsNullOrEmpty(vcf) || vcf == "*mission" ? null : CarSidecar.Read(vfs.TryRead(CarSidecar.NameOf(vcf)));
}
