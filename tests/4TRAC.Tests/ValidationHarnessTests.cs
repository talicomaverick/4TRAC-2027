using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Flat;
using FourTrac.Core.Geo;
using Xunit;

namespace FourTrac.Tests;

/// <summary>Checks on the validation harness's truth generation (spin-on and lofted shots).</summary>
public class ValidationHarnessTests
{
    private static readonly Atmosphere Std = Atmosphere.FromMeasurements(15.0, 101325.0, 0.5, 0.0);
    private static readonly FlatSpin Spin308 = new(0.308, 168.0, 10.0, 1.215, true);

    [Fact]
    public void SpinTruth_RoundTrip_RecoversTheOrigin()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.462));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, coriolis: true);
        double ox = 600.0, oy = -800.0, mv = 800.0;

        var v = CoverageStudy.ObservedImpactVelocity(model, mv, ox, oy, 0.0, Spin308, highArc: false);
        Assert.NotNull(v);

        var obs = new FlatObservation(-EnuFrame.ElevationDeg(v!.Value), EnuFrame.AzimuthDeg(v.Value));
        var scene = new FlatScene(40.0, 0.0, Std, Coriolis: true);
        var origin = FlatSolver.SolveAtMuzzleVelocity(obs, mv, drag, scene, 0.0, 0.0, Spin308);

        Assert.True(origin.Converged);
        Assert.True(Math.Abs(origin.SpinDriftM) > 0.01, "Spin drift should be non-zero in the truth.");
        Assert.InRange(origin.EastM, ox - 0.5, ox + 0.5);
        Assert.InRange(origin.NorthM, oy - 0.5, oy + 0.5);
    }

    [Fact]
    public void SpinTruth_DiffersFromSpinOffTruth()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.462));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, coriolis: true);

        var plain = CoverageStudy.ObservedImpactVelocity(model, 800.0, 0.0, -2000.0, 0.0, null, false);
        var spun = CoverageStudy.ObservedImpactVelocity(model, 800.0, 0.0, -2000.0, 0.0, Spin308, false);

        Assert.NotNull(plain);
        Assert.NotNull(spun);
        // Same origin, same muzzle speed: the aim is corrected for drift, so the impact azimuth moves.
        Assert.NotEqual(EnuFrame.AzimuthDeg(plain!.Value), EnuFrame.AzimuthDeg(spun!.Value), 6);
    }

    [Fact]
    public void HighArcShot_ReachesASteepExteriorAngle()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.462));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, coriolis: true);

        var shot = FlatShooter.Shoot(model, 800.0, 0.0, -1000.0, 0.0, highArc: true);

        Assert.NotNull(shot);
        double ext = -EnuFrame.ElevationDeg(shot!.Value.Velocity);
        Assert.True(ext >= 15.0, $"High-arc exterior angle was only {ext:F1} deg.");
        Assert.True(shot.Value.TimeOfFlightS > 5.0);
    }

    [Fact]
    public void Coverage_SpinOn_FlatOriginPrior_Smoke()
    {
        var config = new CoverageConfig(
            LatDeg: 40.0, OriginHeightM: 0.0, Atmosphere: Std, Coriolis: true,
            Reference: DragTableCatalog.ByStandard("G1"),
            BcNominalLbIn2: 0.462, BcSigmaLbIn2: 0.462 * 0.03,
            LaunchElevationMinDeg: 0.0, LaunchElevationMaxDeg: 0.0, LaunchAzimuthDeg: 0.0,
            MvMinMps: 2550 * UnitConversions.FpsToMps, MvMaxMps: 2700 * UnitConversions.FpsToMps,
            WindEastMps: 0.0, WindNorthMps: 0.0, WindSigmaMps: 0.5,
            AngleSigmaDeg: 0.5, AzimuthSigmaDeg: 0.5,
            Spin: Spin308,
            OriginRangeMinM: 3000.0, OriginRangeMaxM: 4500.0);

        var result = CoverageStudy.Run(config, trials: 12, hypothesesPerTrial: 80, seed: 20261030UL);

        Assert.True(result.Trials.Count >= 10, $"Too few completed trials: {result.Trials.Count}.");
        Assert.True(result.Coverage95 >= 0.75, $"95% coverage {result.Coverage95:P0} is far below nominal with spin on.");
    }

    [Fact]
    public void ElevatedShooterFiringDownward_NeverReturnsAWrongConvergedOrigin()
    {
        // Shooter on a plane 150 m above the impact, firing 17 deg DOWN. Forward in time this is a
        // descending launch. The solver must report this origin (or at least not a far lofted one).
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.462));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, coriolis: false);
        double mv = 800.0;
        var v0 = EnuFrame.DirectionFromAzimuthElevation(0.0, -17.0) * mv;
        var fwd = FlatIntegrators.Forward(model, new Cartesian3(0.0, 0.0, 150.0), v0);
        Assert.True(fwd.Hit);

        var obs = new FlatObservation(-EnuFrame.ElevationDeg(fwd.Velocity), EnuFrame.AzimuthDeg(fwd.Velocity));
        var scene = new FlatScene(40.0, 150.0, Std, Coriolis: false);
        var origin = FlatSolver.SolveAtMuzzleVelocity(obs, mv, drag, scene, 0.0, 0.0);

        double trueE = -fwd.Position.X, trueN = -fwd.Position.Y;
        // Known limitation (2026-11-06): a descending launch from a plane above the impact is not
        // supported; the solver returns no origin (NonFinite). The safety property guarded here:
        // it must never return a CONVERGED origin that is not the true one.
        if (origin.Converged)
            Assert.True(
                Math.Abs(origin.EastM - trueE) < 1.0 && Math.Abs(origin.NorthM - trueN) < 1.0,
                $"Returned a wrong origin ({origin.EastM:F0}, {origin.NorthM:F0}) m instead of ({trueE:F0}, {trueN:F0}) m.");
    }

    private static void AssertDescendingRecovers(
        double bc, double mv, double originH, double elevDeg, double windE, double windN, bool coriolis)
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(bc));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, windE, windN, coriolis);
        var v0 = EnuFrame.DirectionFromAzimuthElevation(30.0, elevDeg) * mv;
        var fwd = FlatIntegrators.Forward(model, new Cartesian3(0.0, 0.0, originH), v0);
        Assert.True(fwd.Hit);

        var obs = new FlatObservation(-EnuFrame.ElevationDeg(fwd.Velocity), EnuFrame.AzimuthDeg(fwd.Velocity));
        var scene = new FlatScene(40.0, originH, Std, Coriolis: coriolis);
        var options = new FlatSolveOptions { DescendingLaunch = true };
        var origin = FlatSolver.SolveAtMuzzleVelocity(obs, mv, drag, scene, windE, windN, null, options);

        Assert.True(origin.Converged, $"Descending branch found no origin (end: {origin.EndReason}).");
        Assert.InRange(origin.EastM, -fwd.Position.X - 0.5, -fwd.Position.X + 0.5);
        Assert.InRange(origin.NorthM, -fwd.Position.Y - 0.5, -fwd.Position.Y + 0.5);
        Assert.InRange(origin.HeightM, originH - 0.5, originH + 0.5);
        Assert.InRange(origin.LaunchElevationDeg, elevDeg - 0.05, elevDeg + 0.05);
    }

    [Fact]
    public void DescendingBranch_Rifle_FromAHillside_RecoversTheOrigin() =>
        AssertDescendingRecovers(bc: 0.462, mv: 800.0, originH: 150.0, elevDeg: -17.0, windE: 0.0, windN: 0.0, coriolis: false);

    [Fact]
    public void DescendingBranch_Handgun_FromARooftop_WithWind_RecoversTheOrigin() =>
        AssertDescendingRecovers(bc: 0.15, mv: 350.0, originH: 60.0, elevDeg: -8.0, windE: 2.0, windN: -1.0, coriolis: true);

    [Fact]
    public void DescendingBranch_HighRise_SteepShot_RecoversTheOrigin() =>
        AssertDescendingRecovers(bc: 0.15, mv: 350.0, originH: 120.0, elevDeg: -35.0, windE: 1.0, windN: 0.0, coriolis: true);

    [Fact]
    public void ClimbingAndDescendingBranches_AreBothAvailableForAPlaneAboveTheImpact()
    {
        // Same observation, plane 100 m above the impact: the climbing (lofted) branch and the
        // descending (direct-down) branch are distinct origins, and the descending one is nearer.
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.15));
        var scene = new FlatScene(40.0, 100.0, Std, Coriolis: false);
        var obs = new FlatObservation(30.0, 0.0);

        var down = FlatSolver.SolveAtMuzzleVelocity(obs, 350.0, drag, scene, 0.0, 0.0, null, new FlatSolveOptions { DescendingLaunch = true });
        var up = FlatSolver.SolveAtMuzzleVelocity(obs, 350.0, drag, scene, 0.0, 0.0, null, new FlatSolveOptions { DescendingLaunch = false });

        Assert.True(down.Converged, $"Descending: {down.EndReason}");
        Assert.True(down.LaunchElevationDeg < 0.0);
        if (up.Converged)
        {
            Assert.True(up.LaunchElevationDeg > 0.0);
            Assert.True(Math.Abs(up.NorthM) > Math.Abs(down.NorthM));
        }
    }

    private static readonly FlatSpin Spin9mm = new(0.355, 115.0, 10.0, 0.60, true);

    [Fact]
    public void DescendingBranch_WithSpin_RoundTrip_RecoversTheOrigin()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.15));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, 1.0, -1.0, coriolis: true);
        double ox = 80.0, oy = -120.0, mv = 350.0, h = 60.0;

        var v = CoverageStudy.ObservedImpactVelocity(model, mv, ox, oy, h, Spin9mm, highArc: false, descending: true);
        Assert.NotNull(v);
        Assert.True(EnuFrame.ElevationDeg(v!.Value) < 0.0);

        var obs = new FlatObservation(-EnuFrame.ElevationDeg(v.Value), EnuFrame.AzimuthDeg(v.Value));
        var scene = new FlatScene(40.0, h, Std, Coriolis: true);
        var origin = FlatSolver.SolveAtMuzzleVelocity(
            obs, mv, drag, scene, 1.0, -1.0, Spin9mm, new FlatSolveOptions { DescendingLaunch = true });

        Assert.True(origin.Converged, $"No origin (end: {origin.EndReason}).");
        Assert.InRange(origin.EastM, ox - 0.5, ox + 0.5);
        Assert.InRange(origin.NorthM, oy - 0.5, oy + 0.5);
    }

    [Fact]
    public void Coverage_DescendingBranch_Handgun_Smoke()
    {
        var config = new CoverageConfig(
            LatDeg: 40.0, OriginHeightM: 60.0, Atmosphere: Std, Coriolis: true,
            Reference: DragTableCatalog.ByStandard("G1"),
            BcNominalLbIn2: 0.15, BcSigmaLbIn2: 0.015,
            LaunchElevationMinDeg: 0.0, LaunchElevationMaxDeg: 0.0, LaunchAzimuthDeg: 0.0,
            MvMinMps: 300.0, MvMaxMps: 400.0,
            WindEastMps: 0.0, WindNorthMps: 0.0, WindSigmaMps: 0.5,
            AngleSigmaDeg: 0.5, AzimuthSigmaDeg: 0.5,
            Spin: Spin9mm,
            OriginRangeMinM: 90.0, OriginRangeMaxM: 110.0,
            HighArc: false, Descending: true);

        var result = CoverageStudy.Run(config, trials: 10, hypothesesPerTrial: 60, seed: 20261106UL);

        Assert.True(result.Trials.Count >= 7, $"Too few completed trials: {result.Trials.Count} (skipped {result.Skipped}).");
        Assert.True(result.Coverage95 >= 0.6, $"95% coverage {result.Coverage95:P0} is far below nominal on the descending branch.");
        Assert.Equal(result.Skipped, result.SkippedInvalidDraw + result.SkippedNotAimable + result.SkippedTooFewConverged);
        Assert.Equal(10, result.Trials.Count + result.Skipped);
    }

    [Fact]
    public void Analysis_DescendingBranch_LocatesAHandgunShooterOnARoof()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.15));
        var model = new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, coriolis: false);
        var v0 = EnuFrame.DirectionFromAzimuthElevation(30.0, -14.0) * 350.0;
        var fwd = FlatIntegrators.Forward(model, new Cartesian3(0.0, 0.0, 60.0), v0);
        Assert.True(fwd.Hit);

        var request = new FlatAnalysisRequest(
            ImpactLatDeg: 40.0, ImpactLonDeg: -75.0, ImpactElevationM: 0.0, OriginHeightM: 60.0,
            ExteriorAngleDeg: -EnuFrame.ElevationDeg(fwd.Velocity), ExteriorSigmaDeg: 0.5,
            AzimuthDeg: EnuFrame.AzimuthDeg(fwd.Velocity), AzimuthSigmaDeg: 0.5,
            MvMinMps: 300.0, MvMaxMps: 400.0, BcLbIn2: 0.15, BcSigmaFraction: 0.10, ReferenceStandard: "G1",
            TemperatureC: 15.0, PressurePa: 101325.0, RelativeHumidity: 0.5,
            WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 0.5,
            Coriolis: false, Spin: null, Hypotheses: 500, Seed: 77UL,
            DescendingLaunch: true);

        var result = FlatAnalysis.Run(request);

        Assert.Null(result.Error);
        double trueE = -fwd.Position.X, trueN = -fwd.Position.Y;
        double miss = Math.Sqrt((result.ModeEnM.X - trueE) * (result.ModeEnM.X - trueE) + (result.ModeEnM.Y - trueN) * (result.ModeEnM.Y - trueN));
        Assert.True(miss < 30.0, $"Mode is {miss:F1} m from the true rooftop origin.");
        Assert.Contains(result.Notes, n => n.Contains("DESCENDING branch"));

        // The same observation on the climbing branch is a different candidate (or has no region).
        var climbing = FlatAnalysis.Run(request with { DescendingLaunch = false });
        Assert.True(climbing.Error is not null || climbing.Notes.Any(n => n.Contains("CLIMBING branch")));
    }

    private static FlatAnalysisRequest HandgunRequest(double angleDeg, bool spin) => new(
        ImpactLatDeg: -30.0000, ImpactLonDeg: 25.0000, ImpactElevationM: 1700.0, OriginHeightM: 75.0,
        ExteriorAngleDeg: angleDeg, ExteriorSigmaDeg: 4.0, AzimuthDeg: 135.0, AzimuthSigmaDeg: 3.0,
        MvMinMps: 220.0, MvMaxMps: 270.0, BcLbIn2: 0.17, BcSigmaFraction: 0.15, ReferenceStandard: "G1",
        TemperatureC: 15.0, PressurePa: 83000.0, RelativeHumidity: 0.4,
        WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 1.0,
        Coriolis: true, Spin: spin ? new FlatSpin(0.357, 158.0, 18.75, 0.62, true) : null,
        Hypotheses: 500, Seed: 3UL);

    [Fact]
    public void Analysis_SteepImpact_AddsTheRangeAndSpinWarnings()
    {
        var res = FlatAnalysis.Run(HandgunRequest(76.0, spin: true));

        Assert.Null(res.Error);
        Assert.Contains(res.Notes, n => n.StartsWith("WARNING (steep impact", StringComparison.Ordinal));
        Assert.Contains(res.Notes, n => n.StartsWith("WARNING (spin drift at a steep angle", StringComparison.Ordinal));
    }

    [Fact]
    public void Analysis_SteepImpact_WithoutSpin_OnlyWarnsAboutRange()
    {
        var res = FlatAnalysis.Run(HandgunRequest(76.0, spin: false));

        Assert.Contains(res.Notes, n => n.StartsWith("WARNING (steep impact", StringComparison.Ordinal));
        Assert.DoesNotContain(res.Notes, n => n.StartsWith("WARNING (spin drift at a steep angle", StringComparison.Ordinal));
    }

    [Fact]
    public void Analysis_ModerateImpact_HasNoSteepWarning()
    {
        var res = FlatAnalysis.Run(HandgunRequest(30.0, spin: true));

        Assert.DoesNotContain(res.Notes, n => n.StartsWith("WARNING (steep impact", StringComparison.Ordinal));
    }

    [Fact]
    public void Analysis_SymmetricCrosswind_WidensTheZoneAndSaysSo()
    {
        var baseRequest = HandgunRequest(60.0, spin: false);
        var none = FlatAnalysis.Run(baseRequest);
        var both = FlatAnalysis.Run(baseRequest with { SymmetricCrosswindMps = 5.0 });

        Assert.Null(none.Error);
        Assert.Null(both.Error);
        Assert.True(both.Bands.Max(b => b.AreaM2) > 1.3 * none.Bands.Max(b => b.AreaM2),
            $"Zone with unknown wind side ({both.Bands.Max(b => b.AreaM2):F0} m2) is not wider than without ({none.Bands.Max(b => b.AreaM2):F0} m2).");
        Assert.Contains(both.Notes, n => n.StartsWith("WIND UNKNOWN", StringComparison.Ordinal));
        Assert.DoesNotContain(none.Notes, n => n.StartsWith("WIND UNKNOWN", StringComparison.Ordinal));
    }

    [Fact]
    public void Analysis_SymmetricCrosswindOff_IsIdenticalToTheOldBehaviour()
    {
        var baseRequest = HandgunRequest(60.0, spin: false);
        var a = FlatAnalysis.Run(baseRequest);
        var b = FlatAnalysis.Run(baseRequest with { SymmetricCrosswindMps = 0.0 });

        Assert.Equal(a.ModeEnM.X, b.ModeEnM.X);
        Assert.Equal(a.ModeEnM.Y, b.ModeEnM.Y);
    }

    [Fact]
    public void WindShear_Model_MatchesConstantWindAtAndAboveTheTopOfTheLayer()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.15));
        var shear = new FlatWindShear(6.0, -3.0, 50.0);
        var layered = new FlatTrajectoryModel(drag, Std, 40.0, 2.0, 1.0, true, shear);
        var ground = new FlatTrajectoryModel(drag, Std, 40.0, 2.0, 1.0, true);
        var aloft = new FlatTrajectoryModel(drag, Std, 40.0, 8.0, -2.0, true);

        static double[] D(FlatTrajectoryModel m, double z)
        {
            var d = new double[6];
            m.Derivatives(0.0, new[] { 10.0, 20.0, z, 120.0, -80.0, -40.0 }, d);
            return d;
        }

        Assert.Equal(D(ground, 0.0), D(layered, 0.0));
        Assert.Equal(D(ground, -5.0), D(layered, -5.0));
        Assert.Equal(D(aloft, 50.0), D(layered, 50.0));
        Assert.Equal(D(aloft, 400.0), D(layered, 400.0));
        // Halfway up the layer the wind is halfway between: the drag acceleration differs from both ends.
        Assert.NotEqual(D(ground, 25.0)[3], D(layered, 25.0)[3]);
        Assert.NotEqual(D(aloft, 25.0)[3], D(layered, 25.0)[3]);
    }

    [Fact]
    public void WindShear_Analysis_MovesTheOriginAndSaysSo()
    {
        var baseRequest = HandgunRequest(60.0, spin: false);
        var none = FlatAnalysis.Run(baseRequest);
        var layered = FlatAnalysis.Run(baseRequest with { WindAloftSpeedMps = 10.0, WindAloftFromDeg = 270.0, WindAloftHeightM = 5.0 });

        Assert.Null(none.Error);
        Assert.Null(layered.Error);
        double shift = Math.Sqrt(Math.Pow(layered.ModeEnM.X - none.ModeEnM.X, 2) + Math.Pow(layered.ModeEnM.Y - none.ModeEnM.Y, 2));
        Assert.True(shift > 1.0, $"A 10 m/s wind aloft moved the mode by only {shift:F2} m.");
        Assert.Contains(layered.Notes, n => n.StartsWith("WIND LAYERS", StringComparison.Ordinal));
        Assert.DoesNotContain(none.Notes, n => n.StartsWith("WIND LAYERS", StringComparison.Ordinal));
    }

    [Fact]
    public void WindShear_WhenAloftEqualsGround_IsIdenticalToTheOldBehaviour()
    {
        var baseRequest = HandgunRequest(60.0, spin: false);
        var a = FlatAnalysis.Run(baseRequest);
        var b = FlatAnalysis.Run(baseRequest with { WindAloftSpeedMps = baseRequest.WindSpeedMps, WindAloftFromDeg = baseRequest.WindFromDeg, WindAloftHeightM = 50.0 });

        Assert.Equal(a.ModeEnM.X, b.ModeEnM.X);
        Assert.Equal(a.ModeEnM.Y, b.ModeEnM.Y);
    }
}
