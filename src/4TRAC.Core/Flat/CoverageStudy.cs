using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using FourTrac.Core.Stats;

namespace FourTrac.Core.Flat;

/// <summary>
/// Truth-drawn calibration setup (PLAN.md §8.3 open question 4, now implemented).
/// Every trial draws the TRUE muzzle velocity, BC and wind from the same priors
/// the inverse uses, simulates the true shot forward, perturbs the observed
/// impact angles by the declared measurement noise, and then asks whether the
/// true origin lies inside the inverse's 50 % and 95 % regions. Because truth
/// and prior come from the same distributions, coverage is calibrated by
/// construction, rather than only at the prior centre.
/// </summary>
public sealed record CoverageConfig(
    double LatDeg,
    double OriginHeightM,
    Atmosphere Atmosphere,
    bool Coriolis,
    DragTable Reference,
    double BcNominalLbIn2,
    double BcSigmaLbIn2,
    double LaunchElevationMinDeg,
    double LaunchElevationMaxDeg,
    double LaunchAzimuthDeg,
    double MvMinMps,
    double MvMaxMps,
    double WindEastMps,
    double WindNorthMps,
    double WindSigmaMps,
    double AngleSigmaDeg,
    double AzimuthSigmaDeg,
    FlatSpin? Spin,
    double OriginRangeMinM = 0.0,
    double OriginRangeMaxM = 0.0);

/// <summary>One calibration trial. Errors are in metres in the impact frame; the signed error is truth minus the cloud median.</summary>
public sealed record CoverageTrial(
    int Index,
    double TruthMuzzleVelocityMps,
    double TruthRangeM,
    bool Covered50,
    bool Covered95,
    double MedianErrorM,
    double SignedEastErrorM,
    double SignedNorthErrorM,
    int CloudPoints,
    int CloudNoOrigin,
    int CloudOutsideEnvelope,
    double TruthExteriorDeg = double.NaN,
    double TruthOriginRangeM = double.NaN);

/// <summary>Aggregate calibration result. Coverage is the fraction of completed trials.</summary>
public sealed record CoverageResult(
    IReadOnlyList<CoverageTrial> Trials,
    int Skipped,
    double Coverage50,
    double Coverage95,
    double MedianErrorM,
    ulong Seed);

public static class CoverageStudy
{
    public static CoverageResult Run(
        CoverageConfig config,
        int trials,
        int hypothesesPerTrial,
        ulong seed,
        FlatSolveOptions? options = null,
        bool sampleCalibrated = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (trials < 1) throw new ArgumentOutOfRangeException(nameof(trials));
        if (hypothesesPerTrial < 10) throw new ArgumentOutOfRangeException(nameof(hypothesesPerTrial), "Use at least 10 hypotheses.");

        var o = options ?? new FlatSolveOptions();
        var results = new List<CoverageTrial>(trials);
        int skipped = 0;

        for (int i = 0; i < trials; i++)
        {
            ulong trialSeed = seed ^ (0x9E3779B97F4A7C15UL * (ulong)(i + 1));
            var trial = RunTrial(config, i, trialSeed, hypothesesPerTrial, o, sampleCalibrated);
            if (trial is null) skipped++;
            else results.Add(trial);
        }

        int n = results.Count;
        double c50 = n == 0 ? double.NaN : results.Count(t => t.Covered50) / (double)n;
        double c95 = n == 0 ? double.NaN : results.Count(t => t.Covered95) / (double)n;
        double medErr = n == 0 ? double.NaN : Median(results.Select(t => t.MedianErrorM).ToArray());

        return new CoverageResult(results, skipped, c50, c95, medErr, seed);
    }

    private static CoverageTrial? RunTrial(
        CoverageConfig c, int index, ulong trialSeed, int hypotheses, FlatSolveOptions o, bool sampleCalibrated)
    {
        var rng = new Xoshiro256(trialSeed);

        // Truth drawn from the same priors the inverse will declare.
        double mv = ScalarPrior.Uniform(c.MvMinMps, c.MvMaxMps).Sample(rng);
        double bc = ScalarPrior.Normal(c.BcNominalLbIn2, c.BcSigmaLbIn2).Sample(rng);
        double we = ScalarPrior.Normal(c.WindEastMps, c.WindSigmaMps).Sample(rng);
        double wn = ScalarPrior.Normal(c.WindNorthMps, c.WindSigmaMps).Sample(rng);
        if (!(bc > 0)) return null;

        var truthDrag = new BcDragModel(c.Reference, new ConstantBcProvider(bc));
        var truthScene = new FlatScene(c.LatDeg, c.OriginHeightM, c.Atmosphere, c.Coriolis);

        Vec2 truth;
        Cartesian3 impactVel;
        if (c.OriginRangeMaxM > 0)
        {
            // Flat origin prior: the shooter's position is uniform by area in the annulus;
            // the launch that reaches the impact is then found by shooting.
            if (c.Spin is not null)
                throw new ArgumentException("Origin-prior truth does not yet include spin drift.");
            double r = Math.Sqrt(ScalarPrior.Uniform(c.OriginRangeMinM * c.OriginRangeMinM, c.OriginRangeMaxM * c.OriginRangeMaxM).Sample(rng));
            double bearing = ScalarPrior.Uniform(0.0, 360.0).Sample(rng) * UnitConversions.DegToRad;
            double ox = r * Math.Sin(bearing), oy = r * Math.Cos(bearing);
            var shot = FlatShooter.ImpactVelocity(
                new FlatTrajectoryModel(truthDrag, c.Atmosphere, c.LatDeg, we, wn, c.Coriolis), mv, ox, oy, c.OriginHeightM);
            if (shot is null) return null;
            impactVel = shot.Value;
            truth = new Vec2(ox, oy);
        }
        else
        {
            // Launch-elevation prior (the earlier design): the origin prior is then not flat.
            double launchElev = ScalarPrior.Uniform(c.LaunchElevationMinDeg, c.LaunchElevationMaxDeg).Sample(rng);
            var impact = TruthImpact(c, launchElev, truthDrag, mv, we, wn);
            if (impact is null) return null;
            impactVel = impact.Value.Velocity;
            truth = new Vec2(-impact.Value.Impact.X, -impact.Value.Impact.Y);
        }

        double exterior = -EnuFrame.ElevationDeg(impactVel);
        double azimuth = EnuFrame.AzimuthDeg(impactVel);

        double obsExterior = exterior + c.AngleSigmaDeg * rng.NextNormal();
        double obsAzimuth = azimuth + c.AzimuthSigmaDeg * rng.NextNormal();

        var priors = new InversePriors(
            ScalarPrior.Uniform(c.MvMinMps, c.MvMaxMps),
            ScalarPrior.Normal(obsExterior, c.AngleSigmaDeg),
            ScalarPrior.Normal(obsAzimuth, c.AzimuthSigmaDeg),
            ScalarPrior.Normal(c.BcNominalLbIn2, c.BcSigmaLbIn2),
            ScalarPrior.Normal(c.WindEastMps, c.WindSigmaMps),
            ScalarPrior.Normal(c.WindNorthMps, c.WindSigmaMps));

        var cloud = FlatCloud.Run(priors, c.Reference, truthScene, c.Spin, hypotheses, trialSeed ^ 0xD1B54A32D192ED03UL, o);
        if (cloud.Points.Count < 10) return null;

        var pts = cloud.Points.Select(p => new Vec2(p.EastM, p.NorthM)).ToList();
        var hdr = Hdr.Compute(pts, FlatCloud.ChooseCellSize(cloud.Points), new[] { 0.5, 0.95 }, 0.0, sampleCalibrated);

        var err = truth - hdr.Median;
        return new CoverageTrial(
            index,
            mv,
            truth.Length,
            Hdr.Contains(hdr.Bands[0].Rings, truth),
            Hdr.Contains(hdr.Bands[1].Rings, truth),
            err.Length,
            err.X,
            err.Y,
            cloud.Points.Count,
            cloud.NoAscendingOrigin,
            cloud.OutsideEnvelope,
            exterior,
            truth.Length);
    }

    /// <summary>
    /// Forward truth from the origin (0, 0, h_o) to the impact plane. With spin,
    /// the drift-free trajectory is displaced to the right by D(T) and its
    /// velocity gets D'(T), matching what the inverse removes.
    /// </summary>
    private static (Cartesian3 Impact, Cartesian3 Velocity)? TruthImpact(
        CoverageConfig c, double launchElevationDeg, IDragModel drag, double mv, double windE, double windN)
    {
        var model = new FlatTrajectoryModel(drag, c.Atmosphere, c.LatDeg, windE, windN, c.Coriolis);
        var v0 = EnuFrame.DirectionFromAzimuthElevation(c.LaunchAzimuthDeg, launchElevationDeg) * mv;
        var start = new Cartesian3(0.0, 0.0, c.OriginHeightM);
        var fwd = FlatIntegrators.Forward(model, start, v0);
        if (!fwd.Hit) return null;

        if (c.Spin is null)
            return (fwd.Position, fwd.Velocity);

        double sg = SpinDrift.StabilityFactor(c.Spin, mv);
        double azNd = EnuFrame.AzimuthDeg(fwd.Velocity);
        double drift = 0.0, lateral = 0.0;
        for (int pass = 0; pass < 2; pass++)
        {
            drift = SpinDrift.DisplacementM(sg, fwd.TimeOfFlightS, c.Spin.RightHandTwist);
            lateral = SpinDrift.LateralVelocityMps(sg, fwd.TimeOfFlightS, c.Spin.RightHandTwist);
            var right = SpinDrift.RightOf(azNd);
            azNd = EnuFrame.AzimuthDeg(fwd.Velocity + right * lateral);
        }
        var rightFinal = SpinDrift.RightOf(azNd);
        return (fwd.Position + rightFinal * drift, fwd.Velocity + rightFinal * lateral);
    }

    private static double Median(double[] values)
    {
        var s = values.OrderBy(v => v).ToArray();
        int m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : 0.5 * (s[m - 1] + s[m]);
    }
}
