using FourTrac.Core.Atm;
using FourTrac.Core.Geo;
using FourTrac.Core.Solver;

namespace FourTrac.Core.Inverse;

/// <summary>
/// One hypothesised origin from the Monte-Carlo cloud: position in the
/// impact-anchored scene ENU frame (metres; origins are uprange of the
/// impact, so typically negative along the travel direction), plus the muzzle
/// velocity the backward solve realised for this hypothesis.
/// </summary>
public readonly record struct CloudPoint(
    double EastM,
    double NorthM,
    double ImpliedMuzzleVelocityMps);

/// <summary>
/// The Monte-Carlo cloud: exact prior draws mapped through the backward solve
/// to origin hypotheses. Unweighted by construction - every dimension (muzzle
/// velocity, angles, BC, wind) is drawn directly from its declared prior and
/// the muzzle-velocity consistency solve makes the draw exact, so importance
/// weights are 1 and no reweighting is ever applied (PLAN.md §8.3 M3.2).
/// </summary>
public sealed record OriginCloudResult(
    IReadOnlyList<CloudPoint> Points,
    int FailedCount,
    int RequestedCount,
    ulong Seed);

/// <summary>
/// Drives the inverse Monte-Carlo: draw a hypothesis, resolve its wind to
/// ECEF at the impact anchor (M3.1 convention - the air mass does not rotate
/// with ENU axes), run the muzzle-velocity consistency solve, report the
/// origin in the impact-anchored scene frame.
/// </summary>
public static class OriginCloud
{
    /// <summary>
    /// Generate <paramref name="hypotheses"/> origin hypotheses from
    /// <paramref name="priors"/>. Deterministic per <paramref name="seed"/>
    /// (xoshiro256**), so a run is exactly reproducible from the case report.
    /// Hypotheses whose muzzle-velocity consistency solve does not converge are
    /// counted in <see cref="OriginCloudResult.FailedCount"/> and dropped.
    /// </summary>
    public static OriginCloudResult Run(
        ImpactObservation observation,
        InversePriors priors,
        BulletProfile bullet,
        Atmosphere atmosphere,
        bool includeCoriolis,
        int hypotheses,
        ulong seed,
        BackwardOptions? options = null,
        double mvToleranceMps = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(priors);
        ArgumentNullException.ThrowIfNull(bullet);
        ArgumentNullException.ThrowIfNull(atmosphere);
        if (hypotheses < 1)
            throw new ArgumentOutOfRangeException(nameof(hypotheses), "At least one hypothesis is required.");

        var impactFrame = BackwardSolver.FrameAtImpact(observation);
        var sampler = new HypothesisSampler(seed);
        var points = new List<CloudPoint>(hypotheses);
        int failed = 0;

        for (int i = 0; i < hypotheses; i++)
        {
            ShotHypothesis h = sampler.Next(priors);

            // Wind prior is on the ENU components measured at the crime scene:
            // resolve once through the impact anchor, hold fixed (M3.1).
            var windEcef = impactFrame.VectorToEcef(
                new Cartesian3(h.WindEastMps, h.WindNorthMps, 0.0));

            var hypBullet = ApplyBc(bullet, h.BcLbIn2, priors.BcLbIn2.A);

            // Per-hypothesis observables: the DRAWN scene angles enter the
            // solve here. (V2 caught the earlier version passing the original
            // observation through, which silently froze the cloud's angle
            // dimension - the cloud then had no angle scatter at all and
            // coverage collapsed. Every drawn quantity must reach the solve.)
            var hypObservation = new ImpactObservation(
                observation.PositionEcef,
                h.ExteriorAngleDeg,
                h.AzimuthDeg,
                observation.ImpactSpeedMps);

            var solve = BackwardSolver.SolveAtMuzzleVelocity(
                hypObservation,
                h.MuzzleVelocityMps,
                hypBullet,
                atmosphere,
                windEcef,
                includeCoriolis,
                options,
                mvToleranceMps);

            if (!solve.Converged)
            {
                failed++;
                continue;
            }

            var enu = impactFrame.ToEnu(solve.Origin.OriginEcef);
            points.Add(new CloudPoint(enu.X, enu.Y, solve.ImpliedMuzzleVelocityMps));
        }

        return new OriginCloudResult(points, failed, hypotheses, seed);
    }

    /// <summary>
    /// Apply a drawn BC to a profile without mutating the template.
    /// Scalar mode: the draw IS the BC (lb/in^2). BC(v) mode: every knot is
    /// scaled by drawn/nominal, where the nominal is the prior centre - a
    /// uniform multiplicative perturbation of the fitted curve (F-B1 knot set
    /// stays shape-identical). CD-table mode: no BC exists to perturb; the
    /// draw is ignored and the table is used as-is (documented in §8.3 -
    /// CD-table sigma is an M4/Litz item).
    /// </summary>
    private static BulletProfile ApplyBc(BulletProfile template, double drawnBcLbIn2, double nominalBcLbIn2)
    {
        if (template.DragMode == DragMode.CdTable)
            return template;

        if (!(drawnBcLbIn2 > 0))
            throw new InvalidOperationException($"Drawn BC must be positive (got {drawnBcLbIn2}).");

        var copy = new BulletProfile
        {
            Name = template.Name,
            Caliber = template.Caliber,
            MassGr = template.MassGr,
            DiameterIn = template.DiameterIn,
            DragMode = template.DragMode,
            ReferenceStandard = template.ReferenceStandard,
            BcLbIn2 = template.BcLbIn2,
            BcPoints = template.BcPoints,
            CdPoints = template.CdPoints,
            Provenance = template.Provenance,
        };

        switch (template.DragMode)
        {
            case DragMode.ScalarBc:
                copy.BcLbIn2 = drawnBcLbIn2;
                break;

            case DragMode.BcVelocity:
                if (!(nominalBcLbIn2 > 0))
                    throw new InvalidOperationException(
                        "BC(v) mode needs a positive prior centre to scale the knots against.");
                double ratio = drawnBcLbIn2 / nominalBcLbIn2;
                copy.BcPoints = template.BcPoints!
                    .Select(p => new[] { p[0], p[1] * ratio })
                    .ToList();
                break;
        }

        return copy;
    }
}
