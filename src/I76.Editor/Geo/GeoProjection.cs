namespace I76.Editor.Geo;

/// <summary>
/// A local equirectangular projection about a centre: east and north in metres from the centre, with the metres a degree of
/// latitude and of longitude spans there (WGS84 series). Over a 10 km map the scale error against the true Mercator/UTM is a few metres
/// at the edges, which a 5 m heightfield does not show.
/// </summary>
public sealed record GeoProjection(double Lat0, double Lon0)
{
    public double MetresPerDegLat { get; } = LatScale(Lat0);
    public double MetresPerDegLon { get; } = LonScale(Lat0);

    private static double LatScale(double lat) { double p = lat * Math.PI / 180; return 111132.92 - 559.82 * Math.Cos(2 * p) + 1.175 * Math.Cos(4 * p) - 0.0023 * Math.Cos(6 * p); }
    private static double LonScale(double lat) { double p = lat * Math.PI / 180; return 111412.84 * Math.Cos(p) - 93.5 * Math.Cos(3 * p) + 0.118 * Math.Cos(5 * p); }

    /// <summary>East and north of a point from the centre, metres.</summary>
    public (double East, double North) ToLocal(double lat, double lon) => ((lon - Lon0) * MetresPerDegLon, (lat - Lat0) * MetresPerDegLat);

    public (double Lat, double Lon) ToLatLon(double east, double north) => (Lat0 + north / MetresPerDegLat, Lon0 + east / MetresPerDegLon);
}
