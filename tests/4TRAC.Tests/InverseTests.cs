using FourTrac.Core.Atm;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using FourTrac.Core.Solver;
using FourTrac.Core.Stats;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// M3.1/M3.2 (PLAN.md §8): inverse scaffold - priors, deterministic sampler,
/// the backward (time-reversed) engine, the muzzle-velocity consistency
/// root-find, and the Monte-Carlo origin cloud.
///
/// The noise-free identity test is the convention gate: hand the inverse the
/// EXACT impact observables of a known forward scene (sigma -> 0) and it must
/// return the exact origin. Any sign, direction or time-reversal error
/// (drag against the reversed velocity, Coriolis under time reversal, wind
/// FROM-vs-TO, exterior-vs-launch angle) breaks it by meters or by whole
/// degrees - orders of magnitude above the tolerances below.
///
/// Observable frame convention (itemized, not implicit): the exterior angle
/// and travel azimuth are defined IN THE SCENE ENU FRAME ANCHORED AT THE
/// IMPACT POINT - that is where the crime scene is measured (compass and
/// inclinometer at the hole). The test therefore projects the forward impact
/// velocity onto the impact-anchored frame, not the origin-anchored frame the
/// forward solver uses for its drop-chart observables. Launch angles come back
/// in the origin-anchored frame (the aim direction).
///
/// Wind convention: the gate passes the wind as a fixed ECEF vector (the air
/// mass resolved once at the scene anchor) - the exact convention that makes
/// the round trip machine-precise. Resolving the same wind through the
/// impact-anchored frame instead imprints a spurious vertical wind of order
/// w*d/r_earth (the two ENU frames differ by that rotation); shallow arcs
/// amplify vertical air motion strongly, so that ambiguity is separately
/// measured and bounded in Backward_WindFrameRotationTerm_IsQuantified.
/// </summary>
public class InverseTests
{
    // (latDeg, launchElDeg, azDeg, mvMps, bc, windMps, windFromDeg, coriolis)
    // Ranges are kept at forensic scale.
    [Theory]
    [InlineData(45.0, 2.0, 30.0, 850.0, 0.40, 0.0, 0.0, false)]  // calm baseline, no Coriolis
    [InlineData(45.0, 1.0, 120.0, 850.0, 0.40, 5.0, 240.0, false)] // crosswind, no Coriolis
    [InlineData(45.0, 1.0, 120.0, 850.0, 0.40, 5.0, 240.0, true)]  // crosswind + Coriolis
    [InlineData(60.0, 1.5, 340.0, 700.0, 0.35, 3.0, 90.0, true)]   // high latitude + Coriolis
    [InlineData(-35.0, 2.0, 95.0, 900.0, 0.45, 0.0, 0.0, true)]    // southern hemisphere (Coriolis sign flip)
    [InlineData(45.0, 10.0, 200.0, 350.0, 0.20, 0.0, 0.0, true)]   // steep pistol-style lob (long TOF)
    public void Backward_NoiseFreeIdentity_RecoversOrigin(
        double latDeg, double launchElDeg, double azDeg, double mvMps, double bc,
        double windMps, double windFromDeg, bool coriolis)
    {
        const double lonDeg = 7.5;
        const double launchHeight = 123.0; // above the ellipsoid - exercises the height-anchor math

        var bullet = ScalarBullet(bc);
        var atm = Atmosphere.IsaAtElevation(launchHeight);
        var wind = windMps > 0 ? WindProfile.Constant(windMps, windFromDeg) : WindProfile.Calm;

        // ---- Forward: simulate the known scene, flat (impact on the launch plane).
        var forward = ForwardSolver.Solve(new ShotScenario
        {
            LatitudeDeg = latDeg,
            LongitudeDeg = lonDeg,
            HeightM = launchHeight,
            AzimuthDeg = azDeg,
            LaunchElevationDeg = launchElDeg,
            MuzzleVelocityMps = mvMps,
            Bullet = bullet,
            Atmosphere = atm,
            Wind = wind,
            IncludeCoriolis = coriolis,
            TargetHeightM = launchHeight,
            MaxTimeS = 60.0,
        });
        Assert.Equal(TrajectoryEndReason.GroundHit, forward.Trajectory.EndReason);
        Assert.True(forward.DownRangeM > 200, $"Degenerate scene: downrange {forward.DownRangeM} m.");

        // ---- The sigma -> 0 observation: exact impact observables, scene-local
        //      at the impact point (see class doc for the frame convention).
        var impactPos = forward.Trajectory.FinalPosition;
        Geodetic.EcefToLla(impactPos, out double iLat, out double iLon, out double iH);
        var impactFrame = EnuFrame.AtOrigin(iLat, iLon, iH);
        var impactVelEnu = impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity);
        var observation = new ImpactObservation(
            PositionEcef: impactPos,
            ExteriorAngleDeg: -EnuFrame.ElevationDeg(impactVelEnu),
            VelocityAzimuthDeg: EnuFrame.AzimuthDeg(impactVelEnu),
            ImpactSpeedMps: forward.Trajectory.FinalSpeed);

        // The air vector the forward run actually flew through (constant profile
        // -> one fixed ECEF vector; resolved in the forward's scene frame).
        var windEcef = wind.WindVectorEcef(0.0, forward.Scene);

        // ---- Backward: recover the origin from the observables alone.
        var origin = BackwardSolver.Solve(
            observation, bullet, atm, windEcef, coriolis,
            new BackwardOptions { OriginHeightM = launchHeight });

        Assert.Equal(BackwardEndReason.ReachedOriginPlane, origin.EndReason);

        double posErrM = (origin.OriginEcef - forward.Scene.OriginEcef).Length;
        double mvErr = Math.Abs(origin.ImpliedMuzzleSpeedMps - mvMps);
        double tofErr = Math.Abs(origin.TimeOfFlightS - forward.TimeOfFlightS);
        double azErr = Math.Abs(((origin.LaunchAzimuthDeg - azDeg + 540.0) % 360.0) - 180.0);
        double elErr = Math.Abs(origin.LaunchElevationDeg - launchElDeg);

        // Tolerances: RK4 +/- h asymmetry + ECEF<->LLA precision are sub-mm;
        // the only model-level term is the wind frame rotation (mm at these
        // ranges). A convention bug (mirrored wind, flipped Coriolis, sign
        // error in the exterior angle) lands at 0.1 m .. km.
        Assert.True(posErrM <= 5e-3, $"Origin position error {posErrM * 1e3:F4} mm (tol 5 mm).");
        Assert.True(mvErr <= 1e-2, $"Implied muzzle velocity error {mvErr:F5} m/s (tol 0.01).");
        Assert.True(tofErr <= 1e-3, $"Time-of-flight error {tofErr * 1e3:F4} ms (tol 1 ms).");
        Assert.True(azErr <= 1e-4, $"Launch azimuth error {azErr:F6} deg (tol 1e-4 deg).");
        Assert.True(elErr <= 1e-4, $"Launch elevation error {elErr:F6} deg (tol 1e-4 deg).");
    }

    /// <summary>
    /// Quantifies - does not hide - the ENU-frame rotation wind term. The
    /// forward solver resolves the wind through the launch-point frame, the
    /// profile-based inverse through the impact-point frame; the frames differ
    /// by the local rotation d/r_earth, which converts part of a HORIZONTAL
    /// wind into a spurious VERTICAL wind of ~w*d/r (~0.85 mm/s at 5 m/s and
    /// 1.1 km here). Shallow arcs amplify vertical air motion strongly
    /// (measured sensitivity ~30-70 x dw*TOF, vs ~3-5 for horizontal), so the
    /// profile-path identity at this scene costs ~2 cm.
    ///
    /// Physical reading: this is a wind-meter-location ambiguity of the scene
    /// measurement (<= 1 mm/s of vertical wind) - far below any real wind
    /// measurement sigma - not a solver defect. The ECEF-wind overload (the
    /// measured air vector resolved once at the scene anchor) is the exact
    /// convention used by the V2 identity gate; this test pins that the term
    /// exists, stays inside its physical scale, and never contaminates the gate.
    /// </summary>
    [Fact]
    public void Backward_WindFrameRotationTerm_IsQuantified()
    {
        var bullet = ScalarBullet(0.40);
        var atm = Atmosphere.IsaAtElevation(123.0);
        var wind = WindProfile.Constant(5.0, 240.0);

        var forward = ForwardSolver.Solve(new ShotScenario
        {
            LatitudeDeg = 45.0, LongitudeDeg = 7.5, HeightM = 123.0,
            AzimuthDeg = 120.0, LaunchElevationDeg = 1.0,
            MuzzleVelocityMps = 850.0, Bullet = bullet, Atmosphere = atm,
            Wind = wind, IncludeCoriolis = false, TargetHeightM = 123.0,
        });
        Assert.Equal(TrajectoryEndReason.GroundHit, forward.Trajectory.EndReason);

        var impactPos = forward.Trajectory.FinalPosition;
        Geodetic.EcefToLla(impactPos, out double iLat, out double iLon, out double iH);
        var impactFrame = EnuFrame.AtOrigin(iLat, iLon, iH);
        var vEnu = impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity);
        var observation = new ImpactObservation(
            impactPos,
            -EnuFrame.ElevationDeg(vEnu),
            EnuFrame.AzimuthDeg(vEnu),
            forward.Trajectory.FinalSpeed);

        var opts = new BackwardOptions { OriginHeightM = 123.0 };

        // Exact convention (fixed ECEF wind): machine-precision identity.
        var originEcef = BackwardSolver.Solve(
            observation, bullet, atm, wind.WindVectorEcef(0.0, forward.Scene), false, opts);
        Assert.Equal(BackwardEndReason.ReachedOriginPlane, originEcef.EndReason);
        double errEcef = (originEcef.OriginEcef - forward.Scene.OriginEcef).Length;
        Assert.True(errEcef <= 1e-3, $"ECEF-wind identity should be sub-mm, was {errEcef * 1e3:F4} mm.");

        // Profile path (impact-frame resolution): the frame-rotation term.
        var originProfile = BackwardSolver.Solve(observation, bullet, atm, wind, false, opts);
        Assert.Equal(BackwardEndReason.ReachedOriginPlane, originProfile.EndReason);
        double errProfile = (originProfile.OriginEcef - forward.Scene.OriginEcef).Length;

        // Physical scale: kappa (<= 60, measured ~32 for the vertical component
        // at this scene) * frame rotation (d/r) * wind speed * TOF.
        double rho = forward.DownRangeM / 6378137.0;
        double bound = Math.Max(5e-3, 60.0 * rho * 5.0 * forward.TimeOfFlightS);
        Assert.True(errProfile <= bound,
            $"Frame-rotation term {errProfile * 1e3:F3} mm exceeds physical scale {bound * 1e3:F3} mm.");
        // And it is a real, nonzero term at windy scenes - published, not papered over.
        Assert.True(errProfile >= 1e-3, "Expected a measurable frame-rotation term with wind.");
    }

    /// <summary>
    /// Guard behaviour: an origin plane above the reachable apex must time out
    /// (never silently report a wrong crossing).
    /// </summary>
    [Fact]
    public void Backward_UnreachableOriginPlane_TimesOut()
    {
        var bullet = ScalarBullet(0.40);
        var atm = Atmosphere.IsaAtElevation(123.0);
        var forward = ForwardSolver.Solve(new ShotScenario
        {
            LatitudeDeg = 45.0,
            LongitudeDeg = 7.5,
            HeightM = 123.0,
            AzimuthDeg = 30.0,
            LaunchElevationDeg = 2.0,
            MuzzleVelocityMps = 850.0,
            Bullet = bullet,
            Atmosphere = atm,
            Wind = WindProfile.Calm,
            IncludeCoriolis = false,
            TargetHeightM = 123.0,
        });
        Assert.Equal(TrajectoryEndReason.GroundHit, forward.Trajectory.EndReason);

        var impactPos = forward.Trajectory.FinalPosition;
        Geodetic.EcefToLla(impactPos, out double iLat, out double iLon, out double iH);
        var impactFrame = EnuFrame.AtOrigin(iLat, iLon, iH);
        var observation = new ImpactObservation(
            impactPos,
            -EnuFrame.ElevationDeg(impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity)),
            EnuFrame.AzimuthDeg(impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity)),
            forward.Trajectory.FinalSpeed);

        // A +2 deg lob tops out ~45 m above the launch plane; put the origin
        // plane 4 km up - unreachable, so the engine must report TimeLimit.
        var origin = BackwardSolver.Solve(
            observation, bullet, atm, WindProfile.Calm, false,
            new BackwardOptions { OriginHeightM = 123.0 + 4000.0, MaxTimeS = 15.0 });

        Assert.Equal(BackwardEndReason.TimeLimit, origin.EndReason);
    }

    /// <summary>
    /// M3.2 muzzle-velocity consistency root-find: the declared prior lives at
    /// the MUZZLE (D1), so the solve must find the impact speed whose
    /// backward-implied MV matches the target. At the true MV it must return
    /// the same origin as the direct identity gate (the exact impact speed is
    /// the root by construction); off-truth targets must still converge with
    /// the implied MV on target - the map is monotone on both sides of truth.
    /// </summary>
    [Fact]
    public void Backward_AtMuzzleVelocity_RecoversOriginAndMv()
    {
        const double lonDeg = 7.5;
        const double launchHeight = 123.0;
        const double mvMps = 850.0;

        var bullet = ScalarBullet(0.40);
        var atm = Atmosphere.IsaAtElevation(launchHeight);

        var forward = ForwardSolver.Solve(new ShotScenario
        {
            LatitudeDeg = 45.0, LongitudeDeg = lonDeg, HeightM = launchHeight,
            AzimuthDeg = 30.0, LaunchElevationDeg = 2.0,
            MuzzleVelocityMps = mvMps, Bullet = bullet, Atmosphere = atm,
            Wind = WindProfile.Calm, IncludeCoriolis = false,
            TargetHeightM = launchHeight, MaxTimeS = 60.0,
        });
        Assert.Equal(TrajectoryEndReason.GroundHit, forward.Trajectory.EndReason);

        var impactPos = forward.Trajectory.FinalPosition;
        Geodetic.EcefToLla(impactPos, out double iLat, out double iLon, out double iH);
        var impactFrame = EnuFrame.AtOrigin(iLat, iLon, iH);
        var vEnu = impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity);
        var observation = new ImpactObservation(
            impactPos,
            -EnuFrame.ElevationDeg(vEnu),
            EnuFrame.AzimuthDeg(vEnu),
            forward.Trajectory.FinalSpeed);

        var opts = new BackwardOptions { OriginHeightM = launchHeight };

        foreach (double target in new[] { mvMps, mvMps - 5.0, mvMps + 5.0 })
        {
            var solve = BackwardSolver.SolveAtMuzzleVelocity(
                observation, target, bullet, atm,
                Cartesian3.Zero, // calm air
                includeCoriolis: false, opts);

            Assert.True(solve.Converged, $"MV target {target} did not converge after {solve.Evaluations} evaluations.");
            Assert.InRange(solve.Evaluations, 2, 16);
            Assert.True(Math.Abs(solve.ImpliedMuzzleVelocityMps - target) <= 1e-3,
                $"Implied MV {solve.ImpliedMuzzleVelocityMps:F5} vs target {target} (tol 1e-3).");

            double posErrM = (solve.Origin.OriginEcef - forward.Scene.OriginEcef).Length;
            if (target == mvMps)
            {
                // At the true MV the root IS the true impact speed: identity tolerance.
                Assert.True(posErrM <= 1e-3,
                    $"Identity point origin error {posErrM * 1e3:F4} mm (tol 1 mm).");
            }
            else
            {
                // Off-truth targets shift the origin by ~dO/dMV * 5 m/s (single
                // metres at these TOFs) - it must stay a sane near-trajectory
                // origin, not diverge or jump to another crossing branch.
                Assert.True(posErrM <= 25.0,
                    $"Target {target}: origin moved {posErrM:F2} m from truth (bound 25 m).");
            }
        }
    }

    /// <summary>
    /// The M3.2 Monte-Carlo cloud in the noise-free limit: with priors narrowed
    /// to (nearly) exact values, every hypothesis must collapse onto the true
    /// origin, report a converged muzzle-velocity match, place the cloud
    /// UPRANGE of the impact (bearing = azimuth + 180 deg - the E/N reporting
    /// frame convention), and reproduce bit-identically from the same seed
    /// (case-report reproducibility).
    /// </summary>
    [Fact]
    public void OriginCloud_NoiseFree_CollapsesAndIsDeterministic()
    {
        const double launchHeight = 123.0;
        const double mvMps = 850.0;

        var bullet = ScalarBullet(0.40);
        var atm = Atmosphere.IsaAtElevation(launchHeight);

        var forward = ForwardSolver.Solve(new ShotScenario
        {
            LatitudeDeg = 45.0, LongitudeDeg = 7.5, HeightM = launchHeight,
            AzimuthDeg = 75.0, LaunchElevationDeg = 0.6,   // short, low-TOF scene
            MuzzleVelocityMps = mvMps, Bullet = bullet, Atmosphere = atm,
            Wind = WindProfile.Calm, IncludeCoriolis = false,
            TargetHeightM = launchHeight, MaxTimeS = 60.0,
        });
        Assert.Equal(TrajectoryEndReason.GroundHit, forward.Trajectory.EndReason);
        Assert.True(forward.DownRangeM > 400, $"Degenerate scene: {forward.DownRangeM} m.");

        var impactPos = forward.Trajectory.FinalPosition;
        Geodetic.EcefToLla(impactPos, out double iLat, out double iLon, out double iH);
        var impactFrame = EnuFrame.AtOrigin(iLat, iLon, iH);
        var vEnu = impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity);
        var observation = new ImpactObservation(
            impactPos,
            -EnuFrame.ElevationDeg(vEnu),
            EnuFrame.AzimuthDeg(vEnu),
            forward.Trajectory.FinalSpeed);

        // Near-exact priors: the cloud is the sigma -> 0 limit of the cloud.
        // Angle sigma is deliberately tiny (0.0005 deg): at this shallow
        // launch the along-track lever dR/dtheta is huge (2 cot 2theta ~
        // 95 rad^-1), so 0.005 deg would already smear the cloud by ~12 m.
        var priors = new InversePriors(
            MuzzleVelocity: ScalarPrior.Uniform(mvMps - 0.5, mvMps + 0.5),
            ExteriorAngleDeg: ScalarPrior.Normal(observation.ExteriorAngleDeg, 0.0005),
            AzimuthDeg: ScalarPrior.Normal(observation.VelocityAzimuthDeg, 0.001),
            BcLbIn2: ScalarPrior.Normal(0.40, 0.002),
            WindEastMps: ScalarPrior.Normal(0.0, 0.05),
            WindNorthMps: ScalarPrior.Normal(0.0, 0.05));

        var opts = new BackwardOptions { OriginHeightM = launchHeight };
        const int n = 18;
        const ulong seed = 424242;

        var cloud1 = OriginCloud.Run(observation, priors, bullet, atm, includeCoriolis: false,
            hypotheses: n, seed, opts);
        var cloud2 = OriginCloud.Run(observation, priors, bullet, atm, includeCoriolis: false,
            hypotheses: n, seed, opts);

        // Determinism: same seed -> bit-identical cloud.
        Assert.Equal(0, cloud1.FailedCount);
        Assert.Equal(0, cloud2.FailedCount);
        Assert.Equal(cloud1.RequestedCount, cloud2.RequestedCount);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(cloud1.Points[i], cloud2.Points[i]); // record struct: exact doubles
            Assert.InRange(cloud1.Points[i].ImpliedMuzzleVelocityMps, mvMps - 0.51, mvMps + 0.51);
        }

        // Truth in the reporting frame (impact-anchored ENU).
        var truthEnu = impactFrame.ToEnu(forward.Scene.OriginEcef);
        var truth = new Vec2(truthEnu.X, truthEnu.Y);

        var pts = cloud1.Points.Select(p => new Vec2(p.EastM, p.NorthM)).ToList();
        var stats = new Vec2(
            pts.Average(p => p.X),
            pts.Average(p => p.Y));

        // Collapse: the noise-free cloud sits on the true origin (n = 18
        // medians/means carry sampling noise of a fraction of the prior width).
        double meanErr = (stats - truth).Length;
        Assert.True(meanErr <= 2.0, $"Cloud mean {meanErr:F3} m from truth (tol 2 m).");

        // Up-range placement: the origin lies opposite the travel direction.
        double bearingDeg = (Math.Atan2(stats.X, stats.Y) * 180.0 / Math.PI + 360.0) % 360.0;
        double expectedBearing = (observation.VelocityAzimuthDeg + 180.0) % 360.0;
        double bearingErr = Math.Abs(((bearingDeg - expectedBearing + 540.0) % 360.0) - 180.0);
        Assert.True(bearingErr <= 3.0, $"Cloud bearing {bearingDeg:F2} vs expected {expectedBearing:F2}.");

        // Dispersion: the near-exact priors must not smear the cloud.
        double maxSpread = pts.Max(p => (p - stats).Length);
        Assert.True(maxSpread <= 8.0, $"Cloud spread {maxSpread:F3} m exceeds 8 m for near-exact priors.");
    }

    /// <summary>Same seed -> bit-identical hypothesis sequence; different seed -> different cloud.</summary>
    [Fact]
    public void Sampler_SameSeed_ReproducesSequence()
    {
        var priors = TestPriors();
        var a = new HypothesisSampler(20261022);
        var b = new HypothesisSampler(20261022);
        var c = new HypothesisSampler(7);

        bool anyDiffFromOtherSeed = false;
        for (int i = 0; i < 50; i++)
        {
            var ha = a.Next(priors);
            var hb = b.Next(priors);
            var hc = c.Next(priors);
            Assert.Equal(ha, hb); // record struct: exact double equality
            if (!ha.Equals(hc)) anyDiffFromOtherSeed = true;
        }
        Assert.True(anyDiffFromOtherSeed);
    }

    /// <summary>
    /// The sampler must reproduce its declared priors (n = 100 000): bounds for
    /// the uniform, mean and standard deviation for every normal, and azimuth
    /// wrapping across the 0/360 seam.
    /// </summary>
    [Fact]
    public void Sampler_RecoversPriorMoments()
    {
        const int n = 100_000;
        var sampler = new HypothesisSampler(12345);
        var priors = TestPriors();

        double sumSpeed = 0, sumSpeedSq = 0;
        double minSpeed = double.MaxValue, maxSpeed = double.MinValue;
        double sumEl = 0, sumElSq = 0;
        double sumBc = 0, sumBcSq = 0;
        double sumWe = 0, sumWn = 0;
        int azBelowFive = 0, azAbove355 = 0, azMid = 0;

        for (int i = 0; i < n; i++)
        {
            var h = sampler.Next(priors);

            sumSpeed += h.MuzzleVelocityMps;
            sumSpeedSq += h.MuzzleVelocityMps * h.MuzzleVelocityMps;
            if (h.MuzzleVelocityMps < minSpeed) minSpeed = h.MuzzleVelocityMps;
            if (h.MuzzleVelocityMps > maxSpeed) maxSpeed = h.MuzzleVelocityMps;

            sumEl += h.ExteriorAngleDeg;
            sumElSq += h.ExteriorAngleDeg * h.ExteriorAngleDeg;

            sumBc += h.BcLbIn2;
            sumBcSq += h.BcLbIn2 * h.BcLbIn2;

            sumWe += h.WindEastMps;
            sumWn += h.WindNorthMps;

            Assert.InRange(h.AzimuthDeg, 0.0, 360.0 - 1e-12);
            if (h.AzimuthDeg < 5.0) azBelowFive++;
            else if (h.AzimuthDeg > 355.0) azAbove355++;
            else azMid++;
        }

        // Uniform(700, 900): bounds exact, mean 800 +/- 1.0 (~5.5 standard errors).
        Assert.True(minSpeed >= 700.0, $"Uniform draw below minimum: {minSpeed}.");
        Assert.True(maxSpeed <= 900.0, $"Uniform draw above maximum: {maxSpeed}.");
        double meanSpeed = sumSpeed / n;
        Assert.InRange(meanSpeed, 799.0, 801.0);

        // Normal(3.2, 0.3): mean +/- 0.01 (~10 SE), sd +/- 0.005 (~7 SE).
        double meanEl = sumEl / n;
        double sdEl = Math.Sqrt(sumElSq / n - meanEl * meanEl);
        Assert.InRange(meanEl, 3.19, 3.21);
        Assert.InRange(sdEl, 0.295, 0.305);

        // Normal(0.4, 0.02): mean +/- 0.001, sd +/- 0.001.
        double meanBc = sumBc / n;
        double sdBc = Math.Sqrt(sumBcSq / n - meanBc * meanBc);
        Assert.InRange(meanBc, 0.399, 0.401);
        Assert.InRange(sdBc, 0.019, 0.021);

        // Wind axes: means at 0 +/- 0.05 (sigma 2).
        Assert.InRange(sumWe / n, -0.05, 0.05);
        Assert.InRange(sumWn / n, -0.05, 0.05);

        // Azimuth Normal(359.5, 2.0) wrapped: both sides of the 0/360 seam must
        // receive mass (expected ~39.8k below 5 deg, ~58.7k above 355 deg, ~1.6k
        // between - thresholds are many standard errors away).
        Assert.True(azBelowFive > 30_000, $"Seam side <5 deg under-populated: {azBelowFive}.");
        Assert.True(azAbove355 > 45_000, $"Seam side >355 deg under-populated: {azAbove355}.");
        Assert.True(azMid > 500, $"Mid-range azimuth under-populated: {azMid}.");
    }

    private static InversePriors TestPriors() => new(
        MuzzleVelocity: ScalarPrior.Uniform(700, 900),
        ExteriorAngleDeg: ScalarPrior.Normal(3.2, 0.3),
        AzimuthDeg: ScalarPrior.Normal(359.5, 2.0), // straddles the 0/360 seam on purpose
        BcLbIn2: ScalarPrior.Normal(0.4, 0.02),
        WindEastMps: ScalarPrior.Normal(0.0, 2.0),
        WindNorthMps: ScalarPrior.Normal(0.0, 2.0));

    private static BulletProfile ScalarBullet(double bc) => new()
    {
        Name = "M3.1 identity round-trip bullet",
        MassGr = 10.6,
        DragMode = DragMode.ScalarBc,
        ReferenceStandard = "G1",
        BcLbIn2 = bc,
    };
}
