using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using FourTrac.Core.Flat;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using FourTrac.Core.Solver;
using Xunit;

namespace FourTrac.Tests;

public class SrtmTests
{
    private const int N = 1201;

    /// <summary>A 1201x1201 tile with sample(row i, col j) = base + i + 2j; 'voidAt' writes a void.</summary>
    private static byte[] MakeTile(short baseValue, bool withVoid = false)
    {
        var bytes = new byte[N * N * 2];
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                short v = (short)(baseValue + i + 2 * j);
                if (withVoid && i == 10 && j == 10) v = short.MinValue;
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan((i * N + j) * 2, 2), v);
            }
        return bytes;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files;
        public List<string> Requests { get; } = new();

        public FakeHandler(Dictionary<string, byte[]> files) => _files = files;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (_files.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) gz.Write(raw);
        return ms.ToArray();
    }

    [Fact]
    public void TileName_UsesSkadiConvention()
    {
        Assert.Equal("N40W075", SrtmMosaic.TileName(40, -75));
        Assert.Equal("S01E010", SrtmMosaic.TileName(-1, 10));
    }

    [Fact]
    public void Mosaic_SamplesNodesExactly_AndReportsVoids()
    {
        var m = new SrtmMosaic();
        m.AddTile(40, -75, MakeTile(100));

        // Row 600 is latitude 40.5, column 600 is longitude -74.5: value 100 + 600 + 2*600.
        Assert.Equal(100 + 600 + 1200, m.Sample(40.5, -74.5), 6);
        // Half-way between columns 600 and 601 at row 600: average of the two node values.
        Assert.Equal(100 + 600 + 1200 + 1.0, m.Sample(40.5, -74.5 + 0.5 / 1200.0), 6);
        Assert.True(double.IsNaN(m.Sample(41.5, -74.5)), "No tile: NaN.");
    }

    [Fact]
    public void Mosaic_VoidSampleIsNaN()
    {
        var m = new SrtmMosaic();
        m.AddTile(40, -75, MakeTile(100, withVoid: true));
        // Node (row 10, col 10) is the void: latitude 41 - 10/1200, longitude -75 + 10/1200.
        Assert.True(double.IsNaN(m.Sample(41.0 - 10.0 / 1200.0, -75.0 + 10.0 / 1200.0)));
    }

    [Fact]
    public void TilesFor_CoversTheEnvelopeAroundTheImpact()
    {
        var tiles = SrtmCache.TilesFor(40.0, -75.0, 10_500.0);
        Assert.Equal(4, tiles.Count);
        Assert.Contains((39, -76), tiles);
        Assert.Contains((40, -75), tiles);
    }

    [Fact]
    public void Build_UsesCacheAndDownloadsMissingTilesOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"4trac_dem_{Guid.NewGuid():N}");
        var tiles = SrtmCache.TilesFor(40.0, -75.0, 10_500.0);
        var files = new Dictionary<string, byte[]>();
        foreach (var (la, lo) in tiles)
        {
            string name = SrtmMosaic.TileName(la, lo);
            files[$"{SrtmCache.BaseUrl}/N{la:00}/{name}.hgt.gz"] = Gzip(MakeTile(100));
        }

        using var http = new HttpClient(new FakeHandler(files));
        var first = SrtmCache.Build(40.0, -75.0, 10_500.0, dir, http);
        Assert.Equal(tiles.Count, first.TileCount);
        Assert.Equal(tiles.Count, Directory.GetFiles(dir, "*.hgt").Length);

        // Second build: everything cached, no network traffic.
        var handler = new FakeHandler(new Dictionary<string, byte[]>());
        using var offline = new HttpClient(handler);
        var second = SrtmCache.Build(40.0, -75.0, 10_500.0, dir, offline);
        Assert.Equal(tiles.Count, second.TileCount);
        Assert.Empty(handler.Requests);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Build_MissingTileFailsClearly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"4trac_dem_{Guid.NewGuid():N}");
        using var http = new HttpClient(new FakeHandler(new Dictionary<string, byte[]>()));
        var ex = Assert.Throws<IOException>(() => SrtmCache.Build(40.0, -75.0, 10_500.0, dir, http));
        Assert.Contains("is not available", ex.Message);
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir, "*.part") : Array.Empty<string>());
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Analysis_WithSrtmCache_RunsOnTerrain()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"4trac_dem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (var (la, lo) in SrtmCache.TilesFor(40.0, -75.0, 10_500.0))
            File.WriteAllBytes(Path.Combine(dir, SrtmMosaic.TileName(la, lo) + ".hgt"), MakeTile(100));

        var req = new FlatAnalysisRequest(
            ImpactLatDeg: 40.0, ImpactLonDeg: -75.0, ImpactElevationM: 100.0, OriginHeightM: 0.0,
            ExteriorAngleDeg: 30.0, ExteriorSigmaDeg: 0.5, AzimuthDeg: 270.0, AzimuthSigmaDeg: 0.5,
            MvMinMps: 2550 * UnitConversions.FpsToMps, MvMaxMps: 2700 * UnitConversions.FpsToMps,
            BcLbIn2: 0.462, BcSigmaFraction: 0.03, ReferenceStandard: "G1",
            TemperatureC: (59 - 32) * 5.0 / 9.0, PressurePa: 29.92 * 3386.39, RelativeHumidity: 0.5,
            WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 0.5,
            Coriolis: true, Spin: null, Hypotheses: 60, Seed: 11UL,
            UseSrtm: true, DemCacheDirectory: dir);

        var r = FlatAnalysis.Run(req, allowUncalibratedHypothesisCount: true);

        Assert.Null(r.Error);
        Assert.Contains(r.Notes, n => n.StartsWith("Origin surface: SRTM"));
        Directory.Delete(dir, recursive: true);
    }
}
