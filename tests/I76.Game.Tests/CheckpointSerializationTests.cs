using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

public class CheckpointSerializationTests
{
    [Fact]
    public void RoundTrip_AllFieldsPopulated()
    {
        var cp = new Checkpoint(
            Mission: "t02",
            Snapshot: [1, 2, 3, 4, 5],
            Salvage: [10, 20, 30],
            MusicAt: 42.5,
            Chase: [1.5f, 2.5f, 3.5f],
            Cockpit: true,
            LastPreset: 2,
            Why: "nav1",
            Tick: 1234
        );

        byte[] bytes = cp.ToBytes();
        var restored = Checkpoint.FromBytes(bytes);

        Assert.NotNull(restored);
        Assert.Equal("t02", restored.Mission);
        Assert.NotNull(restored.Snapshot);
        Assert.Equal([1, 2, 3, 4, 5], restored.Snapshot);
        Assert.NotNull(restored.Salvage);
        Assert.Equal([10, 20, 30], restored.Salvage);
        Assert.Equal(42.5, restored.MusicAt);
        Assert.NotNull(restored.Chase);
        Assert.Equal([1.5f, 2.5f, 3.5f], restored.Chase);
        Assert.True(restored.Cockpit);
        Assert.Equal(2, restored.LastPreset);
        Assert.Equal("nav1", restored.Why);
        Assert.Equal(1234, restored.Tick);
    }

    [Fact]
    public void RoundTrip_OptionalFieldsNull()
    {
        var cp = new Checkpoint(
            Mission: "t01",
            Snapshot: [9, 8, 7],
            Salvage: null,
            MusicAt: null,
            Chase: null,
            Cockpit: false,
            LastPreset: 1,
            Why: "start",
            Tick: 50
        );

        byte[] bytes = cp.ToBytes();
        var restored = Checkpoint.FromBytes(bytes);

        Assert.NotNull(restored);
        Assert.Equal("t01", restored.Mission);
        Assert.Equal([9, 8, 7], restored.Snapshot);
        Assert.Null(restored.Salvage);
        Assert.Null(restored.MusicAt);
        Assert.Null(restored.Chase);
        Assert.False(restored.Cockpit);
        Assert.Equal(1, restored.LastPreset);
        Assert.Equal("start", restored.Why);
        Assert.Equal(50, restored.Tick);
    }

    [Fact]
    public void CorruptData_ReturnsNull()
    {
        Assert.Null(Checkpoint.FromBytes([]));
        Assert.Null(Checkpoint.FromBytes([1, 2, 3, 4]));

        // Corrupt magic
        byte[] valid = new Checkpoint("t01", [1], null, null, null, false, 0, "", 0).ToBytes();
        valid[0] ^= 0xff;
        Assert.Null(Checkpoint.FromBytes(valid));

        // Truncated data
        byte[] truncated = new byte[valid.Length - 5];
        System.Array.Copy(valid, truncated, truncated.Length);
        Assert.Null(Checkpoint.FromBytes(truncated));
    }
}
