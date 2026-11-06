namespace FourTrac.Core.Inverse;

/// <summary>
/// One sampled hypothesis for the inverse problem (PLAN.md §6 "Monte-Carlo
/// sampler"). All SI except where named; angles in degrees. Wind is the
/// air-motion ENU vector (east-positive, north-positive), resolved to ECEF
/// once at the impact anchor before the backward solve (M3.1 convention).
///
/// Muzzle velocity, not impact speed: D1 declares the prior on the MUZZLE
/// velocity (chronograph/envelope). The impact speed consistent with a drawn
/// muzzle velocity is found by <see cref="BackwardSolver.SolveAtMuzzleVelocity"/>
/// (a monotone 1-D root find), so every hypothesis is an exact prior draw -
/// importance weights stay 1 and the cloud needs no reweighting.
/// </summary>
public readonly record struct ShotHypothesis(
    double MuzzleVelocityMps,
    double ExteriorAngleDeg,
    double AzimuthDeg,
    double BcLbIn2,
    double WindEastMps,
    double WindNorthMps);

/// <summary>
/// Prior set for the inverse sampler. Angle/BC/wind priors are centred on the
/// measured value (the caller builds e.g. <c>ScalarPrior.Normal(observedAngle, sigma)</c>);
/// the muzzle-velocity prior is the declared envelope (D1: worst-case MV
/// envelope - <c>Uniform(min, max)</c> or <c>Normal(mean, sigma)</c>).
/// </summary>
public sealed record InversePriors(
    ScalarPrior MuzzleVelocity,
    ScalarPrior ExteriorAngleDeg,
    ScalarPrior AzimuthDeg,
    ScalarPrior BcLbIn2,
    ScalarPrior WindEastMps,
    ScalarPrior WindNorthMps);

/// <summary>
/// Draws hypotheses from an <see cref="InversePriors"/> set. Deterministic per
/// seed (see <see cref="Xoshiro256"/>); the seed is recorded in the case report
/// so a run is exactly reproducible.
/// </summary>
public sealed class HypothesisSampler
{
    private readonly Xoshiro256 _rng;

    public HypothesisSampler(ulong seed)
    {
        _rng = new Xoshiro256(seed);
    }

    /// <summary>Draw the next hypothesis. Azimuth is wrapped to [0, 360).</summary>
    public ShotHypothesis Next(InversePriors priors)
    {
        ArgumentNullException.ThrowIfNull(priors);

        double az = priors.AzimuthDeg.Sample(_rng) % 360.0;
        if (az < 0) az += 360.0;

        return new ShotHypothesis(
            priors.MuzzleVelocity.Sample(_rng),
            priors.ExteriorAngleDeg.Sample(_rng),
            az,
            priors.BcLbIn2.Sample(_rng),
            priors.WindEastMps.Sample(_rng),
            priors.WindNorthMps.Sample(_rng));
    }
}
