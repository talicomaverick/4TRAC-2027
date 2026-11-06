using System.Diagnostics;
using FourTrac.Core.Inverse;
using FourTrac.Core.Stats;

namespace FourTrac.Validation;

/// <summary>One Monte-Carlo trial outcome (one synthetic crime scene).</summary>
public sealed record TrialOutcome(
    bool Covered50,
    bool Covered95,
    double MedianErrorM,
    double MapErrorM,
    double BiasEastM,
    double BiasNorthM,
    double Area50M2,
    double Area95M2,
    int FailedHypotheses,
    double CloudExtentM,
    double ElapsedMs);

/// <summary>
/// Runs one V2 trial: perturb the truth scene with measurement noise, build
/// the inverse priors (truth at every prior's centre - the calibration
/// protocol), run the exact-prior Monte-Carlo cloud, compute the 50 %/95 %
/// HDR regions, and score coverage + origin error against the TRUE origin.
/// </summary>
public static class TrialRunner
{
    /// <summary>Cloud grid cell size: cloud extent / 300, clamped (node cap ~360k).</summary>
    public const double MinCellSizeM = 0.5;
    public const double MaxCellSizeM = 25.0;

    /// <summary>
    /// Deterministic per-trial seed derivation (splitmix-style mix of the run
    /// seed with cell/trial indices) - a trial is reproducible in isolation.
    /// </summary>
    public static ulong DeriveSeed(ulong baseSeed, long salt)
    {
        ulong z = unchecked(baseSeed + 0x9E3779B97F4A7C15UL * (ulong)salt + 1UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static TrialOutcome Run(
        TruthScene truth,
        CellProtocol protocol,
        int hypotheses,
        ulong seed,
        Vec2 truthEnu)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(protocol);
        if (hypotheses < 1)
            throw new ArgumentOutOfRangeException(nameof(hypotheses));
        var sw = Stopwatch.StartNew();

        // --- Scene observations: truth + measurement noise -------------------
        var noise = new Xoshiro256(DeriveSeed(seed, 0xA11CE5C7E5E5L));
        double extObs = truth.ExteriorAngleDeg + protocol.AngleNoiseSigmaDeg * noise.NextNormal();
        double azObs = truth.TravelAzimuthDeg + protocol.AngleNoiseSigmaDeg * noise.NextNormal();
        azObs = ((azObs % 360.0) + 360.0) % 360.0;
        double windE = truth.TruthWindEastMps + protocol.WindSigmaMps * noise.NextNormal();
        double windN = truth.TruthWindNorthMps + protocol.WindSigmaMps * noise.NextNormal();

        var observation = new ImpactObservation(
            truth.ImpactPositionEcef, extObs, azObs, truth.ImpactSpeedMps);

        // --- Priors: every truth quantity at its declared prior's centre -----
        var priors = new InversePriors(
            MuzzleVelocity: ScalarPrior.Uniform(
                truth.MuzzleVelocityMps - protocol.MvPriorHalfWidthMps,
                truth.MuzzleVelocityMps + protocol.MvPriorHalfWidthMps),
            ExteriorAngleDeg: ScalarPrior.Normal(extObs, protocol.AngleNoiseSigmaDeg),
            AzimuthDeg: ScalarPrior.Normal(azObs, protocol.AngleNoiseSigmaDeg),
            BcLbIn2: ScalarPrior.Normal(V2Scene.TruthBc,
                Math.Max(protocol.BcSigmaFraction * V2Scene.TruthBc, 1e-9)),
            WindEastMps: ScalarPrior.Normal(windE, protocol.WindSigmaMps),
            WindNorthMps: ScalarPrior.Normal(windN, protocol.WindSigmaMps));

        var opts = new BackwardOptions { OriginHeightM = V2Scene.HeightM };
        var cloud = OriginCloud.Run(observation, priors, V2Scene.Bullet, V2Scene.Atmosphere,
            includeCoriolis: true, hypotheses, DeriveSeed(seed, 0xC10D), opts);

        // --- HDR regions of the cloud ----------------------------------------
        var pts = new Vec2[cloud.Points.Count];
        double minE = double.PositiveInfinity, maxE = double.NegativeInfinity;
        double minN = double.PositiveInfinity, maxN = double.NegativeInfinity;
        for (int i = 0; i < cloud.Points.Count; i++)
        {
            var p = cloud.Points[i];
            pts[i] = new Vec2(p.EastM, p.NorthM);
            if (p.EastM < minE) minE = p.EastM;
            if (p.EastM > maxE) maxE = p.EastM;
            if (p.NorthM < minN) minN = p.NorthM;
            if (p.NorthM > maxN) maxN = p.NorthM;
        }

        double extent = pts.Length == 0 ? 0.0 : Math.Max(maxE - minE, maxN - minN);
        // Cell size follows the cloud (a 2 km cloud needs coarser cells than a
        // 10 m cloud); the KDE bandwidth is data-driven either way.
        double cellSize = pts.Length == 0
            ? MinCellSizeM
            : Math.Clamp(extent / 300.0, MinCellSizeM, MaxCellSizeM);

        var hdr = pts.Length == 0 ? null : Hdr.Compute(pts, cellSizeM: cellSize);
        var band50 = hdr?.Bands.FirstOrDefault(b => Math.Abs(b.Fraction - 0.5) < 1e-9);
        var band95 = hdr?.Bands.FirstOrDefault(b => Math.Abs(b.Fraction - 0.95) < 1e-9);

        bool covered50 = band50 is not null && Hdr.Contains(band50.Rings, truthEnu);
        bool covered95 = band95 is not null && Hdr.Contains(band95.Rings, truthEnu);

        double medianErr = hdr is null ? double.NaN
            : Math.Sqrt(Sq(hdr.Median.X - truthEnu.X) + Sq(hdr.Median.Y - truthEnu.Y));
        double mapErr = hdr is null ? double.NaN
            : Math.Sqrt(Sq(hdr.Mode.X - truthEnu.X) + Sq(hdr.Mode.Y - truthEnu.Y));

        sw.Stop();
        return new TrialOutcome(
            Covered50: covered50,
            Covered95: covered95,
            MedianErrorM: medianErr,
            MapErrorM: mapErr,
            BiasEastM: hdr is null ? double.NaN : hdr.Mean.X - truthEnu.X,
            BiasNorthM: hdr is null ? double.NaN : hdr.Mean.Y - truthEnu.Y,
            Area50M2: band50?.AreaM2 ?? double.NaN,
            Area95M2: band95?.AreaM2 ?? double.NaN,
            FailedHypotheses: cloud.FailedCount,
            CloudExtentM: extent,
            ElapsedMs: sw.Elapsed.TotalMilliseconds);
    }

    private static double Sq(double v) => v * v;
}
