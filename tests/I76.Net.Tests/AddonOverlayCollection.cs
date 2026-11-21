using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// Tests that put saved cars into <c>AssetVfs.Addon</c>, the process-wide overlay of a player's own files. They run
/// alone, not beside the other collections: a test that lists or reads the install's cars (CarAcceptanceTests, a
/// host's bot choice in HostMigrationTests) would otherwise see a car another test has just added and read it after
/// it was taken away again (FileNotFoundException: vfdclyd6.vcf).
/// </summary>
[CollectionDefinition("AddonOverlay", DisableParallelization = true)]
public class AddonOverlayCollection;
