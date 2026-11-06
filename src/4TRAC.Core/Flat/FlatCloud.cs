using FourTrac.Core.Drag;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using FourTrac.Core.Stats;

namespace FourTrac.Core.Flat;

/// <summary>How one hypothesis ended.</summary>
public enum FlatCloudStatus
{
    /// <summary>Origin found on the ascending branch inside the envelope.</summary>
    Ok,

    /// <summary>No ascending origin crossing (descending launch, or no solution in the time limit).</summary>
    NoAscendingOrigin,

    /// <summary>Origin found but beyond the declared envelope: rejected, not clamped.</summary>
    OutsideEnvelope,

    /// <summary>Drawn parameters were physically invalid (e.g. non-positive BC).</summary>
    InvalidDraw,

    /// <summary>The track left the DEM coverage: rejected, and counted separately because it truncates the posterior.</summary>
    OutsideTerrain,
}

/// <summary>Result of a flat Monte-Carlo cloud run. Counts always satisfy Points.Count + rejected = Requested.</summary>
public sealed record FlatCloudResult(
    IReadOnlyList<CloudPoint> Points,
    int Requested,
    int NoAscendingOrigin,
    int OutsideEnvelope,
    int InvalidDraw,
    int LowMachCount,
    double EnvelopeM,
    ulong Seed,
    int OutsideTerrain = 0,
    int NonPositiveAngleDraws = 0)
{
    public int Rejected => Requested - Points.Count;
}

/// <summary>
/// Monte-Carlo origin cloud in the flat model. Each hypothesis is an exact draw
/// from the declared priors (muzzle velocity, exterior angle, azimuth, BC, wind
/// east and north); the draw is mapped to an origin by
/// <see cref="FlatSolver.SolveAtMuzzleVelocity"/>. Draws are made sequentially
/// from the seed and solved in parallel, so results do not depend on core count.
/// </summary>
public static class FlatCloud
{
    /// <summary>400 fps: below this muzzle speed the bullet model is outside its validated range (LOW-MACH flag).</summary>
    public const double LowMachMps = 121.92;

    public static FlatCloudResult Run(
        InversePriors priors,
        DragTable reference,
        FlatScene scene,
        FlatSpin? spin,
        int hypotheses,
        ulong seed,
        FlatSolveOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(priors);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(scene);
        if (hypotheses < 1) throw new ArgumentOutOfRangeException(nameof(hypotheses));

        var o = options ?? new FlatSolveOptions();
        var sampler = new HypothesisSampler(seed);
        var draws = new ShotHypothesis[hypotheses];
        for (int i = 0; i < hypotheses; i++)
            draws[i] = sampler.Next(priors);

        // The exterior-angle prior is an untruncated Normal, so at shallow angles a
        // share of draws is <= 0 deg (the bullet RISING at impact). They are kept
        // (truncating would bias the cloud) but counted so the report can say so.
        int nonPositiveAngle = 0;
        for (int i = 0; i < hypotheses; i++)
            if (draws[i].ExteriorAngleDeg <= 0.0) nonPositiveAngle++;

        var status = new FlatCloudStatus[hypotheses];
        var points = new CloudPoint?[hypotheses];

        // Progress is reported about 100 times per run (not per hypothesis) so a UI
        // dispatcher is never flooded. Cancellation throws OperationCanceledException.
        int done = 0;
        int step = Math.Max(1, hypotheses / 100);
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        Parallel.For(0, hypotheses, parallelOptions, i =>
        {
            try
            {
            var h = draws[i];
            if (!(h.BcLbIn2 > 0))
            {
                status[i] = FlatCloudStatus.InvalidDraw;
                return;
            }

            var drag = new BcDragModel(reference, new ConstantBcProvider(h.BcLbIn2));
            var origin = FlatSolver.SolveAtMuzzleVelocity(
                new FlatObservation(h.ExteriorAngleDeg, h.AzimuthDeg),
                h.MuzzleVelocityMps,
                drag,
                scene,
                h.WindEastMps,
                h.WindNorthMps,
                spin,
                o);

            if (!origin.Converged)
            {
                status[i] = origin.EndReason == FlatBackwardEnd.OutsideTerrain
                    ? FlatCloudStatus.OutsideTerrain
                    : FlatCloudStatus.NoAscendingOrigin;
                return;
            }

            double range = Math.Sqrt(origin.EastM * origin.EastM + origin.NorthM * origin.NorthM);
            if (range > o.EnvelopeM)
            {
                status[i] = FlatCloudStatus.OutsideEnvelope;
                return;
            }

            status[i] = FlatCloudStatus.Ok;
            points[i] = new CloudPoint(origin.EastM, origin.NorthM, origin.MuzzleVelocityMps);
            }
            finally
            {
                int n = Interlocked.Increment(ref done);
                if (progress is not null && (n % step == 0 || n == hypotheses))
                    progress.Report(n);
            }
        });

        var kept = new List<CloudPoint>(hypotheses);
        int noOrigin = 0, outside = 0, invalid = 0, lowMach = 0, terrain = 0;
        for (int i = 0; i < hypotheses; i++)
        {
            switch (status[i])
            {
                case FlatCloudStatus.Ok:
                    kept.Add(points[i]!.Value);
                    if (points[i]!.Value.ImpliedMuzzleVelocityMps < LowMachMps) lowMach++;
                    break;
                case FlatCloudStatus.NoAscendingOrigin: noOrigin++; break;
                case FlatCloudStatus.OutsideEnvelope: outside++; break;
                case FlatCloudStatus.InvalidDraw: invalid++; break;
                case FlatCloudStatus.OutsideTerrain: terrain++; break;
            }
        }

        return new FlatCloudResult(kept, hypotheses, noOrigin, outside, invalid, lowMach, o.EnvelopeM, seed, terrain, nonPositiveAngle);
    }

    /// <summary>
    /// Grid cell size for the HDR calculation: fine enough to resolve the
    /// cloud's pooled spread, coarse enough to keep the grid within memory.
    /// </summary>
    public static double ChooseCellSize(IReadOnlyList<CloudPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) return 1.0;

        double mx = 0, my = 0;
        foreach (var p in points) { mx += p.EastM; my += p.NorthM; }
        mx /= points.Count; my /= points.Count;
        double s2 = 0;
        foreach (var p in points)
            s2 += (p.EastM - mx) * (p.EastM - mx) + (p.NorthM - my) * (p.NorthM - my);
        double pooled = Math.Sqrt(s2 / (2.0 * (points.Count - 1)));

        return Math.Max(0.25, pooled / 25.0);
    }
}
