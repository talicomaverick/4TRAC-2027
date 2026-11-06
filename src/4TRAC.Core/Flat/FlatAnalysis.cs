using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using FourTrac.Core.Stats;

namespace FourTrac.Core.Flat;

/// <summary>
/// Full scene analysis request, in SI units. The impact point is the scene
/// anchor. Angles are degrees; priors are Normal(observed, sigma) for the
/// scene angles and BC, and Normal(component, sigma) for the wind.
/// </summary>
public sealed record FlatAnalysisRequest(
    double ImpactLatDeg,
    double ImpactLonDeg,
    double ImpactElevationM,
    double OriginHeightM,
    double ExteriorAngleDeg,
    double ExteriorSigmaDeg,
    double AzimuthDeg,
    double AzimuthSigmaDeg,
    double MvMinMps,
    double MvMaxMps,
    double BcLbIn2,
    double BcSigmaFraction,
    string ReferenceStandard,
    double TemperatureC,
    double PressurePa,
    double RelativeHumidity,
    double WindSpeedMps,
    double WindFromDeg,
    double WindSigmaMps,
    bool Coriolis,
    FlatSpin? Spin,
    int Hypotheses,
    ulong Seed,
    string? DemPath = null,
    bool UseSrtm = false,
    string? DemCacheDirectory = null);

/// <summary>Area and location summary of one HDR band.</summary>
public sealed record FlatBandSummary(double Fraction, double AreaM2, IReadOnlyList<IReadOnlyList<Vec2>> Rings);

/// <summary>
/// Result of a scene analysis. When <see cref="Error"/> is set, no region was
/// produced and the reason is stated; nothing is estimated in its place.
/// </summary>
public sealed record FlatAnalysisResult(
    string? Error,
    FlatCloudResult? Cloud,
    IReadOnlyList<FlatBandSummary> Bands,
    Vec2 ModeEnM,
    Vec2 MedianEnM,
    double ModeLatDeg,
    double ModeLonDeg,
    double ModeRangeM,
    double ModeBearingDeg,
    double MvP05Mps,
    double MvP50Mps,
    double MvP95Mps,
    double RangeP05M,
    double RangeP50M,
    double RangeP95M,
    double CellSizeM,
    double BandwidthM,
    IReadOnlyList<string> Notes);

public static class FlatAnalysis
{
    /// <summary>Minimum converged hypotheses for a region to be reported.</summary>
    public const int MinimumPoints = 20;

    /// <summary>Hypothesis count of the truth-drawn calibration runs; fewer is outside the calibrated conditions.</summary>
    public const int CalibratedHypotheses = 500;

    /// <summary>Share of rejected hypotheses (outside envelope / no ascending origin) above which the cloud is flagged as truncated.</summary>
    public const double TruncationWarnFraction = 0.05;

    /// <summary>Horizontal origin range covered by the recorded calibration evidence (m).</summary>
    private const double CalibratedRangeMinM = 500.0;
    private const double CalibratedRangeMaxM = 4500.0;

    public static FlatAnalysisResult Run(
        FlatAnalysisRequest r,
        FlatSolveOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        var notes = new List<string>();

        if (r.MvMaxMps <= r.MvMinMps || r.MvMinMps <= 0)
            return Failed("Muzzle velocity range is invalid: minimum must be positive and below maximum.", notes);
        if (r.Hypotheses < MinimumPoints)
            return Failed($"At least {MinimumPoints} hypotheses are required.", notes);
        if (r.BcLbIn2 <= 0)
            return Failed("Ballistic coefficient must be positive.", notes);

        var reference = DragTableCatalog.ByStandard(r.ReferenceStandard);

        IFlatSurface? surface = null;
        double impactElevationM = r.ImpactElevationM;
        if (r.UseSrtm || !string.IsNullOrWhiteSpace(r.DemPath))
        {
            try
            {
                if (r.UseSrtm)
                {
                    // Cover the declared envelope around the impact, plus a margin.
                    var cache = r.DemCacheDirectory ?? SrtmCache.DefaultDirectory;
                    var mosaic = SrtmCache.Build(r.ImpactLatDeg, r.ImpactLonDeg, (options?.EnvelopeM ?? 10_000.0) + 500.0, cache);
                    var demSurface = new DemSurface(mosaic, r.ImpactLatDeg, r.ImpactLonDeg);
                    surface = demSurface;
                    impactElevationM = demSurface.AnchorHeightM;
                    notes.Add($"Origin surface: SRTM 1-degree tiles ({mosaic.TileCount} tiles, cache '{cache}'; AWS open data). Origin plane height input is not used. Coverage gaps are rejected, never extrapolated.");
                }
                else
                {
                    var demSurface = new DemSurface(DemGrid.Load(r.DemPath!), r.ImpactLatDeg, r.ImpactLonDeg);
                    surface = demSurface;
                    impactElevationM = demSurface.AnchorHeightM;
                    notes.Add($"Origin surface: DEM '{Path.GetFileName(r.DemPath)}' (ESRI ASCII, WGS84). Origin plane height input is not used. Coverage gaps are rejected, never extrapolated.");
                }
                if (Math.Abs(impactElevationM - r.ImpactElevationM) > 1.0)
                    notes.Add($"Impact elevation taken from the terrain ({impactElevationM:F1} m), not the input ({r.ImpactElevationM:F1} m); the atmosphere is anchored there.");
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or FormatException or OverflowException or HttpRequestException or TaskCanceledException)
            {
                return Failed($"Terrain could not be used: {ex.Message}", notes);
            }
        }

        var atmosphere = Atmosphere.FromMeasurements(r.TemperatureC, r.PressurePa, r.RelativeHumidity, impactElevationM);
        var scene = new FlatScene(r.ImpactLatDeg, r.OriginHeightM, atmosphere, r.Coriolis, surface);

        double windE = -r.WindSpeedMps * Math.Sin(r.WindFromDeg * UnitConversions.DegToRad);
        double windN = -r.WindSpeedMps * Math.Cos(r.WindFromDeg * UnitConversions.DegToRad);

        var priors = new InversePriors(
            ScalarPrior.Uniform(r.MvMinMps, r.MvMaxMps),
            ScalarPrior.Normal(r.ExteriorAngleDeg, r.ExteriorSigmaDeg),
            ScalarPrior.Normal(r.AzimuthDeg, r.AzimuthSigmaDeg),
            ScalarPrior.Normal(r.BcLbIn2, r.BcLbIn2 * r.BcSigmaFraction),
            ScalarPrior.Normal(windE, r.WindSigmaMps),
            ScalarPrior.Normal(windN, r.WindSigmaMps));

        var cloud = FlatCloud.Run(priors, reference, scene, r.Spin, r.Hypotheses, r.Seed, options, cancellationToken, progress);

        notes.Add($"Hypotheses: {cloud.Requested} requested, {cloud.Points.Count} inside the region analysis, {cloud.Rejected} rejected.");
        if (cloud.NoAscendingOrigin > 0)
            notes.Add($"{cloud.NoAscendingOrigin} rejected: no ascending origin crossing (descending launch, or no solution in the time limit).");
        if (cloud.OutsideEnvelope > 0)
            notes.Add($"{cloud.OutsideEnvelope} rejected: origin beyond the {cloud.EnvelopeM:F0} m validated envelope (rejected, not clamped).");
        if (cloud.InvalidDraw > 0)
            notes.Add($"{cloud.InvalidDraw} rejected: invalid drawn parameters.");
        if (cloud.OutsideTerrain > 0)
        {
            notes.Add($"{cloud.OutsideTerrain} of {cloud.Requested} rejected: track left the DEM coverage.");
            if (cloud.OutsideTerrain > 0.05 * cloud.Requested)
                return Failed(
                    $"{cloud.OutsideTerrain} of {cloud.Requested} hypotheses left the DEM coverage. Rejecting them would truncate the posterior, so no region is reported. Supply a DEM that covers the full search area.",
                    notes, cloud);
        }
        int truncating = cloud.OutsideEnvelope + cloud.NoAscendingOrigin;
        if (truncating > TruncationWarnFraction * cloud.Requested)
            notes.Add($"WARNING - TRUNCATED CLOUD: {truncating} of {cloud.Requested} hypotheses ({100.0 * truncating / cloud.Requested:F1} %) were rejected as outside the envelope or without an ascending origin. The regions below are built from the remaining hypotheses only and may misstate where the probability lies.");
        if (cloud.NonPositiveAngleDraws > 0)
            notes.Add($"{cloud.NonPositiveAngleDraws} of {cloud.Requested} drawn angles of incidence were at or below 0 deg (bullet rising at impact). They come from the untruncated Normal angle prior and are kept, not removed; a large share means the angle uncertainty is too wide for the measured angle.");
        if (cloud.LowMachCount > 0)
            notes.Add($"LOW-MACH: {cloud.LowMachCount} accepted hypotheses have muzzle speed below 400 fps (121.92 m/s); they validate the solver, not the bullet model.");

        if (cloud.Points.Count < MinimumPoints)
            return Failed($"Only {cloud.Points.Count} of {cloud.Requested} hypotheses converged inside the envelope; no region is reported.", notes, cloud);

        var pts = cloud.Points.Select(p => new Vec2(p.EastM, p.NorthM)).ToList();
        double cell = FlatCloud.ChooseCellSize(cloud.Points);
        var hdr = Hdr.Compute(pts, cell, new[] { 0.5, 0.95 });

        var mode = hdr.Mode;
        var anchor = EnuFrame.AtOrigin(r.ImpactLatDeg, r.ImpactLonDeg, impactElevationM);
        var modeEcef = anchor.ToEcef(new Cartesian3(mode.X, mode.Y, 0.0));
        Geodetic.EcefToLla(modeEcef, out double modeLat, out double modeLon, out _);

        double modeRange = mode.Length;
        double modeBearing = (Math.Atan2(mode.X, mode.Y) * UnitConversions.RadToDeg + 360.0) % 360.0;

        var mvs = cloud.Points.Select(p => p.ImpliedMuzzleVelocityMps).ToArray();
        var ranges = cloud.Points.Select(p => Math.Sqrt(p.EastM * p.EastM + p.NorthM * p.NorthM)).ToArray();

        var bands = hdr.Bands
            .Select(b => new FlatBandSummary(b.Fraction, b.AreaM2, b.Rings))
            .ToList();

        var outsideCalibration = new List<string>();
        if (r.Hypotheses < CalibratedHypotheses)
            outsideCalibration.Add($"{r.Hypotheses} hypotheses (calibrated at {CalibratedHypotheses}; at 40 the 95 % band covered only 88 %)");
        if (r.Spin is not null)
            outsideCalibration.Add("spin drift on (calibration runs had spin off)");
        if (surface is not null)
            outsideCalibration.Add("terrain surface in use (calibration used a flat origin plane)");
        double medianRange = Percentile(ranges, 0.50);
        if (medianRange < CalibratedRangeMinM || medianRange > CalibratedRangeMaxM)
            outsideCalibration.Add($"median origin range {medianRange:F0} m (calibration evidence covers {CalibratedRangeMinM:F0}-{CalibratedRangeMaxM:F0} m)");

        string calibrationStatus = outsideCalibration.Count == 0
            ? "This run is inside the calibrated conditions."
            : "THIS RUN IS OUTSIDE THE CALIBRATED CONDITIONS, so the coverage figures do NOT establish its error rate. Not covered: " + string.Join("; ", outsideCalibration) + ".";
        notes.Insert(0, "CALIBRATION (flat origin prior, truth-drawn, flat plane, spin off, 150 trials x 500 hypotheses at 3-4.5 km): 50 % band covers 49.3 %, 95 % band covers 98.0 % - inside the 45-55 % and 92-98 % gates (95 % at the upper edge). The origin prior is assumed flat over the search area. " + calibrationStatus);
        notes.Add($"Region method: 50 % and 95 % contours from a Gaussian KDE (Scott bandwidth) with the eigen-axis mass calibration (Stats/Hdr.cs).");
        notes.Add("Declared model: flat-earth point mass, ENU frame, gravity 9.80665 m/s^2, constant wind, scalar G1/G7 BC.");
        notes.Add($"Coriolis: {(r.Coriolis ? "on" : "off")}; spin drift: {(r.Spin is null ? "off" : "on (Litz form, Miller stability factor)")}.");
        if (r.OriginHeightM > 0 && surface is null)
            notes.Add("Origin plane is ABOVE the impact: a lofted and a descending launch can both reach this impact. Only the lofted (ascending) solution is reported; a descending shooter from this plane is not represented.");
        notes.Add(surface is null
            ? "Origin surface: flat plane at the origin height input (no DEM)."
            : "Earth curvature in the DEM heights is included (tangent-plane vertical), up to the 10 km envelope.");
        notes.Add("Not modelled: Magnus effect, wind gradients and layered wind, descending-launch origins, BC(v) and CD(Mach) drag modes.");

        return new FlatAnalysisResult(
            null,
            cloud,
            bands,
            mode,
            hdr.Median,
            modeLat,
            modeLon,
            modeRange,
            modeBearing,
            Percentile(mvs, 0.05), Percentile(mvs, 0.50), Percentile(mvs, 0.95),
            Percentile(ranges, 0.05), Percentile(ranges, 0.50), Percentile(ranges, 0.95),
            cell,
            hdr.BandwidthM,
            notes);
    }

    private static FlatAnalysisResult Failed(string error, List<string> notes, FlatCloudResult? cloud = null) =>
        new(error, cloud, Array.Empty<FlatBandSummary>(), default, default, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, notes);

    private static double Percentile(double[] values, double p)
    {
        var s = values.OrderBy(v => v).ToArray();
        double idx = p * (s.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return lo == hi ? s[lo] : s[lo] + (idx - lo) * (s[hi] - s[lo]);
    }
}
