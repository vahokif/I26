using System.Globalization;
using System.Text;
using System.Text.Json;

namespace I76.Editor.Geo;

/// <summary>
/// The online services the real-world map dialog uses and how it must behave towards them: the map tile server (the OSM tile usage policy:
/// a descriptive User-Agent, a cache, at most two connections, no bulk fetching), Nominatim (one request a second, search on Enter) and the
/// attribution. The tile URL is one setting (<see cref="TileUrlTemplate"/>, or the environment variable I26_GEO_TILE_URL) so it can change later.
/// </summary>
public static class GeoServices
{
    public const string DefaultTileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public const string NominatimUrl = "https://nominatim.openstreetmap.org/search";
    public const string Attribution = "© OpenStreetMap contributors";
    public const int MaxConnections = 2;
    public const int MinZoom = 3, MaxZoom = 17;
    /// <summary>A cached tile is used for this long before it is fetched again.</summary>
    public static readonly TimeSpan TileMaxAge = TimeSpan.FromDays(7);
    /// <summary>A failed tile is not asked for again for this long.</summary>
    public static readonly TimeSpan TileRetryAfter = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan NominatimInterval = TimeSpan.FromMilliseconds(1100);

    public static string TileUrlTemplate => Environment.GetEnvironmentVariable("I26_GEO_TILE_URL") is { Length: > 0 } u ? u : DefaultTileUrl;

    public static string TileUrl(string template, int z, int x, int y) =>
        template.Replace("{z}", z.ToString(CultureInfo.InvariantCulture)).Replace("{x}", x.ToString(CultureInfo.InvariantCulture)).Replace("{y}", y.ToString(CultureInfo.InvariantCulture));

    public static string UserAgent(string version) => $"I26/{(string.IsNullOrWhiteSpace(version) ? "dev" : version.Replace('\n', ' ').Trim())} (+https://github.com/vahokif/i26)";

    /// <summary>Where a tile is kept under the cache folder: the server's host (so a changed tile URL does not mix its tiles), then z/x/y.png.</summary>
    public static string TileCachePath(string template, int z, int x, int y)
    {
        string host = Uri.TryCreate(TileUrl(template, z, x, y), UriKind.Absolute, out var u) ? u.Host : "tiles";
        var safe = new string(host.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return string.Join('/', safe, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture) + ".png");
    }

    public static bool TileFresh(DateTime modifiedUtc, DateTime nowUtc) => nowUtc - modifiedUtc < TileMaxAge && modifiedUtc <= nowUtc.AddMinutes(5);

    public static string SearchUrl(string query) =>
        NominatimUrl + "?format=jsonv2&limit=6&q=" + Uri.EscapeDataString(query.Trim());
}

/// <summary>Allows an action at most once per interval: the next allowed moment after each use (Nominatim: one request a second).</summary>
public sealed class RateLimiter(TimeSpan interval)
{
    private DateTime _next = DateTime.MinValue;

    /// <summary>How long to wait from <paramref name="now"/> before the next use is allowed (zero when it is allowed now).</summary>
    public TimeSpan Wait(DateTime now) => _next > now ? _next - now : TimeSpan.Zero;

    /// <summary>Takes the use at <paramref name="now"/> if allowed; false (and no change) when too soon.</summary>
    public bool TryAcquire(DateTime now)
    {
        if (Wait(now) > TimeSpan.Zero) return false;
        _next = now + interval;
        return true;
    }
}

/// <summary>A Nominatim hit: a display name, a point and the box it covers (south, north, west, east).</summary>
public sealed record Place(string Name, double Lat, double Lon, double South, double North, double West, double East);

public static class Nominatim
{
    /// <summary>The places of a Nominatim <c>format=jsonv2</c> answer (anything unreadable is skipped, a non-array answer is an error).</summary>
    public static List<Place> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Nominatim did not answer with a list of places");
        var o = new List<Place>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !Num(e, "lat", out double lat) || !Num(e, "lon", out double lon)) continue;
            string name = e.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : $"{lat:0.####}, {lon:0.####}";
            double s = lat, nn = lat, w = lon, ee = lon;
            if (e.TryGetProperty("boundingbox", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() == 4)
            {
                var v = bb.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && double.TryParse(x.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToArray();
                if (v.All(double.IsFinite)) { s = v[0]; nn = v[1]; w = v[2]; ee = v[3]; }
            }
            o.Add(new Place(name, lat, lon, s, nn, w, ee));
        }
        return o;
    }

    private static bool Num(JsonElement e, string key, out double v)
    {
        v = 0;
        if (!e.TryGetProperty(key, out var p)) return false;
        return p.ValueKind == JsonValueKind.String ? double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) : p.TryGetDouble(out v);
    }
}

/// <summary>Tells an Overpass answer that holds the data from one that is a busy-server page or a query that ran out of time (the JSON then carries a "remark" and what it has is partial).</summary>
public enum OverpassOutcome { Ok, Busy, TimedOut, Bad }

public static class OverpassReply
{
    public static OverpassOutcome Classify(int httpStatus, string body)
    {
        if (httpStatus is 429 or 502 or 503 or 504) return OverpassOutcome.Busy;
        if (httpStatus is < 200 or >= 300) return OverpassOutcome.Bad;
        var t = body.TrimStart();
        if (t.Length == 0 || t[0] != '{') return t.Contains("too busy", StringComparison.OrdinalIgnoreCase) || t.Contains("rate_limited", StringComparison.OrdinalIgnoreCase) ? OverpassOutcome.Busy : OverpassOutcome.Bad;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("remark", out var r) && r.ValueKind == JsonValueKind.String)
            {
                var s = r.GetString() ?? "";
                if (s.Contains("timed out", StringComparison.OrdinalIgnoreCase) || s.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || s.Contains("runtime error", StringComparison.OrdinalIgnoreCase)) return OverpassOutcome.TimedOut;
            }
            return doc.RootElement.TryGetProperty("elements", out _) ? OverpassOutcome.Ok : OverpassOutcome.Bad;
        }
        catch (JsonException) { return OverpassOutcome.Bad; }
    }
}

/// <summary>Web Mercator tile maths and the size of a chosen box against the game's map limits.</summary>
public static class Slippy
{
    public const double MaxLat = 85.0511287798;

    /// <summary>Tile coordinates (fractional) of a point at a zoom: x east from the antimeridian, y south from the top.</summary>
    public static (double X, double Y) ToTile(double lat, double lon, int zoom)
    {
        lat = Math.Clamp(lat, -MaxLat, MaxLat);
        double n = Math.Pow(2, zoom), r = lat * Math.PI / 180;
        return ((lon + 180) / 360 * n, (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n);
    }

    public static (double Lat, double Lon) FromTile(double x, double y, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI;
        return (lat, x / n * 360 - 180);
    }

    /// <summary>The zoom whose tiles show <paramref name="degLon"/> degrees of longitude in <paramref name="pixels"/> pixels or fewer, within the picker's zoom range.</summary>
    public static int ZoomToFit(double degLon, double pixels, double tilePixels = 256)
    {
        for (int z = GeoServices.MaxZoom; z > GeoServices.MinZoom; z--)
            if (degLon / 360 * Math.Pow(2, z) * tilePixels <= pixels) return z;
        return GeoServices.MinZoom;
    }

    public static double WrapLon(double lon) { lon = (lon + 180) % 360; if (lon < 0) lon += 360; return lon - 180; }
}

/// <summary>What a chosen box comes to as a map: its size in km, the tiles it needs and whether the game can hold it.</summary>
public sealed record AreaCheck(double WidthKm, double HeightKm, int Cols, int Rows, bool Fits, string Message)
{
    public int Tiles => Cols * Rows;
    /// <summary>The smallest box a map is made from (a quarter of a 640 m tile on a side would be a toy).</summary>
    public const double MinMetres = 500;

    public static AreaCheck Of(double latA, double lonA, double latB, double lonB)
    {
        if (!(new[] { latA, lonA, latB, lonB }.All(double.IsFinite)) || Math.Abs(latA) > 85 || Math.Abs(latB) > 85 || Math.Abs(lonA) > 180 || Math.Abs(lonB) > 180)
            return new AreaCheck(0, 0, 0, 0, false, "The area has to lie between 85 degrees north and south.");
        var proj = new GeoProjection((latA + latB) / 2, (lonA + lonB) / 2);
        double w = Math.Abs(lonA - lonB) * proj.MetresPerDegLon, h = Math.Abs(latA - latB) * proj.MetresPerDegLat;
        int cols = Math.Max(1, (int)Math.Ceiling(w / GeoGrid.TileMetres - GeoGrid.Tolerance)), rows = Math.Max(1, (int)Math.Ceiling(h / GeoGrid.TileMetres - GeoGrid.Tolerance));
        string size = string.Create(CultureInfo.InvariantCulture, $"{w / 1000:0.0} x {h / 1000:0.0} km");
        if (w < MinMetres || h < MinMetres)
            return new AreaCheck(w / 1000, h / 1000, cols, rows, false, $"{size}: too small, draw at least {MinMetres / 1000:0.0} km on each side.");
        if (cols > 76 || rows > 79 || cols * rows > GeoGrid.MaxTiles)
            return new AreaCheck(w / 1000, h / 1000, cols, rows, false, $"{size} is {cols} x {rows} = {cols * rows} tiles: too big, a map holds {GeoGrid.MaxTiles} tiles of 640 m (about 9.6 x 10 km at most). Make the box smaller.");
        return new AreaCheck(w / 1000, h / 1000, cols, rows, true, $"{size}: {cols} x {rows} = {cols * rows} tiles of {GeoGrid.TileMetres:0} m (of {GeoGrid.MaxTiles} at most).");
    }
}
