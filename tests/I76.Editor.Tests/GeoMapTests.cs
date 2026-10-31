using System.IO.Compression;
using System.Numerics;
using I76.Editor;
using I76.Editor.Geo;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>`i76 geomap` without the network: the projection, the resampling, the height packing and the road conversion, on small fixtures.</summary>
public class GeoMapTests
{
    // the user's Lockwood box
    private const double LatA = 45.87329448518404, LonA = -108.40718970827771, LatB = 45.79168744976674, LonB = -108.27518397548279;

    [Fact]
    public void TheProjectionHasWgs84Scales()
    {
        var p = new GeoProjection(45.83, -108.34);
        Assert.InRange(p.MetresPerDegLat, 111_100, 111_200);
        Assert.InRange(p.MetresPerDegLon, 77_600, 77_800);      // 111.32 km * cos(45.83 deg), on the ellipsoid
        var (e, n) = p.ToLocal(45.83 + 0.01, -108.34 + 0.02);
        Assert.Equal(p.MetresPerDegLon * 0.02, e, 3);
        Assert.Equal(p.MetresPerDegLat * 0.01, n, 3);
        var (lat, lon) = p.ToLatLon(e, n);
        Assert.Equal(45.84, lat, 9); Assert.Equal(-108.32, lon, 9);
    }

    [Fact]
    public void TheLockwoodBoxIs16By15TilesCentred()
    {
        var g = GeoGrid.Plan(LatA, LonA, LatB, LonB);
        Assert.Equal((16, 15), (g.Cols, g.Rows));
        Assert.Equal(240, g.Tiles);
        Assert.Null(g.CropNote);
        Assert.InRange(g.BoxWidth, 10_200, 10_300); Assert.InRange(g.BoxHeight, 9_000, 9_100);
        // the box's centre is the map's centre, and the corners come back
        var (cx, cz) = g.ToMap((LatA + LatB) / 2, (LonA + LonB) / 2);
        Assert.Equal(g.Width / 2, cx, 3); Assert.Equal(g.Height / 2, cz, 3);
        var (lat, lon) = g.ToLatLon(1234, 5678);
        var (x, z) = g.ToMap(lat, lon);
        Assert.Equal(1234, x, 4); Assert.Equal(5678, z, 4);
        // north is +z
        Assert.True(g.ToMap(LatA, LonA).Z > g.ToMap(LatB, LonA).Z);
        Assert.True(g.ToMap(LatA, LonB).X > g.ToMap(LatA, LonA).X);
        // placed on the grid with room: every tile fits the 80 x 80 cells
        Assert.InRange(g.Col0, 0, 80 - g.Cols); Assert.InRange(g.Row0, 0, 80 - g.Rows);
    }

    [Fact]
    public void ABoxOverTheTileLimitIsCroppedAndSaysSo()
    {
        var g = GeoGrid.Plan(45.9, -108.5, 45.8, -108.2);      // 23.4 x 11.1 km: 37 x 18 tiles
        Assert.True(g.Tiles <= GeoGrid.MaxTiles);
        Assert.NotNull(g.CropNote);
        Assert.InRange(g.Tiles, 200, GeoGrid.MaxTiles);
        var thin = GeoGrid.Plan(45.9, -108.5, 45.8999, -105.5);   // 235 km by 11 m: one row, but only as wide as the 80-cell grid holds
        Assert.True(thin.Cols <= 76 && thin.Rows >= 1);
    }

    [Fact]
    public void HeightsAreSubtractedWithAMarginAndPackedInTenths()
    {
        var p = PackedHeights.Pack([920.0, 1000.0, 1150.0]);
        Assert.False(p.Compressed);
        Assert.Equal(910.0, p.OffsetMetres);
        Assert.Equal([100, 900, 2400], p.Raw.Select(v => (int)v));            // (h - 910) / 0.1
        Assert.Equal(920.0, p.MinMetres); Assert.Equal(1150.0, p.MaxMetres);
    }

    [Fact]
    public void ReliefOver409MetresIsCompressedByOneFactor()
    {
        var p = PackedHeights.Pack([0.0, 500.0, 1000.0]);
        Assert.True(p.Compressed);
        Assert.InRange(p.Scale, 0.38, 0.41);
        Assert.All(p.Raw, v => Assert.InRange(v, 0, 4095));
        Assert.InRange(Math.Abs((p.Raw[1] - p.Raw[0]) - (p.Raw[2] - p.Raw[1])), 0, 1);      // evenly spaced input stays evenly spaced to a sample
    }

    [Fact]
    public void ATerrariumPixelDecodesToMetres()
    {
        Assert.Equal(0f, TerrariumMosaic.Decode(128, 0, 0));
        Assert.Equal(1000.5f, TerrariumMosaic.Decode(131, 232 + 0, 128), 3);   // 131*256 + 232 + 0.5 - 32768
        Assert.Equal(-32768f, TerrariumMosaic.Decode(0, 0, 0));
    }

    private static byte[] Png(int w, int h, Func<int, int, (byte, byte, byte)> px, bool sub)
    {
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add((byte)(sub ? 1 : 0));
            byte pr = 0, pg = 0, pb = 0;
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = px(x, y);
                if (sub) { raw.Add((byte)(r - pr)); raw.Add((byte)(g - pg)); raw.Add((byte)(b - pb)); (pr, pg, pb) = (r, g, b); }
                else { raw.Add(r); raw.Add(g); raw.Add(b); }
            }
        }
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw.ToArray());
        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        void Chunk(string tag, byte[] d)
        {
            o.Write([(byte)(d.Length >> 24), (byte)(d.Length >> 16), (byte)(d.Length >> 8), (byte)d.Length]);
            o.Write(System.Text.Encoding.ASCII.GetBytes(tag)); o.Write(d); o.Write([0, 0, 0, 0]);   // the CRC is not checked
        }
        Chunk("IHDR", [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 2, 0, 0, 0]);
        Chunk("IDAT", ms.ToArray()); Chunk("IEND", []);
        return o.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePngReaderGivesBackThePixels(bool sub)
    {
        var png = Png(256, 256, (x, y) => ((byte)(128 + (x >> 4)), (byte)(y & 255), (byte)((x * 7) & 255)), sub);
        var (w, h, rgb) = PngReader.Decode(png);
        Assert.Equal((256, 256), (w, h));
        Assert.Equal([(byte)(128 + 3), (byte)200, (byte)((50 * 7) & 255)], rgb.AsSpan((200 * 256 + 50) * 3, 3).ToArray());
        var m = TerrariumMosaic.DecodePng(png);
        Assert.Equal(TerrariumMosaic.Decode((byte)(128 + 3), 200, (byte)(350 & 255)), m[200 * 256 + 50]);
    }

    /// <summary>A mosaic of a plane: h = 2 metres per pixel east plus 3 per pixel south, from the tile coordinates.</summary>
    private static TerrariumMosaic Plane(int zoom, int x0, int y0) =>
        new(zoom, x0, y0, 2, 2, (tx, ty) =>
        {
            var o = new float[256 * 256];
            for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) o[y * 256 + x] = 2f * ((tx - x0) * 256 + x) + 3f * ((ty - y0) * 256 + y);
            return o;
        });

    [Fact]
    public void BicubicResamplingIsExactOnAPlaneAndMovesWithTheGround()
    {
        const int z = 14;
        var (tx, ty) = TerrariumMosaic.TileCoord(45.83, -108.34, z);
        var m = Plane(z, (int)tx, (int)ty);
        foreach (var (lat, lon) in new[] { (45.83, -108.34), (45.8301, -108.3399) })
        {
            var (fx, fy) = TerrariumMosaic.TileCoord(lat, lon, z);
            double px = (fx - m.X0) * 256 - 0.5, py = (fy - m.Y0) * 256 - 0.5;        // pixel centres are the samples
            Assert.Equal(2 * px + 3 * py, m.At(lat, lon), 3);
        }
        // a tenth of a degree north is higher up the picture: smaller y, so lower on this plane
        Assert.True(m.At(45.8305, -108.34) < m.At(45.83, -108.34));
        Assert.InRange(TerrariumMosaic.PixelMetres(45.83, 14), 6.5, 6.8);
    }

    [Fact]
    public void ABoxNeedsTheTilesAroundIt()
    {
        var (x0, y0, w, h) = TerrariumMosaic.TilesFor(LatB, LonA, LatA, LonB, 14);
        Assert.Equal((3258, 5837), (x0, y0));
        Assert.Equal((7, 6), (w, h));
    }

    [Fact]
    public void SamplingTheGridPutsEachSampleAtItsFiveMetres()
    {
        var g = GeoGrid.Plan(LatA, LonA, LatB, LonB);
        var h = GeoMapBuilder.SampleElevation(new Slope(g), g);
        Assert.Equal(g.Cols * 128 * g.Rows * 128, h.Length);
        int w = g.Cols * 128;
        Assert.Equal(0.1 * 5 * 100, h[7 * w + 100] - h[7 * w], 2);          // 0.1 m per metre east, sample 100 is 500 m on
        Assert.Equal(0.2 * 5 * 30, h[30 * w + 5] - h[5], 2);                // 0.2 m per metre north
    }

    /// <summary>Ground rising 0.1 m per metre east and 0.2 m per metre north of the map's south-west corner, 1000 m up.</summary>
    private sealed class Slope(GeoGrid g) : IElevation
    {
        public double At(double lat, double lon) { var (x, z) = g.ToMap(lat, lon); return 1000 + 0.1 * x + 0.2 * z; }
    }


    private static string Json(GeoGrid g, params (long Id, string Tags, (double X, double Z)[] Pts)[] ways)
    {
        string Node((double X, double Z) p) { var (lat, lon) = g.ToLatLon(p.X, p.Z); return FormattableString.Invariant($"{{\"lat\":{lat:0.0000000},\"lon\":{lon:0.0000000}}}"); }
        var els = ways.Select(w => FormattableString.Invariant(
            $"{{\"type\":\"way\",\"id\":{w.Id},\"nodes\":[{string.Join(",", w.Pts.Select((_, i) => w.Id * 100 + i))}],\"tags\":{{{w.Tags}}},\"geometry\":[{string.Join(",", w.Pts.Select(Node))}]}}"));
        return "{\"elements\":[" + string.Join(",", els) + "]}";
    }

    [Fact]
    public void WaysAreClassifiedIntoAsphaltDirtOrNothing()
    {
        var g = GeoGrid.Plan(LatA, LonA, LatB, LonB);
        var ways = OsmRoads.Parse(Json(g,
            (1, "\"highway\":\"motorway\"", [(500, 500), (1500, 500)]),
            (2, "\"highway\":\"motorway_link\"", [(500, 600), (1500, 600)]),
            (3, "\"highway\":\"tertiary\"", [(500, 700), (1500, 700)]),
            (4, "\"highway\":\"track\"", [(500, 800), (1500, 800)]),
            (5, "\"highway\":\"unclassified\"", [(500, 900), (1500, 900)]),
            (6, "\"highway\":\"residential\"", [(500, 1000), (1500, 1000)]),
            (7, "\"highway\":\"footway\"", [(500, 1100), (1500, 1100)]),
            (8, "\"highway\":\"secondary\",\"surface\":\"gravel\"", [(500, 1200), (1500, 1200)]),
            (9, "\"highway\":\"service\",\"service\":\"driveway\"", [(500, 1300), (1500, 1300)])));
        Assert.Equal(9, ways.Count);
        uint? K(int i, bool all) => OsmRoads.Classify(ways[i], all)?.Kind;
        Assert.Equal(0u, K(0, false)); Assert.Equal(0u, K(1, false)); Assert.Equal(0u, K(2, false));
        Assert.Equal(1u, K(3, false)); Assert.Equal(1u, K(4, false));
        Assert.Null(K(5, false)); Assert.Equal(0u, K(5, true));
        Assert.Null(K(6, true));            // not for cars
        Assert.Equal(1u, K(7, false));      // OSM says unpaved
        Assert.Null(K(8, true));            // a driveway
        Assert.True(OsmRoads.Classify(ways[0], false)!.Value.Rank < OsmRoads.Classify(ways[2], false)!.Value.Rank);
    }

    [Fact]
    public void RoadsAreResampledToFiveMetresWithNoFoldingBend()
    {
        var g = GeoGrid.Plan(LatA, LonA, LatB, LonB);
        var ways = OsmRoads.Parse(Json(g,
            (1, "\"highway\":\"motorway\"", [(500, 500), (1400, 530), (2200, 500), (3000, 900)]),
            (2, "\"highway\":\"tertiary\"", [(500, 2000), (520, 2000), (520, 2012), (900, 2012), (900, 2300)]),    // a tight zig-zag
            (3, "\"highway\":\"track\"", [(-300, 4000), (500, 4000), (900, 4000)]),                                  // starts off the map
            (4, "\"highway\":\"track\"", [(-300, -300), (-200, -200)])));                                           // wholly off it
        var st = new RoadImportStats();
        var roads = RoadImport.Convert(ways, g, false, st);
        Assert.Equal(4, st.Wanted);
        Assert.Equal(1, st.OutsideMap);
        Assert.Contains(roads, r => r.WayId == 1 && r.Kind == 0);
        Assert.Equal(0, roads[0].Rank);                                  // the motorway is first
        foreach (var r in roads)
        {
            Assert.True(r.Centres.Count >= RoadCurves.MinPoints);
            for (int i = 1; i < r.Centres.Count; i++) { float d = Vector2.Distance(r.Centres[i - 1], r.Centres[i]); Assert.True(d is >= 2f and <= 8f, $"way {r.WayId} step {i} of {r.Centres.Count}: {d}"); }
            Assert.True(RoadCurves.MinRadius(r.Centres) >= RoadPlanner.FoldRadius - 0.01f, $"way {r.WayId}: radius {RoadCurves.MinRadius(r.Centres)}");
            Assert.All(r.Centres, p => Assert.InRange(p.X, RoadImport.EdgeMargin - 0.01f, g.Width - RoadImport.EdgeMargin + 0.01f));
        }
        var track = roads.First(r => r.WayId == 3);
        Assert.True(track.Centres[0].X >= RoadImport.EdgeMargin - 0.01f);   // clipped at the margin
    }

    [Fact]
    public void ClippingCutsAPolylineAtTheRectangle()
    {
        var runs = RoadImport.Clip([new Vector2(-50, 10), new Vector2(50, 10), new Vector2(50, 200), new Vector2(-50, 200), new Vector2(-50, 10)], 0, 0, 100, 100);
        Assert.Single(runs);
        Assert.Equal(new Vector2(0, 10), runs[0][0]);
        Assert.Equal(new Vector2(50, 100), runs[0][^1]);
    }

    [Fact]
    public void SimplifyingKeepsTheShapeWithinTheTolerance()
    {
        var wiggle = Enumerable.Range(0, 50).Select(i => new Vector2(i * 4, (i % 2) * 0.3f)).ToList();
        var s = RoadImport.Simplify(wiggle, 1f);
        Assert.Equal(2, s.Count);
        var bent = new List<Vector2> { new(0, 0), new(50, 0), new(100, 50) };
        Assert.Equal(3, RoadImport.Simplify(bent, 1f).Count);
    }

    [Fact]
    public void AMapIsBuiltFromAHillSideAndTwoRoadsAndSavedAndReread()
    {
        // a small box: 2 x 2 tiles
        var g0 = GeoGrid.Plan(45.8300, -108.3400, 45.8300 + 1300 / 111_200.0, -108.3400 + 1300 / 77_600.0);
        Assert.Equal((2, 2), (g0.Cols, g0.Rows));
        var json = Json(g0,
            (1, "\"highway\":\"motorway\"", [(100, 640), (700, 640), (1180, 640)]),
            (2, "\"highway\":\"tertiary\",\"bridge\":\"yes\"", [(640, 100), (640, 400)]),                  // a bridge: ground kept as it is
            (3, "\"highway\":\"track\"", [(640, 700), (640, 1100)]));
        var elev = new Bump(g0);
        var opt = new GeoMapOptions(45.83, -108.34, 45.84, -108.33, "tstgeo", "Test Geo", Look: new MapLook("p17_16.act", "nk_6cld7.map", "tp011gr6.map", "p17_16.hzd", 9));
        var (proj, rep) = GeoMapBuilder.Build(opt, g0, elev, OsmRoads.Parse(json));

        Assert.Equal(4, proj.Terrain.TileCount);
        Assert.Equal(3, rep.Roads);
        Assert.Equal(1, rep.Structures);
        Assert.True(rep.Spawns >= 1 || rep.Spawns == 0);     // 2 x 2 tiles are too small for spawns clear of the edge (600 m)
        var doc = MapDocument.Parse(proj.Mission.ToBytes());
        Assert.Equal(3, doc.Roads.Count);
        Assert.Equal([0u, 0u, 1u], doc.Roads.Select(r => r.Kind));            // big first; the track last
        Assert.Equal("p17_16.act", doc.Look!.Palette);
        // the stock road style: edges 10 apart, points about 5 apart
        foreach (var r in doc.Roads)
        {
            Assert.All(r.Points, p => Assert.Equal(10f, Vector2.Distance(p.Left, p.Right), 2));
            for (int i = 1; i < r.Points.Count; i++) Assert.InRange(Vector2.Distance(r.Points[i - 1].Centre, r.Points[i].Centre), 3f, 7f);
        }
        // the ground is level across the motorway (no cross slope) and its grade is within the limit
        var tdoc = new TerrainDocument(proj.Terrain, "tstgeo");
        var motor = doc.Roads[0];
        float prev = 0;
        for (int i = 0; i < motor.Points.Count; i++)
        {
            var c = motor.Points[i].Centre;
            float hc = RoadTerrain.Ground(tdoc, c.X, c.Y);
            Assert.Equal(hc, RoadTerrain.Ground(tdoc, motor.Points[i].Left.X, motor.Points[i].Left.Y), 0.35f);
            Assert.Equal(hc, RoadTerrain.Ground(tdoc, motor.Points[i].Right.X, motor.Points[i].Right.Y), 0.35f);
            if (i > 0) Assert.True(Math.Abs(hc - prev) <= 0.11f * 5.5f, $"step {i}: {hc - prev}");
            prev = hc;
        }
        // a bridge keeps a straight grade between its ends over the ground, which is only filled up to the deck, never cut
        var bridge = doc.Roads[1];
        float Hm(Vector2 c) => (float)(RoadTerrain.Ground(tdoc, c.X, c.Y) / rep.Heights.Scale + rep.Heights.OffsetMetres);
        double Dem(Vector2 c) { var (la, lo) = g0.ToLatLon(c.X - g0.Col0 * 640, c.Y - g0.Row0 * 640); return elev.At(la, lo); }
        var a0 = bridge.Points[0].Centre; var a1 = bridge.Points[^1].Centre; var am = bridge.Points[bridge.Points.Count / 2].Centre;
        Assert.Equal((Hm(a0) + Hm(a1)) / 2, Hm(am), 0.5);                          // straight
        Assert.True(Hm(am) >= Dem(am) - 0.2);                                       // on or above the DEM
        Assert.Equal(Dem(a0), Hm(a0), 0.3);                                         // an end meets the ground as it was
        Assert.Equal(1, rep.Bridges); Assert.Equal(0, rep.Tunnels);
        // the ground under a road carries its surface nibble: asphalt 4, dirt 6
        int Nib(Vector2 p) { Assert.True(proj.Terrain.Sample((int)Math.Round(p.X / 5), (int)Math.Round(p.Y / 5)) is { } s); return proj.Terrain.Sample((int)Math.Round(p.X / 5), (int)Math.Round(p.Y / 5))!.Value >> 12; }
        Assert.Equal(4, Nib(motor.Points[5].Centre));
        Assert.Equal(6, Nib(doc.Roads[2].Points[5].Centre));
    }

    /// <summary>A hill: 1000 m, plus a 40 m bump 300 m wide in the middle, plus 0.02 m/m north.</summary>
    private sealed class Bump(GeoGrid g) : IElevation
    {
        public double At(double lat, double lon)
        {
            var (x, z) = g.ToMap(lat, lon);
            double d2 = (x - g.Width / 2) * (x - g.Width / 2) + (z - g.Height / 2) * (z - g.Height / 2);
            return 1000 + 0.02 * z + 40 * Math.Exp(-d2 / (2 * 150 * 150));
        }
    }
}
