using System.Security.Cryptography;
using System.Text;

namespace I76.Editor.Geo;

/// <summary>
/// The downloads, cached under <c>~/.cache/i26-geo/</c> (never in the repo): AWS terrain tiles (terrarium PNG, Mapzen/AWS terrain tiles,
/// USGS 3DEP in the US) and one Overpass query for OpenStreetMap's highway ways (c) OpenStreetMap contributors, ODbL.
/// A cached file is not fetched again; <see cref="Downloaded"/> counts the bytes that were.
/// </summary>
public sealed class GeoFetch
{
    public const string TileUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
    public static readonly string[] OverpassServers =
    [
        "https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter", "https://overpass.private.coffee/api/interpreter",
    ];

    private readonly HttpClient _http;
    public string CacheDir { get; }
    public long Downloaded { get; private set; }
    public int TilesDownloaded { get; private set; }
    public Action<string>? Log { get; set; }
    /// <summary>Called as the terrain tiles come in: (done, of).</summary>
    public Action<int, int>? TileProgress { get; set; }
    /// <summary>Stops the downloads (an <see cref="OperationCanceledException"/> from whichever is running).</summary>
    public CancellationToken Cancel { get; set; }
    /// <summary>Rounds of waiting and trying every Overpass server (the dialog uses fewer than the command line).</summary>
    public int OverpassRounds { get; set; } = 5;
    /// <summary>Called when Overpass is busy and the fetch waits to try again: the round and the seconds.</summary>
    public Action<int, int>? OverpassWait { get; set; }
    /// <summary>Called each second while a road server works on the query: its host and the seconds so far (a big area takes a minute or more).</summary>
    public Action<string, int>? OverpassTrying { get; set; }
    /// <summary>How long a server's status page has to answer before the server is left out (a mirror that takes connections and never answers).</summary>
    public TimeSpan StatusWait { get; set; } = TimeSpan.FromSeconds(10);
    // servers left out for the rest of this fetch: no answer to the status page or to a query
    private readonly HashSet<string> _silent = [];

    public GeoFetch(string? cacheDir = null, HttpClient? http = null)
    {
        CacheDir = cacheDir ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "i26-geo");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(150) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("i26-geomap/1.0 (+https://github.com/vahokif)");
    }

    /// <summary>The block of terrain tiles for a latitude/longitude box, fetched (or read from the cache) and stitched.</summary>
    public TerrariumMosaic Terrain(double latSouth, double lonWest, double latNorth, double lonEast, int zoom = 14)
    {
        var (x0, y0, w, h) = TerrariumMosaic.TilesFor(latSouth, lonWest, latNorth, lonEast, zoom);
        Log?.Invoke($"terrain tiles: {w} x {h} at zoom {zoom}");
        var tiles = new Dictionary<(int, int), float[]>();
        int done = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                Cancel.ThrowIfCancellationRequested();
                tiles[(x, y)] = TerrariumMosaic.DecodePng(Tile(zoom, x, y));
                TileProgress?.Invoke(++done, w * h);
            }
        return new TerrariumMosaic(zoom, x0, y0, w, h, (x, y) => tiles[(x, y)]);
    }

    public byte[] Tile(int zoom, int x, int y)
    {
        string path = Path.Combine(CacheDir, "terrarium", zoom.ToString(), x.ToString(), y + ".png");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var data = Get(string.Format(TileUrl, zoom, x, y));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        Downloaded += data.Length; TilesDownloaded++;
        return data;
    }

    private byte[] Get(string url)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Cancel.ThrowIfCancellationRequested();
            try { return _http.GetByteArrayAsync(url, Cancel).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !Cancel.IsCancellationRequested) { last = e; Pause(TimeSpan.FromSeconds(1 << attempt)); }
        }
        throw new IOException($"could not fetch {url}: {last?.Message}", last);
    }

    /// <summary>The query for every highway way in the box, with its tags, node ids and geometry.</summary>
    public static string HighwayQuery(double latSouth, double lonWest, double latNorth, double lonEast) =>
        FormattableString.Invariant($"[out:json][timeout:90];way[\"highway\"]({latSouth:0.######},{lonWest:0.######},{latNorth:0.######},{lonEast:0.######});out geom tags;");

    private void Pause(TimeSpan t) { if (Cancel.WaitHandle.WaitOne(t)) Cancel.ThrowIfCancellationRequested(); }

    /// <summary>The Overpass answer to a query, from the cache or a server (the next one when a server is busy, a few rounds with a wait between).</summary>
    public string Overpass(string query)
    {
        string path = Path.Combine(CacheDir, "overpass", Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant()[..16] + ".json");
        if (File.Exists(path)) return File.ReadAllText(path);
        string last = "no answer";
        bool timedOut = false;
        for (int round = 0; round < OverpassRounds; round++)
        {
            foreach (var server in OverpassServers)
            {
                Cancel.ThrowIfCancellationRequested();
                string host = new Uri(server).Host;
                if (_silent.Contains(server) || !Answers(server)) { _silent.Add(server); continue; }
                try
                {
                    Log?.Invoke($"overpass: {server}");
                    var post = _http.PostAsync(server, new FormUrlEncodedContent([new("data", query)]), Cancel);
                    // the answer only starts when the query has run: tell the seconds so a slow server does not look like a hang
                    for (int secs = 0; !((IAsyncResult)post).AsyncWaitHandle.WaitOne(1000);) OverpassTrying?.Invoke(host, ++secs);
                    using var resp = post.GetAwaiter().GetResult();
                    Log?.Invoke($"overpass: HTTP {(int)resp.StatusCode}, reading the answer");
                    var body = resp.Content.ReadAsByteArrayAsync(Cancel).GetAwaiter().GetResult();
                    var text = Encoding.UTF8.GetString(body);
                    var outcome = OverpassReply.Classify((int)resp.StatusCode, text);
                    if (outcome == OverpassOutcome.Ok)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, body);
                        Downloaded += body.Length;
                        return text;
                    }
                    timedOut |= outcome == OverpassOutcome.TimedOut;
                    last = $"{new Uri(server).Host}: {(outcome == OverpassOutcome.TimedOut ? "the query ran out of time" : outcome == OverpassOutcome.Busy ? "busy" : "HTTP " + (int)resp.StatusCode)}";
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !Cancel.IsCancellationRequested)
                {
                    last = $"{host}: {(e is TaskCanceledException ? "no answer" : e.Message)}";
                    Log?.Invoke("overpass: " + last);
                    if (e is TaskCanceledException) _silent.Add(server);
                }
            }
            if (round + 1 < OverpassRounds) { OverpassWait?.Invoke(round + 1, 5 * (round + 1)); Pause(TimeSpan.FromSeconds(5 * (round + 1))); }
        }
        if (_silent.Count == OverpassServers.Length) last = "none of the road servers answered";
        throw new IOException(timedOut ? $"The road server ran out of time on this area (try a smaller one): {last}" : $"The road server (Overpass) did not answer, it may be busy: {last}");
    }

    /// <summary>
    /// Whether a road server answers at all: its status page (api/status), any HTTP answer within <see cref="StatusWait"/>. Mirrors that
    /// take the connection and then never answer would otherwise hold the query for the whole request timeout, every round.
    /// </summary>
    private bool Answers(string server)
    {
        string status = server.EndsWith("/interpreter", StringComparison.Ordinal) ? server[..^"interpreter".Length] + "status" : server;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        cts.CancelAfter(StatusWait);
        try
        {
            using var resp = _http.GetAsync(status, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException && !Cancel.IsCancellationRequested)
        {
            Log?.Invoke($"overpass: {new Uri(server).Host} does not answer, left out");
            return false;
        }
    }
}
