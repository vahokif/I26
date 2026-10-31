using System.Text.Json;

namespace I76.Editor.Geo;

/// <summary>One OpenStreetMap way with a highway tag: its tags, node ids and geometry (latitude, longitude).</summary>
public sealed record OsmWay(long Id, IReadOnlyDictionary<string, string> Tags, long[] Nodes, (double Lat, double Lon)[] Geometry)
{
    public string Highway => Tags.GetValueOrDefault("highway", "");
    public string? Tag(string key) => Tags.GetValueOrDefault(key);
    /// <summary>A bridge (any bridge= value but "no"): the road keeps a straight grade between its ends over whatever lies under it.</summary>
    public bool IsBridge => IsYes("bridge");
    /// <summary>A tunnel (a culvert is a pipe under the road, not a tunnel): the road is not cut into the ground over it.</summary>
    public bool IsTunnel => IsYes("tunnel") && Tag("tunnel") != "culvert";
    public bool Structure => IsBridge || IsTunnel;
    public bool IsYes(string key) => Tags.TryGetValue(key, out var v) && v is not ("no" or "" or "false" or "0");
}

/// <summary>What the Overpass JSON answer holds, and how the ways map onto the game's road kinds.</summary>
public static class OsmRoads
{
    /// <summary>The ways of an Overpass JSON answer (<c>out geom tags</c>).</summary>
    public static List<OsmWay> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var o = new List<OsmWay>();
        foreach (var e in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (e.GetProperty("type").GetString() != "way" || !e.TryGetProperty("geometry", out var g)) continue;
            var tags = new Dictionary<string, string>();
            if (e.TryGetProperty("tags", out var t)) foreach (var p in t.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? "";
            var geom = g.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object).Select(n => (n.GetProperty("lat").GetDouble(), n.GetProperty("lon").GetDouble())).ToArray();
            var nodes = e.TryGetProperty("nodes", out var ns) ? ns.EnumerateArray().Select(n => n.GetInt64()).ToArray() : [];
            if (geom.Length >= 2) o.Add(new OsmWay(e.GetProperty("id").GetInt64(), tags, nodes, geom));
        }
        return o;
    }

    /// <summary>The game's road kind (RoadKind: 0 asphalt, 1 dirt) and the importance (0 the biggest) of a way, or null when it is not for cars or not wanted.</summary>
    /// <param name="all">Residential streets, living streets, service roads and plain "road" as well as the major network.</param>
    public static (uint Kind, int Rank)? Classify(OsmWay w, bool all)
    {
        if (w.IsYes("area") || (w.Tag("access") is "no" or "private" && w.Highway == "service")) return null;
        string h = w.Highway;
        bool link = h.EndsWith("_link", StringComparison.Ordinal);
        string baseKind = link ? h[..^5] : h;
        int rank = baseKind switch
        {
            "motorway" => 0, "trunk" => 1, "primary" => 2, "secondary" => 3, "tertiary" => 4, "unclassified" => 6, "track" => 8,
            "residential" or "living_street" or "road" when all => 7,
            "service" when all && w.Tag("service") is not ("driveway" or "parking_aisle" or "emergency_access" or "drive-through") => 9,
            _ => -1,
        };
        if (rank < 0) return null;
        if (link && rank < 6) rank++;                  // a ramp is a little less than its road: the road is laid first and the ramp meets it
        // unclassified roads and tracks are DIRT; any other road is too when OSM says its surface is unpaved
        bool dirt = baseKind is "track" or "unclassified" || (!link && UnpavedSurface(w.Tag("surface")));
        return (dirt ? 1u : 0u, rank);
    }

    private static bool UnpavedSurface(string? s) => s is "unpaved" or "gravel" or "dirt" or "ground" or "sand" or "grass" or "compacted" or "fine_gravel" or "earth" or "mud" or "pebblestone";
}
