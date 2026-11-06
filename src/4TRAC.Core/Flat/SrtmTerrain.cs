using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http;

namespace FourTrac.Core.Flat;

/// <summary>Anything that returns an ellipsoidal elevation (m) at a WGS84 position, NaN where there is no data.</summary>
public interface IDemSource
{
    double Sample(double latDeg, double lonDeg);
}

/// <summary>
/// SRTM 1-degree tiles (".hgt": big-endian 16-bit, row 0 = northernmost) as a
/// mosaic. Bilinear sampling inside a tile; a missing tile or a void sample is NaN.
/// </summary>
public sealed class SrtmMosaic : IDemSource
{
    private readonly Dictionary<(int Lat, int Lon), short[]> _tiles = new();
    private readonly Dictionary<(int Lat, int Lon), int> _sizes = new();

    public int TileCount => _tiles.Count;

    public void AddTile(int latFloor, int lonFloor, byte[] hgtBytes)
    {
        ArgumentNullException.ThrowIfNull(hgtBytes);
        int count = hgtBytes.Length / 2;
        int n = (int)Math.Round(Math.Sqrt(count));
        if (n * n != count || (n != 1201 && n != 3601))
            throw new InvalidDataException($"Tile N{latFloor} E{lonFloor}: unexpected size {hgtBytes.Length} bytes.");

        var samples = new short[count];
        for (int k = 0; k < count; k++)
            samples[k] = BinaryPrimitives.ReadInt16BigEndian(hgtBytes.AsSpan(2 * k, 2));
        _tiles[(latFloor, lonFloor)] = samples;
        _sizes[(latFloor, lonFloor)] = n;
    }

    public static string TileName(int latFloor, int lonFloor) =>
        $"{(latFloor >= 0 ? "N" : "S")}{Math.Abs(latFloor):00}{(lonFloor >= 0 ? "E" : "W")}{Math.Abs(lonFloor):000}";

    public double Sample(double latDeg, double lonDeg)
    {
        int la = (int)Math.Floor(latDeg), lo = (int)Math.Floor(lonDeg);
        if (!_tiles.TryGetValue((la, lo), out var s)) return double.NaN;
        int n = _sizes[(la, lo)];
        int step = n - 1;

        // Row 0 is latitude la+1; column 0 is longitude lo.
        double fy = (la + 1 - latDeg) * step;
        double fx = (lonDeg - lo) * step;
        int i0 = Math.Clamp((int)Math.Floor(fy), 0, step - 1);
        int j0 = Math.Clamp((int)Math.Floor(fx), 0, step - 1);
        double ty = fy - i0, tx = fx - j0;

        double v00 = At(s, n, i0, j0), v01 = At(s, n, i0, j0 + 1);
        double v10 = At(s, n, i0 + 1, j0), v11 = At(s, n, i0 + 1, j0 + 1);
        if (double.IsNaN(v00) || double.IsNaN(v01) || double.IsNaN(v10) || double.IsNaN(v11))
            return double.NaN;
        return (1 - ty) * ((1 - tx) * v00 + tx * v01) + ty * ((1 - tx) * v10 + tx * v11);
    }

    private static double At(short[] s, int n, int row, int col)
    {
        short v = s[row * n + col];
        return v == short.MinValue ? double.NaN : v; // -32768 marks a void
    }
}

/// <summary>
/// Automatic SRTM terrain: finds the 1-degree tiles covering a search radius
/// around the impact, downloads any not yet cached (AWS open data, Skadi
/// layout), and returns the mosaic. Downloads are atomic: a partial file is
/// never left in the cache.
/// </summary>
public static class SrtmCache
{
    public const string BaseUrl = "https://s3.amazonaws.com/elevation-tiles-prod/skadi";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "4TRAC", "dem");

    /// <summary>Lower-left corners (floor lat, floor lon) of every 1-degree tile touching the search box.</summary>
    public static IReadOnlyList<(int Lat, int Lon)> TilesFor(double latDeg, double lonDeg, double radiusM)
    {
        if (!(radiusM > 0)) throw new ArgumentOutOfRangeException(nameof(radiusM));
        double dLat = radiusM / 111_320.0;
        double dLon = radiusM / (111_320.0 * Math.Cos(latDeg * Math.PI / 180.0));
        var tiles = new List<(int, int)>();
        for (int la = (int)Math.Floor(latDeg - dLat); la <= (int)Math.Floor(latDeg + dLat); la++)
            for (int lo = (int)Math.Floor(lonDeg - dLon); lo <= (int)Math.Floor(lonDeg + dLon); lo++)
                tiles.Add((la, lo));
        return tiles;
    }

    public static SrtmMosaic Build(double latDeg, double lonDeg, double radiusM, string cacheDirectory, HttpClient? http = null)
    {
        Directory.CreateDirectory(cacheDirectory);
        var mosaic = new SrtmMosaic();
        using var ownClient = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(120) } : null;
        var client = http ?? ownClient!;

        foreach (var (la, lo) in TilesFor(latDeg, lonDeg, radiusM))
        {
            string name = SrtmMosaic.TileName(la, lo);
            string hgt = Path.Combine(cacheDirectory, name + ".hgt");
            if (!File.Exists(hgt))
                Download(client, name, la, hgt);
            mosaic.AddTile(la, lo, File.ReadAllBytes(hgt));
        }
        return mosaic;
    }

    private static void Download(HttpClient client, string name, int la, string targetHgt)
    {
        string folder = (la >= 0 ? "N" : "S") + Math.Abs(la).ToString("00");
        string url = $"{BaseUrl}/{folder}/{name}.hgt.gz";
        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new IOException($"SRTM tile {name} is not available ({(int)response.StatusCode}). Use a DEM file for this area.");

        string tmp = targetHgt + ".part";
        using (var net = response.Content.ReadAsStream())
        using (var gz = new GZipStream(net, CompressionMode.Decompress))
        using (var file = File.Create(tmp))
        {
            gz.CopyTo(file);
        }
        File.Move(tmp, targetHgt, overwrite: true);
    }
}
