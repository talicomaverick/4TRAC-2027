using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Flat;

/// <summary>
/// Scene inputs for the flat model. The impact point is the frame origin
/// (0, 0, 0); <see cref="OriginHeightM"/> is the origin plane height relative to
/// the impact plane (negative when the shooter is below the roof).
/// </summary>
public sealed record FlatScene(
    double LatDeg,
    double OriginHeightM,
    Atmosphere Atmosphere,
    bool Coriolis,
    IFlatSurface? Surface = null);

/// <summary>Scene observables at the impact point, angles in degrees.</summary>
public sealed record FlatObservation(double ExteriorAngleDeg, double AzimuthDeg);

/// <summary>Numerical and envelope settings. The envelope is a rejection rule, never a clamp.</summary>
public sealed class FlatSolveOptions
{
    /// <summary>Fixed RK4 step (s).</summary>
    public double Dt { get; init; } = 0.001;

    /// <summary>Backward flight-time limit (s).</summary>
    public double MaxTimeS { get; init; } = 180.0;

    /// <summary>Declared validity envelope: maximum horizontal impact-to-origin range (m).</summary>
    public double EnvelopeM { get; init; } = 10_000.0;

    /// <summary>
    /// Muzzle-velocity root-find tolerance (m/s). 1e-3 m/s maps to about 1 cm of
    /// origin range at the 10 km envelope's sensitivity (~12 m per m/s at 4 km).
    /// </summary>
    public double MvToleranceMps { get; init; } = 1e-3;

    /// <summary>
    /// Hard cap on backward integrations per root find. Bisection to the
    /// tolerance over the full bracket needs about 25 steps in the worst case;
    /// a shorter cap silently discards valid hypotheses (measured: 25 % of a
    /// steep-impact cloud at 16).
    /// </summary>
    public int MaxEvaluations { get; init; } = 80;
}

/// <summary>
/// The origin implied by one hypothesis. Positions are in the impact frame
/// (metres east, north, and height relative to the impact plane). Launch angles
/// are in the flat frame, which is the aim direction.
/// </summary>
public sealed record FlatOrigin(
    bool Converged,
    double EastM,
    double NorthM,
    double HeightM,
    double MuzzleVelocityMps,
    double LaunchElevationDeg,
    double LaunchAzimuthDeg,
    double TimeOfFlightS,
    double SpinDriftM,
    FlatBackwardEnd EndReason,
    int Evaluations);

/// <summary>
/// Muzzle-velocity-consistent backward solve in the flat model.
///
/// The declared prior lives at the muzzle, but the integration is parameterised
/// by impact speed. The implied muzzle speed rises monotonically with impact
/// speed, so a bracketed Illinois root find (with a periodic bisection step)
/// returns the impact speed that reproduces the drawn muzzle velocity exactly.
/// Every hypothesis is therefore an exact prior draw, and weights are 1.
///
/// Spin drift (when enabled) is removed from the observed impact before the
/// backward solve: the observed impact equals the drift-free impact displaced
/// by D(T) to the right, and the observed velocity equals the drift-free
/// velocity plus the lateral velocity D'(T). T is the drift-free time of flight.
/// </summary>
public static class FlatSolver
{
    public static FlatOrigin SolveAtMuzzleVelocity(
        FlatObservation observation,
        double muzzleVelocityMps,
        IDragModel drag,
        FlatScene scene,
        double windEastMps,
        double windNorthMps,
        FlatSpin? spin = null,
        FlatSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(drag);
        ArgumentNullException.ThrowIfNull(scene);
        if (!(muzzleVelocityMps > 0))
            throw new ArgumentOutOfRangeException(nameof(muzzleVelocityMps), "Muzzle velocity must be positive.");

        var o = options ?? new FlatSolveOptions();
        var model = new FlatTrajectoryModel(drag, scene.Atmosphere, scene.LatDeg, windEastMps, windNorthMps, scene.Coriolis);
        double sg = spin is null ? 0.0 : SpinDrift.StabilityFactor(spin, muzzleVelocityMps);

        int evals = 0;

        (double F, Trial T) Eval(double impactSpeed)
        {
            evals++;
            var t = RunTrial(observation, impactSpeed, model, scene, o, spin, sg);
            double f = t.Backward.EndReason switch
            {
                FlatBackwardEnd.ReachedOriginPlane => t.Backward.Velocity.Length - muzzleVelocityMps,
                FlatBackwardEnd.MinSpeed => -muzzleVelocityMps,
                _ => muzzleVelocityMps,
            };
            return (f, t);
        }

        double a = 0.2 * muzzleVelocityMps;
        double b = muzzleVelocityMps;
        var (fa, _) = Eval(a);
        var (fb, lastTrial) = Eval(b);
        for (int i = 0; i < 8 && fa > 0; i++) { a *= 0.5; (fa, lastTrial) = Eval(a); }
        for (int i = 0; i < 8 && fb < 0; i++) { b += 0.1 * muzzleVelocityMps; (fb, lastTrial) = Eval(b); }

        if (double.IsNaN(fa) || double.IsNaN(fb) || fa > 0 || fb < 0)
            return ToOrigin(lastTrial, false, evals);

        bool lastMovedB = false;
        Trial trialC = lastTrial;
        double fc = fb;
        for (int it = 0; evals < o.MaxEvaluations; it++)
        {
            double c = (it % 4 == 3) ? 0.5 * (a + b) : b - fb * (b - a) / (fb - fa);
            (fc, trialC) = Eval(c);

            if (Math.Abs(fc) < o.MvToleranceMps)
                return ToOrigin(trialC, trialC.Backward.EndReason == FlatBackwardEnd.ReachedOriginPlane, evals);

            if (fc > 0)
            {
                if (lastMovedB) fa *= 0.5;
                b = c; fb = fc; lastMovedB = true;
            }
            else
            {
                if (!lastMovedB) fb *= 0.5;
                a = c; fa = fc; lastMovedB = false;
            }

            if (b - a < 1e-12 * muzzleVelocityMps)
            {
                // The bracket has collapsed. This is only a solution when the residual
                // is within tolerance. If f jumps across the bracket (a TimeLimit or
                // MinSpeed end returns +/-MV), the collapse is onto a discontinuity and
                // the implied muzzle velocity is NOT the drawn one: not converged.
                bool onRoot = Math.Abs(fc) < o.MvToleranceMps
                    && trialC.Backward.EndReason == FlatBackwardEnd.ReachedOriginPlane;
                return ToOrigin(trialC, onRoot, evals);
            }
        }

        return ToOrigin(trialC, Math.Abs(fc) < o.MvToleranceMps && trialC.Backward.EndReason == FlatBackwardEnd.ReachedOriginPlane, evals);
    }

    private readonly record struct Trial(FlatBackwardResult Backward, double SpinDriftM);

    private static Trial RunTrial(
        FlatObservation observation,
        double impactSpeed,
        FlatTrajectoryModel model,
        FlatScene scene,
        FlatSolveOptions o,
        FlatSpin? spin,
        double sg)
    {
        var vObs = EnuFrame.DirectionFromAzimuthElevation(observation.AzimuthDeg, -observation.ExteriorAngleDeg) * impactSpeed;

        if (spin is null)
        {
            var nd = FlatIntegrators.Backward(model, Cartesian3.Zero, vObs, scene.OriginHeightM, o.Dt, o.MaxTimeS, surface: scene.Surface);
            return new Trial(nd, 0.0);
        }

        var first = FlatIntegrators.Backward(model, Cartesian3.Zero, vObs, scene.OriginHeightM, o.Dt, o.MaxTimeS, surface: scene.Surface);
        if (first.EndReason != FlatBackwardEnd.ReachedOriginPlane)
            return new Trial(first, 0.0);

        // Drift depends on the DRIFT-FREE time of flight, which the first pass
        // (run on the observed, drift-carrying velocity) only approximates:
        // lateral velocity feeds Coriolis, shifting the time of flight by
        // milliseconds at long range. Iterate until the time of flight is stable.
        var right = SpinDrift.RightOf(observation.AzimuthDeg);
        double tof = first.TimeOfFlightS;
        Trial trial = new(first, 0.0);
        for (int pass = 0; pass < 6; pass++)
        {
            double drift = SpinDrift.DisplacementM(sg, tof, spin.RightHandTwist);
            double lateralV = SpinDrift.LateralVelocityMps(sg, tof, spin.RightHandTwist);
            var corrected = FlatIntegrators.Backward(
                model, right * (-drift), vObs - right * lateralV, scene.OriginHeightM, o.Dt, o.MaxTimeS, surface: scene.Surface);
            trial = new Trial(corrected, drift);
            if (corrected.EndReason != FlatBackwardEnd.ReachedOriginPlane)
                break;
            if (Math.Abs(corrected.TimeOfFlightS - tof) < 1e-7)
                break;
            tof = corrected.TimeOfFlightS;
        }
        return trial;
    }

    private static FlatOrigin ToOrigin(Trial t, bool converged, int evals)
    {
        var r = t.Backward;
        return new FlatOrigin(
            converged,
            r.Position.X,
            r.Position.Y,
            r.Position.Z,
            r.Velocity.Length,
            EnuFrame.ElevationDeg(r.Velocity),
            EnuFrame.AzimuthDeg(r.Velocity),
            r.TimeOfFlightS,
            t.SpinDriftM,
            r.EndReason,
            evals);
    }
}
