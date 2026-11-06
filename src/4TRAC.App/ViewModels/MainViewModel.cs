using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using FourTrac.Core.Flat;
using FourTrac.Core.Geo;
using FourTrac.Core.Stats;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Wpf;
using NetTopologySuite.Geometries;

namespace FourTrac.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const double EarthRadiusM = 6378137.0;
    private const string ResultPrefix = "Result";

    private readonly MapControl _mapControl;
    private FlatAnalysisRequest? _lastRequest;
    private FlatAnalysisResult? _lastResult;

    // Scene
    private string _impactLat = "40.0";
    private string _impactLon = "-75.0";
    private string _impactElevation = "100";
    private string _originHeight = "0";
    private string _incidenceAngle = "30";
    private string _incidenceAngleSigma = "0.5";
    private string _azimuth = "270";
    private string _azimuthSigma = "0.5";

    // Bullet
    private string _ballisticCoefficient = "0.462";
    private string _bcSigmaPercent = "3";
    private string _referenceStandard = "G1";
    private string _diameterIn = "0.308";
    private string _massGr = "168";
    private string _twistIn = "10";
    private string _lengthIn = "1.22";
    private bool _spinDriftOn = true;
    private bool _rightHandTwist = true;

    // Muzzle
    private string _mvMinFps = "2550";
    private string _mvMaxFps = "2700";

    // Atmosphere
    private string _temperatureF = "59";
    private string _pressureInHg = "29.92";
    private string _relativeHumidity = "50";

    // Wind
    private string _windSpeedMph = "0";
    private string _windFromDeg = "0";
    private string _windSigma = "0.5";

    // Run
    private string _hypotheses = "500";
    private string _seed = "20261029";
    private bool _coriolisOn = true;

    private string _resultsText =
        "Example inputs are loaded. Replace them with the case data before solving.\n" +
        "Model: flat-earth point mass, 10 km validated envelope, gravity 9.80665 m/s^2.\n" +
        "Press Solve to find the origin regions.";

    public MainViewModel(MapControl mapControl)
    {
        _mapControl = mapControl;
        InitializeMap();
        InitializeProfiles();
        InitializeValidation();
        SolveCommand = new RelayCommand(async _ => await SolveAsync(), _ => !IsBusy && !HasErrors);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ExportPngCommand = new RelayCommand(_ => ExportPng());
        ExportGeoJsonCommand = new RelayCommand(_ => ExportGeoJson());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv());
        ExportReportCommand = new RelayCommand(_ => ExportReport());
        BrowseDemCommand = new RelayCommand(_ => BrowseDem());
    }

    public ICommand ExportReportCommand { get; }
    public ICommand BrowseDemCommand { get; }

    private string _demPath = "";
    public string DemPath { get => _demPath; set => SetProperty(ref _demPath, value); }

    private bool _useSrtm;
    public bool UseSrtm { get => _useSrtm; set => SetProperty(ref _useSrtm, value); }

    private void BrowseDem()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select DEM (ESRI ASCII grid, WGS84 degrees)",
            Filter = "ESRI ASCII grid (*.asc)|*.asc|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            DemPath = dialog.FileName;
    }

    public ICommand SolveCommand { get; }
    public ICommand ExportPngCommand { get; }
    public ICommand ExportGeoJsonCommand { get; }
    public ICommand ExportCsvCommand { get; }

    public string ImpactLat { get => _impactLat; set => SetProperty(ref _impactLat, value); }
    public string ImpactLon { get => _impactLon; set => SetProperty(ref _impactLon, value); }
    public string ImpactElevation { get => _impactElevation; set => SetProperty(ref _impactElevation, value); }
    public string OriginHeight { get => _originHeight; set => SetProperty(ref _originHeight, value); }
    public string IncidenceAngle { get => _incidenceAngle; set => SetProperty(ref _incidenceAngle, value); }
    public string IncidenceAngleSigma { get => _incidenceAngleSigma; set => SetProperty(ref _incidenceAngleSigma, value); }
    public string Azimuth { get => _azimuth; set => SetProperty(ref _azimuth, value); }
    public string AzimuthSigma { get => _azimuthSigma; set => SetProperty(ref _azimuthSigma, value); }
    public string BallisticCoefficient { get => _ballisticCoefficient; set => SetProperty(ref _ballisticCoefficient, value); }
    public string BcSigmaPercent { get => _bcSigmaPercent; set => SetProperty(ref _bcSigmaPercent, value); }
    public string ReferenceStandard { get => _referenceStandard; set => SetProperty(ref _referenceStandard, value); }
    public string DiameterIn { get => _diameterIn; set => SetProperty(ref _diameterIn, value); }
    public string MassGr { get => _massGr; set => SetProperty(ref _massGr, value); }
    public string TwistIn { get => _twistIn; set => SetProperty(ref _twistIn, value); }
    public string LengthIn { get => _lengthIn; set => SetProperty(ref _lengthIn, value); }
    public bool SpinDriftOn { get => _spinDriftOn; set => SetProperty(ref _spinDriftOn, value); }
    public bool RightHandTwist { get => _rightHandTwist; set => SetProperty(ref _rightHandTwist, value); }
    public string MvMinFps { get => _mvMinFps; set => SetProperty(ref _mvMinFps, value); }
    public string MvMaxFps { get => _mvMaxFps; set => SetProperty(ref _mvMaxFps, value); }
    public string TemperatureF { get => _temperatureF; set => SetProperty(ref _temperatureF, value); }
    public string PressureInHg { get => _pressureInHg; set => SetProperty(ref _pressureInHg, value); }
    public string RelativeHumidity { get => _relativeHumidity; set => SetProperty(ref _relativeHumidity, value); }
    public string WindSpeedMph { get => _windSpeedMph; set => SetProperty(ref _windSpeedMph, value); }
    public string WindFromDeg { get => _windFromDeg; set => SetProperty(ref _windFromDeg, value); }
    public string WindSigma { get => _windSigma; set => SetProperty(ref _windSigma, value); }
    public string Hypotheses { get => _hypotheses; set => SetProperty(ref _hypotheses, value); }
    public string Seed { get => _seed; set => SetProperty(ref _seed, value); }
    public bool CoriolisOn { get => _coriolisOn; set => SetProperty(ref _coriolisOn, value); }
    public string ResultsText { get => _resultsText; set => SetProperty(ref _resultsText, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool IsIdle => !_isBusy;

    private string _warningText = "";
    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
                OnPropertyChanged(nameof(HasWarning));
        }
    }
    public bool HasWarning => !string.IsNullOrWhiteSpace(_warningText);

    private string _headlineText = "No result yet. Enter the case data and press Solve.";
    public string HeadlineText { get => _headlineText; private set => SetProperty(ref _headlineText, value); }

    private static string BuildHeadline(FlatAnalysisResult r) =>
        r.Error is not null
            ? "No region reported."
            : "The regions are the result. The most likely origin is only an annotation inside them.";

    private IReadOnlyList<ResultTile> _tiles = Array.Empty<ResultTile>();
    public IReadOnlyList<ResultTile> Tiles { get => _tiles; private set => SetProperty(ref _tiles, value); }

    private IReadOnlyList<string> _noteLines = Array.Empty<string>();
    public IReadOnlyList<string> NoteLines { get => _noteLines; private set => SetProperty(ref _noteLines, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    private bool _progressIndeterminate = true;
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => SetProperty(ref _progressIndeterminate, value); }

    private CancellationTokenSource? _cts;
    public ICommand CancelCommand { get; private set; } = null!;

    /// <summary>Key numbers as tiles: regions first, then spread, then run counts.</summary>
    private static IReadOnlyList<ResultTile> BuildTiles(FlatAnalysisResult r)
    {
        if (r.Error is not null)
            return Array.Empty<ResultTile>();

        var tiles = new List<ResultTile>();
        foreach (var b in r.Bands.OrderBy(b => b.Fraction))
            tiles.Add(new ResultTile($"{b.Fraction * 100:F0} % region", $"{b.AreaM2:N0} m\u00B2"));
        tiles.Add(new ResultTile("Origin range, P5\u2013P95", $"{r.RangeP05M:N0}\u2013{r.RangeP95M:N0} m"));
        tiles.Add(new ResultTile("Implied muzzle velocity, P5\u2013P95",
            $"{r.MvP05Mps * UnitConversions.MpsToFps:N0}\u2013{r.MvP95Mps * UnitConversions.MpsToFps:N0} fps"));
        if (r.Cloud is not null)
            tiles.Add(new ResultTile("Hypotheses used", $"{r.Cloud.Points.Count} of {r.Cloud.Requested}"));
        tiles.Add(new ResultTile("Most likely origin (annotation)", $"{r.ModeRangeM:N0} m at {r.ModeBearingDeg:F0}\u00B0"));
        return tiles;
    }

    /// <summary>Pulls the notes that change how far a result can be trusted into one banner.</summary>
    private static string BuildWarnings(FlatAnalysisResult r)
    {
        var lines = new List<string>();
        if (r.Error is not null)
            lines.Add($"No region was reported: {r.Error}");

        const string marker = "THIS RUN IS OUTSIDE THE CALIBRATED CONDITIONS";
        if (r.Notes.Count > 0)
        {
            int i = r.Notes[0].IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0)
                lines.Add(r.Notes[0][i..]);
        }

        foreach (var n in r.Notes)
        {
            if (n.StartsWith("WARNING", StringComparison.Ordinal)
                || n.StartsWith("LOW-MACH", StringComparison.Ordinal)
                || n.Contains("at or below 0 deg", StringComparison.Ordinal))
                lines.Add(n);
        }
        return string.Join(Environment.NewLine, lines.Select(l => "• " + l));
    }

    private void InitializeMap()
    {
        var map = new Map();
        map.Layers.Add(OpenStreetMap.CreateTileLayer());
        _mapControl.Map = map;
        _mapControl.Map.Navigator.CenterOnAndZoomTo(ToMercator(-75.0, 40.0), _mapControl.Map.Navigator.Resolutions[5]);
    }

    private async Task SolveAsync()
    {
        if (IsBusy) return;

        FlatAnalysisRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (Exception ex)
        {
            WarningText = $"• Input error: {ex.Message}";
            HeadlineText = "Fix the input and press Solve again.";
            ResultsText = $"Input error: {ex.Message}";
            return;
        }

        // A new solve invalidates the previous result: clear it from the map and from
        // the export state so a cancelled or failed run cannot leave a stale result behind.
        _lastRequest = null;
        _lastResult = null;
        ClearResultLayers();
        WarningText = "";
        Tiles = Array.Empty<ResultTile>();
        NoteLines = Array.Empty<string>();
        ProgressValue = 0;
        ProgressIndeterminate = true;
        HeadlineText = $"Preparing {request.Hypotheses} hypotheses (seed {request.Seed})\u2026";
        ResultsText = $"Running {request.Hypotheses} hypotheses (seed {request.Seed})...";
        IsBusy = true;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        int total = request.Hypotheses;
        var progress = new Progress<int>(done =>
        {
            if (!IsBusy) return; // a late callback must not overwrite the final status
            ProgressIndeterminate = false;
            ProgressValue = 100.0 * done / total;
            HeadlineText = $"Solving: {done} of {total} hypotheses ({ProgressValue:F0} %)\u2026";
        });

        FlatAnalysisResult result;
        try
        {
            result = await Task.Run(() => FlatAnalysis.Run(request, null, token, progress), token);
        }
        catch (OperationCanceledException)
        {
            HeadlineText = "Solve cancelled. No result was produced.";
            ResultsText = "Solve cancelled by the user. No result was produced and the previous result was cleared.";
            return;
        }
        catch (Exception ex)
        {
            WarningText = $"\u2022 Solver error: {ex.Message}";
            HeadlineText = "The solve failed.";
            ResultsText = $"Solver error: {ex.Message}";
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }

        _lastRequest = request;
        _lastResult = result;
        WarningText = BuildWarnings(result);
        HeadlineText = BuildHeadline(result);
        Tiles = BuildTiles(result);
        NoteLines = result.Notes;
        ResultsText = FormatResults(request, result);
        try
        {
            ShowOnMap(request, result);
        }
        catch (Exception ex)
        {
            ResultsText += $"\n\nMAP ERROR (results above are still valid): {ex.Message}";
        }
    }

    private FlatAnalysisRequest BuildRequest()
    {
        double Num(string s, string name) =>
            TryParseNumber(s, out double v)
                ? v
                : throw new FormatException($"'{name}' must be a number (got '{s}').");

        int hypotheses = (int)Num(Hypotheses, "Hypotheses");
        if (hypotheses < 500 || hypotheses > 100_000)
            throw new FormatException("Hypotheses must be between 500 and 100000 (500 is the calibrated minimum).");

        if (!ulong.TryParse(Seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong seed))
            throw new FormatException("Seed must be a non-negative integer.");

        FlatSpin? spin = null;
        if (SpinDriftOn)
        {
            spin = new FlatSpin(
                Num(DiameterIn, "Bullet diameter"),
                Num(MassGr, "Mass"),
                Num(TwistIn, "Twist"),
                Num(LengthIn, "Bullet length"),
                RightHandTwist);
            spin.Validate();
        }

        return new FlatAnalysisRequest(
            ImpactLatDeg: Num(ImpactLat, "Impact latitude"),
            ImpactLonDeg: Num(ImpactLon, "Impact longitude"),
            ImpactElevationM: Num(ImpactElevation, "Impact elevation"),
            OriginHeightM: Num(OriginHeight, "Origin plane height"),
            ExteriorAngleDeg: Num(IncidenceAngle, "Angle of incidence"),
            ExteriorSigmaDeg: Num(IncidenceAngleSigma, "Angle of incidence sigma"),
            AzimuthDeg: Num(Azimuth, "Azimuth"),
            AzimuthSigmaDeg: Num(AzimuthSigma, "Azimuth sigma"),
            MvMinMps: Num(MvMinFps, "MV minimum") * UnitConversions.FpsToMps,
            MvMaxMps: Num(MvMaxFps, "MV maximum") * UnitConversions.FpsToMps,
            BcLbIn2: Num(BallisticCoefficient, "Ballistic coefficient"),
            BcSigmaFraction: Num(BcSigmaPercent, "BC sigma") / 100.0,
            ReferenceStandard: ReferenceStandard,
            TemperatureC: (Num(TemperatureF, "Temperature") - 32.0) * 5.0 / 9.0,
            PressurePa: Num(PressureInHg, "Pressure") * 3386.39,
            RelativeHumidity: Num(RelativeHumidity, "Relative humidity") / 100.0,
            WindSpeedMps: Num(WindSpeedMph, "Wind speed") * UnitConversions.MphToMps,
            WindFromDeg: Num(WindFromDeg, "Wind direction"),
            WindSigmaMps: Num(WindSigma, "Wind sigma"),
            Coriolis: CoriolisOn,
            Spin: spin,
            Hypotheses: hypotheses,
            Seed: seed,
            DemPath: string.IsNullOrWhiteSpace(DemPath) ? null : DemPath.Trim(),
            UseSrtm: UseSrtm,
            DemCacheDirectory: SrtmCache.DefaultDirectory);
    }

    private static string FormatResults(FlatAnalysisRequest q, FlatAnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("4TRAC 2027 - flat-earth solve");
        if (r.Notes.Count > 0 && r.Notes[0].StartsWith("CALIBRATION", StringComparison.Ordinal))
            sb.AppendLine($"*** {r.Notes[0]} ***");
        sb.AppendLine($"Seed {q.Seed} | hypotheses {q.Hypotheses} | Coriolis {(q.Coriolis ? "on" : "off")} | spin drift {(q.Spin is null ? "off" : "on")}");
        sb.AppendLine($"MV {q.MvMinMps / UnitConversions.FpsToMps:F0}-{q.MvMaxMps / UnitConversions.FpsToMps:F0} fps (uniform) | angle of incidence {q.ExteriorAngleDeg:F2} +/- {q.ExteriorSigmaDeg:F2} deg | azimuth {q.AzimuthDeg:F2} +/- {q.AzimuthSigmaDeg:F2} deg");
        sb.AppendLine($"BC {q.BcLbIn2:F4} +/- {q.BcSigmaFraction * 100:F1} % ({q.ReferenceStandard}) | origin plane {q.OriginHeightM:F1} m vs impact");
        sb.AppendLine();

        if (r.Error is not null)
        {
            sb.AppendLine($"NO REGION REPORTED: {r.Error}");
            AppendNotes(sb, r.Notes);
            return sb.ToString();
        }

        sb.AppendLine("MOST LIKELY ORIGIN (HDR mode, annotation only)");
        sb.AppendLine($"  lat/lon       {r.ModeLatDeg:F6}, {r.ModeLonDeg:F6}");
        sb.AppendLine($"  range         {r.ModeRangeM:F1} m from impact");
        sb.AppendLine($"  bearing       {r.ModeBearingDeg:F1} deg (origin as seen from impact)");
        sb.AppendLine($"  offset E / N  {r.ModeEnM.X:F1} / {r.ModeEnM.Y:F1} m");
        sb.AppendLine();

        sb.AppendLine("REGIONS (kernel-density HDR, eigen-axis calibrated)");
        foreach (var b in r.Bands)
            sb.AppendLine($"  {b.Fraction * 100:F0} % region   area {b.AreaM2:F0} m^2");
        sb.AppendLine();

        sb.AppendLine("IMPLIED MUZZLE VELOCITY (fps): P5 " +
            $"{r.MvP05Mps * UnitConversions.MpsToFps:F0}  P50 {r.MvP50Mps * UnitConversions.MpsToFps:F0}  P95 {r.MvP95Mps * UnitConversions.MpsToFps:F0}");
        sb.AppendLine($"ORIGIN RANGE (m):           P5 {r.RangeP05M:F0}  P50 {r.RangeP50M:F0}  P95 {r.RangeP95M:F0}");
        sb.AppendLine($"KDE cell {r.CellSizeM:F2} m, bandwidth {r.BandwidthM:F2} m");
        sb.AppendLine();

        AppendNotes(sb, r.Notes);
        return sb.ToString();
    }

    private static void AppendNotes(StringBuilder sb, IReadOnlyList<string> notes)
    {
        sb.AppendLine("DEVIATIONS AND ASSUMPTIONS");
        foreach (var n in notes)
            sb.AppendLine($"  - {n}");
    }

    private void ClearResultLayers()
    {
        var map = _mapControl.Map;
        var stale = map.Layers.Where(l => l.Name?.StartsWith(ResultPrefix, StringComparison.Ordinal) == true).ToList();
        foreach (var layer in stale)
            map.Layers.Remove(layer);
        _mapControl.Refresh();
    }

    private void ShowOnMap(FlatAnalysisRequest q, FlatAnalysisResult r)
    {
        var map = _mapControl.Map;
        ClearResultLayers();

        if (r.Error is not null)
        {
            _mapControl.Refresh();
            return;
        }

        var anchor = EnuFrame.AtOrigin(q.ImpactLatDeg, q.ImpactLonDeg, q.ImpactElevationM);

        foreach (var band in r.Bands)
        {
            var polygons = BuildPolygons(anchor, band.Rings);
            bool outer95 = band.Fraction > 0.9;
            var fill = outer95 ? new Color(255, 212, 0, 60) : new Color(255, 106, 0, 110);
            var outline = outer95 ? new Color(184, 134, 11) : new Color(255, 69, 0);
            map.Layers.Add(new MemoryLayer
            {
                Name = $"{ResultPrefix}{(int)Math.Round(band.Fraction * 100)}",
                Features = polygons.Select(p => (IFeature)new GeometryFeature { Geometry = p }).ToList(),
                Style = new VectorStyle
                {
                    Fill = new Brush(fill),
                    Outline = new Pen(outline, 2),
                },
            });
        }

        var impact = ToMercator(q.ImpactLonDeg, q.ImpactLatDeg);
        map.Layers.Add(new MemoryLayer
        {
            Name = $"{ResultPrefix}Impact",
            Features = new[] { (IFeature)new PointFeature(impact) },
            Style = new SymbolStyle { SymbolScale = 0.8, Fill = new Brush(Color.Red) },
        });

        var mode = ToMercator(r.ModeLonDeg, r.ModeLatDeg);
        map.Layers.Add(new MemoryLayer
        {
            Name = $"{ResultPrefix}Mode",
            Features = new[] { (IFeature)new PointFeature(mode) },
            Style = new SymbolStyle { SymbolScale = 0.8, Fill = new Brush(Color.Blue) },
        });

        double extent = Math.Max(r.Bands.Max(b => RingExtent(b.Rings)), 50.0);
        map.Navigator.CenterOnAndZoomTo(ToMercator(r.ModeLonDeg, r.ModeLatDeg), Math.Max(extent / 600.0, 0.05));
        _mapControl.Refresh();
    }

    private static double RingExtent(IReadOnlyList<IReadOnlyList<Vec2>> rings)
    {
        double max = 0;
        foreach (var ring in rings)
            foreach (var p in ring)
                max = Math.Max(max, p.Length);
        return 2.0 * max;
    }

    /// <summary>Outer rings (positive signed area) with the holes that lie inside them.</summary>
    private static List<Polygon> BuildPolygons(EnuFrame anchor, IReadOnlyList<IReadOnlyList<Vec2>> rings)
    {
        var factory = new GeometryFactory();
        var outers = new List<IReadOnlyList<Vec2>>();
        var holes = new List<IReadOnlyList<Vec2>>();
        foreach (var ring in rings)
        {
            if (MarchingSquares.SignedArea(ring) >= 0) outers.Add(ring);
            else holes.Add(ring);
        }

        var polygons = new List<Polygon>();
        foreach (var outer in outers)
        {
            var innerRings = holes
                .Where(h => Hdr.Contains(new[] { outer }, h[0]))
                .Select(h => ToLinearRing(factory, anchor, h))
                .ToArray();
            polygons.Add(factory.CreatePolygon(ToLinearRing(factory, anchor, outer), innerRings));
        }
        return polygons;
    }

    private static LinearRing ToLinearRing(GeometryFactory factory, EnuFrame anchor, IReadOnlyList<Vec2> ring)
    {
        var coords = ring.Select(p =>
        {
            var lla = ToLatLon(anchor, p);
            var m = ToMercator(lla.lon, lla.lat);
            return new Coordinate(m.X, m.Y);
        }).ToArray();
        return factory.CreateLinearRing(coords);
    }

    private static (double lat, double lon) ToLatLon(EnuFrame anchor, Vec2 enu)
    {
        var ecef = anchor.ToEcef(new Cartesian3(enu.X, enu.Y, 0.0));
        Geodetic.EcefToLla(ecef, out double lat, out double lon, out _);
        return (lat, lon);
    }

    private static MPoint ToMercator(double lonDeg, double latDeg)
    {
        double x = EarthRadiusM * lonDeg * UnitConversions.DegToRad;
        double y = EarthRadiusM * Math.Log(Math.Tan(Math.PI / 4.0 + latDeg * UnitConversions.DegToRad / 2.0));
        return new MPoint(x, y);
    }

    private void ExportGeoJson()
    {
        if (_lastRequest is null || _lastResult is null || _lastResult.Error is not null)
        {
            ResultsText += "\n\nGeoJSON export: run a successful solve first.";
            return;
        }

        try
        {
            var q = _lastRequest;
            var r = _lastResult;
            var anchor = EnuFrame.AtOrigin(q.ImpactLatDeg, q.ImpactLonDeg, q.ImpactElevationM);
            var features = new List<object>
            {
                new
                {
                    type = "Feature",
                    properties = new { name = "Impact", role = "impact" },
                    geometry = new { type = "Point", coordinates = new[] { q.ImpactLonDeg, q.ImpactLatDeg } },
                },
                new
                {
                    type = "Feature",
                    properties = new { name = "Most likely origin (annotation)", role = "mode", rangeM = r.ModeRangeM, bearingDeg = r.ModeBearingDeg },
                    geometry = new { type = "Point", coordinates = new[] { r.ModeLonDeg, r.ModeLatDeg } },
                },
            };

            foreach (var band in r.Bands)
            {
                var polys = new List<object>();
                foreach (var poly in BuildPolygonsLatLon(anchor, band.Rings))
                    polys.Add(poly);
                features.Add(new
                {
                    type = "Feature",
                    properties = new { name = $"{band.Fraction * 100:F0} % region", role = "region", fraction = band.Fraction, areaM2 = band.AreaM2, seed = q.Seed },
                    geometry = new { type = "MultiPolygon", coordinates = polys },
                });
            }

            var geojson = new { type = "FeatureCollection", features };
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"4TRAC_{DateTime.Now:yyyyMMdd_HHmmss}.geojson");
            File.WriteAllText(path, JsonSerializer.Serialize(geojson, new JsonSerializerOptions { WriteIndented = true }));
            ResultsText += $"\n\nExported GeoJSON to {path}";
        }
        catch (Exception ex)
        {
            ResultsText += $"\n\nGeoJSON export error: {ex.Message}";
        }
    }

    private static List<object> BuildPolygonsLatLon(EnuFrame anchor, IReadOnlyList<IReadOnlyList<Vec2>> rings)
    {
        var outers = new List<IReadOnlyList<Vec2>>();
        var holes = new List<IReadOnlyList<Vec2>>();
        foreach (var ring in rings)
        {
            if (MarchingSquares.SignedArea(ring) >= 0) outers.Add(ring);
            else holes.Add(ring);
        }

        var result = new List<object>();
        foreach (var outer in outers)
        {
            var coordinates = new List<object> { RingToLonLat(anchor, outer) };
            foreach (var h in holes.Where(h => Hdr.Contains(new[] { outer }, h[0])))
                coordinates.Add(RingToLonLat(anchor, h));
            result.Add(coordinates);
        }
        return result;
    }

    private static double[][] RingToLonLat(EnuFrame anchor, IReadOnlyList<Vec2> ring) =>
        ring.Select(p =>
        {
            var (lat, lon) = ToLatLon(anchor, p);
            return new[] { lon, lat };
        }).ToArray();

    private void ExportCsv()
    {
        if (_lastRequest is null || _lastResult is null || _lastResult.Error is not null || _lastResult.Cloud is null)
        {
            ResultsText += "\n\nCSV export: run a successful solve first.";
            return;
        }

        try
        {
            var q = _lastRequest;
            var anchor = EnuFrame.AtOrigin(q.ImpactLatDeg, q.ImpactLonDeg, q.ImpactElevationM);
            var sb = new StringBuilder();
            sb.AppendLine("east_m,north_m,lat_deg,lon_deg,muzzle_fps,range_m");
            foreach (var p in _lastResult.Cloud.Points)
            {
                var (lat, lon) = ToLatLon(anchor, new Vec2(p.EastM, p.NorthM));
                double range = Math.Sqrt(p.EastM * p.EastM + p.NorthM * p.NorthM);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{p.EastM:F3},{p.NorthM:F3},{lat:F8},{lon:F8},{p.ImpliedMuzzleVelocityMps * UnitConversions.MpsToFps:F2},{range:F3}"));
            }
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"4TRAC_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, sb.ToString());
            ResultsText += $"\n\nExported CSV to {path}";
        }
        catch (Exception ex)
        {
            ResultsText += $"\n\nCSV export error: {ex.Message}";
        }
    }

    private void ExportReport()
    {
        if (_lastRequest is null || _lastResult is null)
        {
            ResultsText += "\n\nCase report: run a solve first.";
            return;
        }

        try
        {
            var html = CaseReport.Html(_lastRequest, _lastResult, DateTime.UtcNow);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"4TRAC_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(path, html, Encoding.UTF8);
            ResultsText += $"\n\nExported case report to {path}";
        }
        catch (Exception ex)
        {
            ResultsText += $"\n\nCase report error: {ex.Message}";
        }
    }

    private void ExportPng()
    {
        try
        {
            var renderer = new Mapsui.Rendering.Skia.MapRenderer();
            var viewport = _mapControl.Map.Navigator.Viewport;
            using var stream = renderer.RenderToBitmapStream(_mapControl.Map, (float)viewport.Resolution, Mapsui.Rendering.RenderFormat.Png, 100);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"4TRAC_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            using var file = File.Create(path);
            stream.CopyTo(file);
            ResultsText += $"\n\nExported PNG to {path}";
        }
        catch (Exception ex)
        {
            ResultsText += $"\n\nPNG export error: {ex.Message}";
        }
    }
}

/// <summary>One headline number for the results strip.</summary>
public sealed record ResultTile(string Label, string Value);
