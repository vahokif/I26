using I76.Editor.Geo;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>The real-world map dialog's logic without a network: tile maths, box limits, cache keys, rate limiting and canned server answers.</summary>
public class GeoPickerTests
{
    [Fact]
    public void TileMathRoundTrips()
    {
        foreach (var (lat, lon, z) in new[] { (45.83, -108.34, 12), (-33.86, 151.21, 17), (0.0, 0.0, 3), (60.17, 24.94, 9) })
        {
            var (x, y) = Slippy.ToTile(lat, lon, z);
            var (la, lo) = Slippy.FromTile(x, y, z);
            Assert.Equal(lat, la, 6);
            Assert.Equal(lon, lo, 6);
        }
    }

    [Fact]
    public void KnownTile()
    {
        // the wiki's example: Sydney's lat -33.8688 lon 151.2093 at zoom 12 is tile 3768/2457... (fractional part inside)
        var (x, y) = Slippy.ToTile(-33.8688, 151.2093, 12);
        Assert.Equal(3768, (int)x);
        Assert.Equal(2457, (int)y);
        Assert.Equal(0.0, Slippy.ToTile(0, -180, 5).X, 9);
        Assert.Equal(1 << 5, Slippy.ToTile(0, 180, 5).X, 9);
        Assert.Equal(16.0, Slippy.ToTile(0, 0, 5).Y, 9);
    }

    [Fact]
    public void LatitudeIsClampedAndLongitudeWraps()
    {
        Assert.Equal(0.0, Slippy.ToTile(89.9, 0, 4).Y, 3);
        Assert.Equal(-170.0, Slippy.WrapLon(190), 9);
        Assert.Equal(179.0, Slippy.WrapLon(-181), 9);
    }

    [Fact]
    public void ZoomToFitStaysInRange()
    {
        Assert.Equal(GeoServices.MinZoom, Slippy.ZoomToFit(360, 100));
        Assert.Equal(GeoServices.MaxZoom, Slippy.ZoomToFit(0.0001, 1000));
        int z = Slippy.ZoomToFit(0.5, 800);
        Assert.True(0.5 / 360 * Math.Pow(2, z) * 256 <= 800);
        Assert.True(0.5 / 360 * Math.Pow(2, z + 1) * 256 > 800);
    }

    [Fact]
    public void BoxSizeAndLimits()
    {
        // the Billings box from the docs: about 12 km x 9 km is more than the map holds? check against GeoGrid
        var c = AreaCheck.Of(45.873, -108.407, 45.792, -108.275);
        Assert.InRange(c.WidthKm, 10.0, 10.5);
        Assert.InRange(c.HeightKm, 8.9, 9.1);
        Assert.Equal(GeoGrid.Plan(45.873, -108.407, 45.792, -108.275).Cols, c.Cols);
        Assert.Equal(GeoGrid.Plan(45.873, -108.407, 45.792, -108.275).Rows, c.Rows);
        Assert.True(c.Fits);
        Assert.Contains("km", c.Message);
    }

    [Fact]
    public void TooBigAndTooSmallAreRefused()
    {
        var big = AreaCheck.Of(46.0, -109.0, 45.0, -107.0);
        Assert.False(big.Fits);
        Assert.Contains("too big", big.Message);
        var tiny = AreaCheck.Of(45.0, -108.0, 45.001, -107.999);
        Assert.False(tiny.Fits);
        Assert.Contains("too small", tiny.Message);
        Assert.False(AreaCheck.Of(89, 0, 88, 1).Fits);
        Assert.False(AreaCheck.Of(double.NaN, 0, 1, 1).Fits);
    }

    [Fact]
    public void LargestFittingBoxFits()
    {
        // 16 x 16 = 256 tiles would not fit; 15 x 16 (9.6 x 10.2 km) does
        var c = AreaCheck.Of(0, 0, 10.2 / 110.57, 9.6 / 111.3);
        Assert.True(c.Fits);
        Assert.True(c.Tiles <= GeoGrid.MaxTiles);
    }

    [Fact]
    public void TileCachePathsAreKeyedByServerAndTile()
    {
        Assert.Equal("tile.openstreetmap.org/12/3768/2457.png", GeoServices.TileCachePath(GeoServices.DefaultTileUrl, 12, 3768, 2457));
        Assert.Equal("tiles.example.org/3/1/2.png", GeoServices.TileCachePath("https://tiles.example.org/{z}/{x}/{y}.png", 3, 1, 2));
        Assert.Equal("https://tile.openstreetmap.org/3/1/2.png", GeoServices.TileUrl(GeoServices.DefaultTileUrl, 3, 1, 2));
        Assert.DoesNotContain("..", GeoServices.TileCachePath("https://a/{z}/{x}/{y}", 1, 0, 0));
    }

    [Fact]
    public void CacheExpiry()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(GeoServices.TileFresh(now.AddDays(-6), now));
        Assert.False(GeoServices.TileFresh(now.AddDays(-8), now));
        Assert.False(GeoServices.TileFresh(now.AddDays(3), now));   // a file from the future is not trusted
    }

    [Fact]
    public void UserAgentNamesTheApp()
    {
        Assert.Equal("I26/1.2 (3e3e5b5) (+https://github.com/vahokif/i26)", GeoServices.UserAgent("1.2 (3e3e5b5)"));
        Assert.StartsWith("I26/dev ", GeoServices.UserAgent(""));
        Assert.DoesNotContain('\n', GeoServices.UserAgent("1\n2"));
        Assert.True(GeoServices.MaxConnections <= 2);
    }

    [Fact]
    public void RateLimiterAllowsOnePerInterval()
    {
        var rl = new RateLimiter(TimeSpan.FromSeconds(1));
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(rl.TryAcquire(t));
        Assert.False(rl.TryAcquire(t.AddMilliseconds(400)));
        Assert.Equal(TimeSpan.FromMilliseconds(600), rl.Wait(t.AddMilliseconds(400)));
        Assert.True(rl.TryAcquire(t.AddMilliseconds(1000)));
        Assert.False(rl.TryAcquire(t.AddMilliseconds(1500)));
        Assert.Equal(TimeSpan.Zero, rl.Wait(t.AddSeconds(5)));
    }

    private const string Canned = """
        [{"place_id":1,"lat":"45.7874","lon":"-108.5007","display_name":"Billings, Yellowstone County, Montana, United States","boundingbox":["45.6743","45.8888","-108.6806","-108.3677"]},
         {"lat":"12.5","lon":"-3.25","display_name":"Pointy"},
         {"lat":"x","lon":"1"},
         {"lat":"1","lon":"2","display_name":"Bad box","boundingbox":["a","b","c","d"]}]
        """;

    [Fact]
    public void NominatimAnswerParses()
    {
        var p = Nominatim.Parse(Canned);
        Assert.Equal(3, p.Count);
        Assert.Equal("Billings, Yellowstone County, Montana, United States", p[0].Name);
        Assert.Equal(45.7874, p[0].Lat, 6);
        Assert.Equal(-108.6806, p[0].West, 6);
        Assert.Equal(45.8888, p[0].North, 6);
        Assert.Equal((12.5, -3.25), (p[1].South, p[1].West));   // no box: the point
        Assert.Equal((1.0, 2.0), (p[2].South, p[2].East));      // a broken box: the point
        Assert.Empty(Nominatim.Parse("[]"));
        Assert.Throws<InvalidDataException>(() => Nominatim.Parse("""{"error":"x"}"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Nominatim.Parse("<html>"));
    }

    [Fact]
    public void SearchUrlEscapesAndAsksForFewResults()
    {
        var u = GeoServices.SearchUrl(" Billings, MT & co ");
        Assert.Contains("q=Billings%2C%20MT%20%26%20co", u);
        Assert.Contains("limit=6", u);
        Assert.StartsWith("https://nominatim.openstreetmap.org/search?", u);
    }

    [Fact]
    public void OverpassAnswersAreTold()
    {
        Assert.Equal(OverpassOutcome.Ok, OverpassReply.Classify(200, """{"elements":[]}"""));
        Assert.Equal(OverpassOutcome.TimedOut, OverpassReply.Classify(200, """{"elements":[],"remark":"runtime error: Query timed out in \"query\" at line 1 after 91 seconds."}"""));
        Assert.Equal(OverpassOutcome.Busy, OverpassReply.Classify(429, "Too Many Requests"));
        Assert.Equal(OverpassOutcome.Busy, OverpassReply.Classify(504, ""));
        Assert.Equal(OverpassOutcome.Busy, OverpassReply.Classify(502, "<html>502 Bad Gateway</html>"));
        Assert.Equal(OverpassOutcome.Busy, OverpassReply.Classify(200, "<html>The server is probably too busy to handle your request.</html>"));
        Assert.Equal(OverpassOutcome.Bad, OverpassReply.Classify(200, "<html>nope</html>"));
        Assert.Equal(OverpassOutcome.Bad, OverpassReply.Classify(400, "{}"));
        Assert.Equal(OverpassOutcome.Bad, OverpassReply.Classify(200, "{\"x\":1}"));
        Assert.Equal(OverpassOutcome.Bad, OverpassReply.Classify(200, "{ broken"));
    }

    [Fact]
    public void ErrorsAreExplainedPlainly()
    {
        Assert.Equal("Cancelled.", GeoMapJob.Explain(new OperationCanceledException()));
        Assert.Contains("offline", GeoMapJob.Explain(new IOException("x", new HttpRequestException("no route"))));
        Assert.Contains("too big", GeoMapJob.Explain(new ArgumentException("too big")));
    }

    [Fact]
    public void JobRefusesABadAreaOrNameBeforeAnyDownload()
    {
        var fetch = new GeoFetch(Path.Combine(Path.GetTempPath(), "i26-geopicker-test-" + Guid.NewGuid().ToString("N")), new HttpClient(new FailingHandler()));
        var big = new GeoMapRequest(46, -109, 45, -107, "big", "Big", GeoGame.I76, false, 9, Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => GeoMapJob.Run(big, fetch, null, _ => { }, CancellationToken.None));
        var badName = new GeoMapRequest(45.873, -108.407, 45.792, -108.275, "bad name!", "Billings", GeoGame.I76, false, 9, Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => GeoMapJob.Run(badName, fetch, null, _ => { }, CancellationToken.None));
        var cts = new CancellationTokenSource(); cts.Cancel();
        var ok = badName with { Stem = "billings" };
        Assert.Throws<OperationCanceledException>(() => GeoMapJob.Run(ok, fetch, null, _ => { }, cts.Token));
    }

    [Fact]
    public void A_road_server_that_never_answers_is_left_out_at_once()
    {
        // the first server takes the connection and never answers (as two public mirrors did): its status page is given a moment, then the next server's answer is used
        var handler = new SilentFirstHandler(new Uri(GeoFetch.OverpassServers[0]).Host);
        var fetch = new GeoFetch(Path.Combine(Path.GetTempPath(), "i26-geopicker-test-" + Guid.NewGuid().ToString("N")), new HttpClient(handler)) { StatusWait = TimeSpan.FromMilliseconds(200) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("""{"elements":[]}""", fetch.Overpass(GeoFetch.HighwayQuery(45.79, -108.41, 45.80, -108.40)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.DoesNotContain(handler.Asked, u => u.Host == new Uri(GeoFetch.OverpassServers[0]).Host && u.AbsolutePath.EndsWith("/interpreter"));
    }

    private sealed class SilentFirstHandler(string silentHost) : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request.RequestUri!);
            if (request.RequestUri!.Host == silentHost) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"elements":[]}""") };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("no network in tests");
    }
}
