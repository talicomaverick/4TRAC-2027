using FourTrac.Core.Atm;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;
using FourTrac.Core.Solver;

namespace FourTrac.Core.Inverse;

/// <summary>
/// The forensic scene observable at the impact point (PLAN.md §5 scene panel).
/// Angles are exterior (departure) quantities measured IN THE SCENE's local
/// ENU frame at the impact point: the exterior angle is the descent below the
/// horizontal at the hole (positive = descending), and the azimuth is the
/// horizontal travel direction of the bullet (deg clockwise from true north).
/// </summary>
public sealed record ImpactObservation(
    Cartesian3 PositionEcef,
    double ExteriorAngleDeg,
    double VelocityAzimuthDeg,
    double ImpactSpeedMps);

/// <summary>Options for backward (time-reversed) trajectory integration.</summary>
public sealed class BackwardOptions
{
    /// <summary>Fixed RK4 step magnitude (s); integration runs with a negative step.</summary>
    public double Dt { get; set; } = 0.001;

    /// <summary>Hard backward flight-time limit (s).</summary>
    public double MaxTimeS { get; set; } = 60.0;

    /// <summary>Origin elevation plane (m above the ellipsoid): the terrain surface or a user override (PLAN.md §5 advanced panel).</summary>
    public double OriginHeightM { get; set; }

    /// <summary>Stop if speed drops below this (m/s).</summary>
    public double MinSpeedMps { get; set; } = 1.0;
}

/// <summary>Why the backward integration stopped.</summary>
public enum BackwardEndReason
{
    /// <summary>Crossed the origin plane on the launch branch (the origin was found).</summary>
    ReachedOriginPlane,

    /// <summary>Exceeded the maximum backward flight time without a valid crossing.</summary>
    TimeLimit,

    /// <summary>Speed fell below the minimum.</summary>
    MinSpeed,
}

/// <summary>Raw result of a backward trajectory integration.</summary>
public sealed record BackwardResult(
    BackwardEndReason EndReason,
    double TimeOfFlightS,
    Cartesian3 FinalPosition,
    Cartesian3 FinalVelocity,
    double FinalSpeed,
    double FinalHeightM);

/// <summary>
/// The hypothesized origin implied by one impact hypothesis: where on the
/// origin plane the bullet came from, and with what launch state.
/// Launch angles are reported in the ENU frame anchored at the ORIGIN itself
/// (what the shooter actually aimed), unlike the impact observables, which are
/// scene-local at the impact point.
/// </summary>
public sealed record OriginEstimate(
    Cartesian3 OriginEcef,
    double OriginLatDeg,
    double OriginLonDeg,
    double OriginHeightM,
    double ImpliedMuzzleSpeedMps,
    double LaunchElevationDeg,
    double LaunchAzimuthDeg,
    double TimeOfFlightS,
    BackwardEndReason EndReason);

/// <summary>
/// Backward (time-reversed) RK4 trajectory integration with origin-plane
/// crossing detection.
///
/// Time reversal (the correctness core): every term of the EOM - gravity,
/// drag against the air-relative velocity, Coriolis -2 x v - is a function of
/// the state only, so integrating the SAME <see cref="TrajectoryModel"/> with
/// a negative step from the impact state re-traces the forward states
/// exactly. No sign is flipped anywhere, and at the plane crossing the state
/// velocity IS the launch velocity (its speed = implied muzzle velocity).
///
/// Crossing rule: the origin is the first crossing of the origin plane at
/// which the ENU vertical velocity is POSITIVE - a lofted launch leaves the
/// plane ascending. The impact start (descending) and the far-side mirror
/// crossing (descending) both have v_up &lt; 0 and never fire. Downhill-launch
/// origins (v_up &lt; 0 at the origin) are outside this rule and are an
/// M3.2+ item together with terrain-following origin planes (PLAN.md §8).
///
/// Frame convention: the scene ENU frame is anchored at the IMPACT point
/// (the only known position); the wind profile resolves through it, which
/// differs from an origin-anchored resolution by the local frame rotation
/// (d/r_earth - sub-mm at forensic ranges, itemized in the M3 notes).
/// </summary>
public sealed class BackwardTrajectoryEngine
{
    public BackwardResult Integrate(
        TrajectoryModel model, Cartesian3 startPositionEcef, Cartesian3 startVelocityEcef,
        EnuFrame scene, BackwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scene);

        var o = options ?? new BackwardOptions();
        if (o.Dt <= 0) throw new ArgumentOutOfRangeException(nameof(o), "Dt must be positive.");
        if (o.MaxTimeS <= 0) throw new ArgumentOutOfRangeException(nameof(o), "MaxTimeS must be positive.");

        int n = model.Dimension;
        var y = new double[n];
        var yprev = new double[n];
        var k1 = new double[n]; var k2 = new double[n]; var k3 = new double[n]; var k4 = new double[n];
        var ytmp = new double[n]; var yout = new double[n];

        y[0] = startPositionEcef.X; y[1] = startPositionEcef.Y; y[2] = startPositionEcef.Z;
        y[3] = startVelocityEcef.X; y[4] = startVelocityEcef.Y; y[5] = startVelocityEcef.Z;

        double elapsed = 0.0;
        double hPrev = Geodetic.Height(startPositionEcef) - o.OriginHeightM;

        while (true)
        {
            if (elapsed >= o.MaxTimeS)
                return Finish(BackwardEndReason.TimeLimit, elapsed, y, model);

            double speed = Math.Sqrt(y[3] * y[3] + y[4] * y[4] + y[5] * y[5]);
            if (speed < o.MinSpeedMps)
                return Finish(BackwardEndReason.MinSpeed, elapsed, y, model);

            Array.Copy(y, yprev, n);

            double hStep = -Math.Min(o.Dt, o.MaxTimeS - elapsed); // negative; never overshoot MaxTimeS
            Rk4.Step(model, -elapsed, y, hStep, k1, k2, k3, k4, ytmp, yout);

            double hNow = Geodetic.Height(new Cartesian3(yout[0], yout[1], yout[2])) - o.OriginHeightM;

            // Plane crossing inside this step (either direction). A start
            // exactly on the plane (hPrev == 0, hNow > 0) is not a crossing -
            // correct, since the start is the impact, not the origin.
            if (hPrev * hNow < 0.0 || hNow == 0.0)
            {
                // Refine the crossing state by bisection on the step fraction.
                double lo = 0.0, hi = hStep;
                for (int i = 0; i < 40 && Math.Abs(hi - lo) > 1e-12; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    Rk4.Step(model, -elapsed, yprev, mid, k1, k2, k3, k4, ytmp, yout);
                    double hm = Geodetic.Height(new Cartesian3(yout[0], yout[1], yout[2])) - o.OriginHeightM;
                    if (hm * hPrev <= 0.0) hi = mid;
                    else lo = mid;
                }
                Rk4.Step(model, -elapsed, yprev, lo, k1, k2, k3, k4, ytmp, yout);

                var crossVel = new Cartesian3(yout[3], yout[4], yout[5]);
                if (scene.VectorToEnu(crossVel).Z > 0.0)
                {
                    // Ascending through the plane = the lofted launch = the origin.
                    Array.Copy(yout, y, n);
                    elapsed += Math.Abs(lo);
                    return Finish(BackwardEndReason.ReachedOriginPlane, elapsed, y, model);
                }

                // Descending mirror crossing - not the origin; keep going.
            }

            Array.Copy(yout, y, n);
            elapsed += Math.Abs(hStep);
            hPrev = hNow;
        }
    }

    private static BackwardResult Finish(BackwardEndReason reason, double t, double[] y, TrajectoryModel model)
    {
        var r = new Cartesian3(y[0], y[1], y[2]);
        var v = new Cartesian3(y[3], y[4], y[5]);
        return new BackwardResult(reason, t, r, v, v.Length, Geodetic.Height(r));
    }
}

/// <summary>
/// Backward solve: given impact observables (the forensic scene), a bullet /
/// atmosphere / wind hypothesis, integrate the time-reversed trajectory to
/// the origin plane and report the hypothesized origin.
///
/// Wind conventions (two overloads, one physical meaning):
/// A wind vector is a property of the moving air mass, not of the observer's
/// ENU axes. The measured wind (speed + FROM-direction against local
/// north/horizontal at the crime scene) should therefore be resolved ONCE to
/// ECEF at the scene anchor and held fixed - the <c>windEcef</c> overload is
/// that convention, and is what the V2 noise-free identity gate uses. The
/// <c>WindProfile</c> overload re-resolves the profile through the
/// IMPACT-anchored frame per evaluation; relative to an origin-anchored
/// resolution (what the forward solver uses) the frames differ by the local
/// rotation d/r_earth, which imprints a spurious VERTICAL wind of order
/// w*d/r (~1 mm/s at 5 m/s and 1 km). Shallow arcs amplify vertical air
/// motion strongly, so this ambiguity costs up to ~cm of origin at forensic
/// ranges - quantified by InverseTests.Backward_WindFrameRotationTerm, never
/// silently absorbed.
/// </summary>
public static class BackwardSolver
{
    /// <summary>Wind as a layered profile, resolved through the impact-anchored scene frame (see class doc for the frame caveat).</summary>
    public static OriginEstimate Solve(
        ImpactObservation observation,
        BulletProfile bullet,
        Atmosphere atmosphere,
        WindProfile wind,
        bool includeCoriolis,
        BackwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(bullet);
        ArgumentNullException.ThrowIfNull(atmosphere);
        ArgumentNullException.ThrowIfNull(wind);

        ValidateImpactSpeed(observation);

        // Scene ENU frame at the impact point: the observables are scene-local
        // measurements (compass and inclinometer at the hole).
        var impactFrame = FrameAtImpact(observation);

        var model = new TrajectoryModel(
            bullet.CreateDragModel(),
            atmosphere,
            wind,
            impactFrame,
            includeDrag: true,
            includeCoriolis: includeCoriolis);

        return SolveCore(observation, observation.ImpactSpeedMps, model, impactFrame, options);
    }

    /// <summary>
    /// Wind as a fixed ECEF vector (m/s, air-motion direction), resolved once
    /// by the caller at the scene anchor - the exact-convention overload used
    /// by the V2 identity gate and the validation harness.
    /// </summary>
    public static OriginEstimate Solve(
        ImpactObservation observation,
        BulletProfile bullet,
        Atmosphere atmosphere,
        Cartesian3 windEcef,
        bool includeCoriolis,
        BackwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(bullet);
        ArgumentNullException.ThrowIfNull(atmosphere);

        ValidateImpactSpeed(observation);

        var impactFrame = FrameAtImpact(observation);

        var model = new TrajectoryModel(
            bullet.CreateDragModel(),
            atmosphere,
            windEcef,
            includeDrag: true,
            includeCoriolis: includeCoriolis);

        return SolveCore(observation, observation.ImpactSpeedMps, model, impactFrame, options);
    }

    /// <summary>
    /// Result of the muzzle-velocity consistency solve: the origin implied by a
    /// hypothesis whose MUZZLE velocity matches the declared prior draw.
    /// </summary>
    /// <param name="Origin">Origin hypothesis (plane crossing).</param>
    /// <param name="ImpactSpeedMps">Converged impact speed that realises the muzzle velocity.</param>
    /// <param name="ImpliedMuzzleVelocityMps">Backward-implied muzzle speed at convergence (≈ the target).</param>
    /// <param name="Evaluations">Full backward integrations consumed (bracket + iterations).</param>
    /// <param name="Converged">False if the bracket or tolerance could not be met within <c>maxEvaluations</c>.</param>
    public sealed record MuzzleConsistentSolve(
        OriginEstimate Origin,
        double ImpactSpeedMps,
        double ImpliedMuzzleVelocityMps,
        int Evaluations,
        bool Converged);

    /// <summary>
    /// Find the impact speed whose backward-implied muzzle velocity equals
    /// <paramref name="muzzleVelocityMps"/>, and return the corresponding origin.
    ///
    /// Why a root find: the backward engine is parameterised by the IMPACT
    /// speed (the known scene state), but the declared prior (D1) lives at the
    /// MUZZLE. The map impact → implied muzzle speed is continuous and strictly
    /// increasing along the hypothesis, with f(v) = impliedMV(v) − target &gt; 0
    /// at v = target (backward integration only accelerates toward the launch),
    /// so a bracketed Illinois false-position iteration converges super-linearly
    /// in a handful of full integrations. Exactness matters: the alternative -
    /// sampling impact speed and importance-weighting by the MV prior - needs
    /// the Jacobian |dMV/dv_impact| (O(1)); an estimated Jacobian would bias the
    /// cloud. Here every hypothesis is an exact prior draw: weights are 1 by
    /// construction, no reweighting anywhere (PLAN.md §8.3).
    ///
    /// <paramref name="observation"/>.ImpactSpeedMps is ignored: the solve
    /// determines the impact speed from the muzzle-velocity target.
    /// </summary>
    public static MuzzleConsistentSolve SolveAtMuzzleVelocity(
        ImpactObservation observation,
        double muzzleVelocityMps,
        BulletProfile bullet,
        Atmosphere atmosphere,
        Cartesian3 windEcef,
        bool includeCoriolis,
        BackwardOptions? options = null,
        double mvToleranceMps = 1e-4,
        int maxEvaluations = 16)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(bullet);
        ArgumentNullException.ThrowIfNull(atmosphere);
        if (muzzleVelocityMps <= 0)
            throw new ArgumentOutOfRangeException(nameof(muzzleVelocityMps), "Muzzle velocity must be positive.");
        if (mvToleranceMps <= 0)
            throw new ArgumentOutOfRangeException(nameof(mvToleranceMps), "Tolerance must be positive.");
        if (maxEvaluations < 4)
            throw new ArgumentOutOfRangeException(nameof(maxEvaluations), "At least 4 evaluations are required.");

        var impactFrame = FrameAtImpact(observation);
        var model = new TrajectoryModel(
            bullet.CreateDragModel(),
            atmosphere,
            windEcef,
            includeDrag: true,
            includeCoriolis: includeCoriolis);
        var opts = options ?? new BackwardOptions();

        int evals = 0;
        double lastV = 0.0;

        // One bracketed trial. f(v) = impliedMV(v) − target. A MinSpeed stop
        // means the integration died before any launch was seen: report the
        // implied MV as far below target so the bracket moves up (larger
        // impact speed). A TimeLimit stop means the lobe took too long (very
        // slow, long-TOF trial): report far above target so the bracket moves
        // down. Both are edge guards - sane scenes converge via the plane
        // crossing.
        (double F, OriginEstimate Est) Eval(double v)
        {
            evals++;
            lastV = v;
            var est = SolveCore(observation, v, model, impactFrame, opts);
            return est.EndReason switch
            {
                BackwardEndReason.ReachedOriginPlane => (est.ImpliedMuzzleSpeedMps - muzzleVelocityMps, est),
                BackwardEndReason.MinSpeed => (-muzzleVelocityMps, est),
                _ => (muzzleVelocityMps, est),
            };
        }

        // Bracket: a - where f < 0, b - where f > 0. At b = target the implied
        // muzzle speed always exceeds the target (backward drag only adds
        // speed), so b is a valid upper end for any sane scene; a starts at
        // 20 % and halves until f(a) < 0 (retention below ~20 % would need it).
        double a = 0.2 * muzzleVelocityMps;
        double b = muzzleVelocityMps;
        var (fa, _) = Eval(a);
        var (fb, lastEst) = Eval(b);
        for (int i = 0; i < 8 && fa > 0; i++) { a *= 0.5; (fa, lastEst) = Eval(a); }
        for (int i = 0; i < 8 && fb < 0; i++) { b += 0.1 * muzzleVelocityMps; (fb, lastEst) = Eval(b); }

        if (double.IsNaN(fa) || double.IsNaN(fb) || fa > 0 || fb < 0)
            return new MuzzleConsistentSolve(lastEst, lastV, lastEst.ImpliedMuzzleSpeedMps, evals, false);

        // Illinois false position (bracketed secant with side damping): when
        // the SAME side moves twice in a row, the stale opposite function
        // value is halved so the iterate cannot stagnate at a curved end.
        bool lastMovedB = false;
        double c = 0.5 * (a + b);
        OriginEstimate estC = lastEst;
        double fc = 0;
        for (int it = 0; evals < maxEvaluations; it++)
        {
            // Periodic bisection step: guarantees bracket contraction even if
            // false position oscillates (insurance against pathological f).
            c = (it % 4 == 3)
                ? 0.5 * (a + b)
                : b - fb * (b - a) / (fb - fa);
            (fc, estC) = Eval(c);

            if (Math.Abs(fc) < mvToleranceMps)
                return new MuzzleConsistentSolve(estC, c, estC.ImpliedMuzzleSpeedMps, evals, true);

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
                return new MuzzleConsistentSolve(estC, c, estC.ImpliedMuzzleSpeedMps, evals, true);
        }

        return new MuzzleConsistentSolve(estC, c, estC.ImpliedMuzzleSpeedMps, evals, Math.Abs(fc) < mvToleranceMps);
    }

    /// <summary>
    /// Scene ENU frame anchored at the impact point - the frame the impact
    /// observables are measured in, and the frame the wind measurement is
    /// resolved through. Public: the cloud driver resolves drawn wind
    /// components and reports origin offsets in the same frame.
    /// </summary>
    public static EnuFrame FrameAtImpact(ImpactObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Geodetic.EcefToLla(observation.PositionEcef, out double latDeg, out double lonDeg, out double heightM);
        return EnuFrame.AtOrigin(latDeg, lonDeg, heightM);
    }

    private static void ValidateImpactSpeed(ImpactObservation observation)
    {
        if (observation.ImpactSpeedMps <= 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "Impact speed must be positive.");
    }

    private static OriginEstimate SolveCore(
        ImpactObservation observation, double impactSpeedMps, TrajectoryModel model, EnuFrame impactFrame, BackwardOptions? options)
    {
        // Travel direction at impact = descending: elevation = -exterior angle.
        var dirEnu = EnuFrame.DirectionFromAzimuthElevation(
            observation.VelocityAzimuthDeg, -observation.ExteriorAngleDeg);
        var startVelocity = impactFrame.VectorToEcef(dirEnu) * impactSpeedMps;

        var engine = new BackwardTrajectoryEngine();
        var result = engine.Integrate(model, observation.PositionEcef, startVelocity, impactFrame, options);

        // Report launch angles in the origin's own ENU frame (the aim direction).
        Geodetic.EcefToLla(result.FinalPosition, out double oLat, out double oLon, out double oH);
        var originFrame = EnuFrame.AtOrigin(oLat, oLon, oH);
        var launchEnu = originFrame.VectorToEnu(result.FinalVelocity);

        return new OriginEstimate(
            result.FinalPosition,
            oLat,
            oLon,
            result.FinalHeightM,
            result.FinalSpeed,
            EnuFrame.ElevationDeg(launchEnu),
            EnuFrame.AzimuthDeg(launchEnu),
            result.TimeOfFlightS,
            result.EndReason);
    }
}
