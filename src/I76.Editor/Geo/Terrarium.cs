namespace I76.Editor.Geo;

/// <summary>Anything that gives a ground height in metres at a latitude and longitude.</summary>
public interface IElevation { double At(double lat, double lon); }

/// <summary>
/// A block of AWS "terrarium" terrain tiles (https://registry.opendata.aws/terrain-tiles/: height = R*256 + G + B/256 - 32768 metres,
/// 256 px tiles in the Web Mercator tile scheme) stitched into one image, sampled bicubically at any latitude and longitude it covers.
/// </summary>
public sealed class TerrariumMosaic : IElevation
{
    public const int TilePx = 256;
    public int Zoom { get; }
    public int X0 { get; }
    public int Y0 { get; }
    public int TilesWide { get; }
    public int TilesHigh { get; }
    private readonly float[] _h;
    private readonly int _w, _hgt;

    /// <param name="tileHeights">One 256 x 256 array of metres for each tile, by (x, y) in tile numbers: every tile of the block.</param>
    public TerrariumMosaic(int zoom, int x0, int y0, int tilesWide, int tilesHigh, Func<int, int, float[]> tileHeights)
    {
        Zoom = zoom; X0 = x0; Y0 = y0; TilesWide = tilesWide; TilesHigh = tilesHigh;
        _w = tilesWide * TilePx; _hgt = tilesHigh * TilePx;
        _h = new float[_w * _hgt];
        for (int ty = 0; ty < tilesHigh; ty++)
            for (int tx = 0; tx < tilesWide; tx++)
            {
                var t = tileHeights(x0 + tx, y0 + ty);
                if (t.Length != TilePx * TilePx) throw new ArgumentException("a terrain tile is 256 x 256");
                for (int y = 0; y < TilePx; y++) Array.Copy(t, y * TilePx, _h, (ty * TilePx + y) * _w + tx * TilePx, TilePx);
            }
    }

    /// <summary>Terrarium RGB to metres.</summary>
    public static float Decode(byte r, byte g, byte b) => r * 256f + g + b / 256f - 32768f;

    public static float[] DecodePng(byte[] png)
    {
        var (w, h, rgb) = PngReader.Decode(png);
        if (w != TilePx || h != TilePx) throw new InvalidDataException($"terrain tile of {w}x{h}");
        var o = new float[w * h];
        for (int i = 0; i < o.Length; i++) o[i] = Decode(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
        return o;
    }

    /// <summary>The tile coordinates (fractional) of a point at a zoom.</summary>
    public static (double X, double Y) TileCoord(double lat, double lon, int zoom)
    {
        double n = Math.Pow(2, zoom), r = lat * Math.PI / 180;
        return ((lon + 180) / 360 * n, (1 - Math.Asinh(Math.Tan(r)) / Math.PI) / 2 * n);
    }

    /// <summary>Metres a pixel spans on the ground at a latitude and zoom.</summary>
    public static double PixelMetres(double lat, int zoom) => 40075016.686 * Math.Cos(lat * Math.PI / 180) / (TilePx * Math.Pow(2, zoom));

    /// <summary>The tiles (x0, y0, wide, high) a latitude/longitude box needs at a zoom, with <paramref name="padPx"/> pixels spare all round (for the bicubic reach).</summary>
    public static (int X0, int Y0, int W, int H) TilesFor(double latSouth, double lonWest, double latNorth, double lonEast, int zoom, int padPx = 3)
    {
        var (xa, ya) = TileCoord(latNorth, lonWest, zoom);
        var (xb, yb) = TileCoord(latSouth, lonEast, zoom);
        double pad = (double)padPx / TilePx;
        int x0 = (int)Math.Floor(xa - pad), y0 = (int)Math.Floor(ya - pad), x1 = (int)Math.Floor(xb + pad), y1 = (int)Math.Floor(yb + pad);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>The height in metres at a point, Catmull-Rom bicubic over the 4 x 4 pixels around it (pixel centres are the samples); clamped at the block's edge.</summary>
    public double At(double lat, double lon)
    {
        var (tx, ty) = TileCoord(lat, lon, Zoom);
        double u = (tx - X0) * TilePx - 0.5, v = (ty - Y0) * TilePx - 0.5;
        int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v);
        double fu = u - iu, fv = v - iv;
        Span<double> wu = stackalloc double[4], wv = stackalloc double[4];
        Weights(fu, wu); Weights(fv, wv);
        double s = 0;
        for (int j = 0; j < 4; j++)
        {
            int y = Math.Clamp(iv - 1 + j, 0, _hgt - 1);
            double row = 0;
            for (int i = 0; i < 4; i++) row += wu[i] * _h[y * _w + Math.Clamp(iu - 1 + i, 0, _w - 1)];
            s += wv[j] * row;
        }
        return s;
    }

    private static void Weights(double t, Span<double> w)
    {
        double t2 = t * t, t3 = t2 * t;
        w[0] = (-t3 + 2 * t2 - t) / 2;
        w[1] = (3 * t3 - 5 * t2 + 2) / 2;
        w[2] = (-3 * t3 + 4 * t2 + t) / 2;
        w[3] = (t3 - t2) / 2;
    }
}
