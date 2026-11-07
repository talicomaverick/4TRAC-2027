using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Flat;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// Flat-earth model verification (V0 for the flat path): closed forms, the
/// forward-inverse round trip, and the spin-drift convention.
/// </summary>
public class FlatPhysicsTests
{
    private sealed class ZeroDrag : IDragModel
    {
        public double Beta(double airRelativeSpeedMps, double mach) => 0.0;
    }

    private static readonly Atmosphere Std = Atmosphere.FromMeasurements(15.0, 101325.0, 0.5, 0.0);

    [Fact]
    public void Vacuum_Range_MatchesClosedForm()
    {
        var model = new FlatTrajectoryModel(new ZeroDrag(), Std, 40.0, 0.0, 0.0, coriolis: false);
        double v = 300.0, theta = 30.0 * Math.PI / 180.0;
        var fwd = FlatIntegrators.Forward(
            model,
            Cartesian3.Zero,
            new Cartesian3(v * Math.Cos(theta), 0.0, v * Math.Sin(theta)));

        double expected = v * v * Math.Sin(2 * theta) / FlatTrajectoryModel.Gravity;
        Assert.True(fwd.Hit);
        Assert.InRange(fwd.Position.X, expected - 1e-3, expected + 1e-3);
        Assert.InRange(fwd.Position.Z, -1e-3, 1e-3);
    }

    [Fact]
    public void Coriolis_HorizontalFall_MatchesClosedFormEastDeflection()
    {
        // Horizontal fire northward from 1000 m in vacuum at 45 deg N:
        //   x(t) = Omega sin(lat) v t^2 + (1/3) Omega cos(lat) g t^3   (first order in Omega)
        const double lat = 45.0;
        const double v = 500.0;
        const double h = 1000.0;
        var model = new FlatTrajectoryModel(new ZeroDrag(), Std, lat, 0.0, 0.0, coriolis: true);
        var fwd = FlatIntegrators.Forward(model, new Cartesian3(0, 0, h), new Cartesian3(0, v, 0));

        double t = Math.Sqrt(2 * h / FlatTrajectoryModel.Gravity);
        double omega = Wgs84.Omega;
        double phi = lat * Math.PI / 180.0;
        double expected = omega * Math.Sin(phi) * v * t * t + omega * Math.Cos(phi) * FlatTrajectoryModel.Gravity * t * t * t / 3.0;

        Assert.True(fwd.Hit);
        Assert.True(fwd.Position.X > 0.0, "Northward shot must deflect EAST in the Northern hemisphere.");
        Assert.InRange(fwd.Position.X, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void Miller_StabilityFactor_MatchesHandCalculation()
    {
        // .308 168 gr, 1:10 in twist, 1.22 in length, 2650 fps. Hand calculation: SG = 2.430.
        var spin = new FlatSpin(0.308, 168.0, 10.0, 1.22, RightHandTwist: true);
        double sg = SpinDrift.StabilityFactor(spin, 2650.0 * UnitConversions.FpsToMps);
        Assert.InRange(sg, 2.42, 2.44);
    }

    [Fact]
    public void SpinDrift_LitzForm_RightHandTwistDriftsRight()
    {
        // D(t) = 1.25 (SG + 1.2) t^1.83 inches. With SG = 2 and t = 1 s: 4.0 in = 0.1016 m.
        double right = SpinDrift.DisplacementM(2.0, 1.0, rightHandTwist: true);
        double left = SpinDrift.DisplacementM(2.0, 1.0, rightHandTwist: false);
        Assert.InRange(right, 0.1016 - 1e-6, 0.1016 + 1e-6);
        Assert.Equal(-right, left, 12);
    }

    [Fact]
    public void RightOf_NorthTravel_IsEast()
    {
        var r = SpinDrift.RightOf(0.0);
        Assert.InRange(r.X, 1 - 1e-12, 1 + 1e-12);
        Assert.InRange(r.Y, -1e-12, 1e-12);
    }

    [Fact]
    public void RoundTrip_NoSpin_RecoversOriginAndMuzzleVelocity()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.40));
        var scene = new FlatScene(40.0, -5.0, Std, Coriolis: true);
        var truth = Forward(drag, scene, 850.0, 25.0, 60.0, 2.0, -1.0, null, out double px, out double py, out double ext, out double az);

        var origin = FlatSolver.SolveAtMuzzleVelocity(
            new FlatObservation(ext, az), 850.0, drag, scene, 2.0, -1.0);

        Assert.True(origin.Converged);
        Assert.InRange(origin.EastM, -px - 0.01, -px + 0.01);
        Assert.InRange(origin.NorthM, -py - 0.01, -py + 0.01);
        Assert.InRange(origin.MuzzleVelocityMps, 850.0 - 0.01, 850.0 + 0.01);
        Assert.InRange(origin.LaunchElevationDeg, 25.0 - 0.01, 25.0 + 0.01);
        Assert.InRange(origin.HeightM, -5.0 - 1e-6, -5.0 + 1e-6);
    }

    [Fact]
    public void RoundTrip_WithSpin_IsCloserThanIgnoringSpin()
    {
        var spin = new FlatSpin(0.308, 168.0, 10.0, 1.22, RightHandTwist: true);
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.40));
        var scene = new FlatScene(40.0, -5.0, Std, Coriolis: true);
        Forward(drag, scene, 850.0, 25.0, 60.0, 2.0, -1.0, spin, out double px, out double py, out double ext, out double az);

        var withSpin = FlatSolver.SolveAtMuzzleVelocity(new FlatObservation(ext, az), 850.0, drag, scene, 2.0, -1.0, spin);
        var noSpin = FlatSolver.SolveAtMuzzleVelocity(new FlatObservation(ext, az), 850.0, drag, scene, 2.0, -1.0, null);

        Assert.True(withSpin.Converged);
        Assert.True(noSpin.Converged);
        double errWith = Math.Sqrt(Math.Pow(withSpin.EastM + px, 2) + Math.Pow(withSpin.NorthM + py, 2));
        double errNo = Math.Sqrt(Math.Pow(noSpin.EastM + px, 2) + Math.Pow(noSpin.NorthM + py, 2));
        Assert.True(withSpin.SpinDriftM > 0.05, $"Drift should be material here, got {withSpin.SpinDriftM:F3} m.");
        Assert.True(errWith < 0.02, $"Spin-corrected origin error {errWith:F4} m.");
        Assert.True(errWith < errNo, "Ignoring spin drift must give a worse origin than correcting for it.");
    }

    [Fact]
    public void DescendingLaunch_IsNeverReportedAsAnOrigin()
    {
        // Shooter above the roof firing downward. The true origin is on the
        // descending branch and must not be reported. A lofted solution from the
        // same plane can still match the impact, and the rule permits reporting it;
        // the test asserts only what the rule guarantees: any reported origin ascends.
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.40));
        var scene = new FlatScene(40.0, 30.0, Std, Coriolis: false);
        var fwd = FlatIntegrators.Forward(
            new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, false),
            new Cartesian3(0, 0, 30.0),
            EnuFrame.DirectionFromAzimuthElevation(90.0, -10.0) * 300.0);
        Assert.True(fwd.Hit);

        double ext = -EnuFrame.ElevationDeg(fwd.Velocity);
        double az = EnuFrame.AzimuthDeg(fwd.Velocity);
        var origin = FlatSolver.SolveAtMuzzleVelocity(new FlatObservation(ext, az), 300.0, drag, scene, 0.0, 0.0);

        if (origin.Converged)
            Assert.True(origin.LaunchElevationDeg > 0.0, "A reported origin must be an ascending launch.");
    }

    [Fact]
    public void Cloud_WithZeroSpread_CollapsesOntoTruth()
    {
        var reference = DragTableCatalog.ByStandard("G1");
        var drag = new BcDragModel(reference, new ConstantBcProvider(0.40));
        var scene = new FlatScene(40.0, -5.0, Std, Coriolis: true);
        var truth = Forward(drag, scene, 850.0, 25.0, 60.0, 2.0, -1.0, null, out double px, out double py, out double ext, out double az);

        var priors = new InversePriors(
            ScalarPrior.Normal(850.0, 1e-9),
            ScalarPrior.Normal(ext, 1e-9),
            ScalarPrior.Normal(az, 1e-9),
            ScalarPrior.Normal(0.40, 1e-9),
            ScalarPrior.Normal(2.0, 1e-9),
            ScalarPrior.Normal(-1.0, 1e-9));

        var cloud = FlatCloud.Run(priors, reference, scene, null, 8, 12345UL);

        Assert.Equal(8, cloud.Points.Count);
        Assert.Equal(0, cloud.Rejected);
        foreach (var p in cloud.Points)
        {
            Assert.InRange(p.EastM, -px - 0.01, -px + 0.01);
            Assert.InRange(p.NorthM, -py - 0.01, -py + 0.01);
        }
    }

    [Fact]
    public void Analysis_DefaultScene_ReturnsRegionsAndDeclaredNotes()
    {
        var req = new FlatAnalysisRequest(
            ImpactLatDeg: 40.0, ImpactLonDeg: -75.0, ImpactElevationM: 100.0, OriginHeightM: 0.0,
            ExteriorAngleDeg: 30.0, ExteriorSigmaDeg: 0.5, AzimuthDeg: 270.0, AzimuthSigmaDeg: 0.5,
            MvMinMps: 2550 * UnitConversions.FpsToMps, MvMaxMps: 2700 * UnitConversions.FpsToMps,
            BcLbIn2: 0.462, BcSigmaFraction: 0.03, ReferenceStandard: "G1",
            TemperatureC: (59 - 32) * 5.0 / 9.0, PressurePa: 29.92 * 3386.39, RelativeHumidity: 0.5,
            WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 0.5,
            Coriolis: true,
            Spin: new FlatSpin(0.308, 168.0, 10.0, 1.22, RightHandTwist: true),
            Hypotheses: 60, Seed: 20261029UL);

        var r = FlatAnalysis.Run(req, allowUncalibratedHypothesisCount: true);

        Assert.Null(r.Error);
        Assert.Equal(2, r.Bands.Count);
        Assert.All(r.Bands, b => Assert.True(b.AreaM2 > 0));
        Assert.True(r.Bands[1].AreaM2 > r.Bands[0].AreaM2, "95 % region must be larger than 50 %.");
        Assert.True(r.ModeRangeM < 10_000.0);
        Assert.True(r.ModeBearingDeg > 45 && r.ModeBearingDeg < 135, "Shot travels west, so the origin lies to the east.");
        Assert.Contains(r.Notes, n => n.StartsWith("CALIBRATION"));
    }

    private sealed class LinearSurface : IFlatSurface
    {
        public double Height(double eastM, double northM) => 0.05 * eastM;
    }

    [Fact]
    public void Terrain_SlopedSurface_RoundTripRecoversOrigin()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.40));
        var surface = new LinearSurface();
        double ox = -2500.0, oy = 300.0;
        double oz = surface.Height(ox, oy);

        var model = new FlatTrajectoryModel(drag, Std, 40.0, 0.0, 0.0, coriolis: false);
        var v = FlatShooter.ImpactVelocity(model, 850.0, ox, oy, oz);
        Assert.NotNull(v);

        double ext = -EnuFrame.ElevationDeg(v!.Value);
        double az = EnuFrame.AzimuthDeg(v.Value);
        var scene = new FlatScene(40.0, 0.0, Std, Coriolis: false, Surface: surface);

        var origin = FlatSolver.SolveAtMuzzleVelocity(new FlatObservation(ext, az), 850.0, drag, scene, 0.0, 0.0);

        Assert.True(origin.Converged);
        Assert.InRange(origin.EastM, ox - 0.02, ox + 0.02);
        Assert.InRange(origin.NorthM, oy - 0.02, oy + 0.02);
        Assert.InRange(origin.HeightM, oz - 0.02, oz + 0.02);
    }

    private sealed class NoDataNorthSurface : IFlatSurface
    {
        // Coverage ends 50 m north of the impact; the track to a northern origin must cross it.
        public double Height(double eastM, double northM) => northM > 50.0 ? double.NaN : 0.0;
    }

    [Fact]
    public void Terrain_OutsideDemCoverage_IsRejectedNotExtrapolated()
    {
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.40));
        var scene = new FlatScene(40.0, 0.0, Std, Coriolis: false, Surface: new NoDataNorthSurface());

        // Travel south (180 deg): the origin lies north of the impact, beyond the coverage.
        var origin = FlatSolver.SolveAtMuzzleVelocity(new FlatObservation(30.0, 180.0), 850.0, drag, scene, 0.0, 0.0);

        // Either no solution (rejected), or a solution whose origin lies inside coverage.
        // A reported origin beyond the coverage would be an extrapolation.
        Assert.True(!origin.Converged || origin.NorthM <= 50.0, $"Origin reported beyond coverage at N={origin.NorthM:F1} m.");
    }

    [Fact]
    public void DemGrid_BilinearSampling_NoDataAndOutside()
    {
        var grid = DemGrid.Load(WriteDem());

        // Values: 100 + 10*col + 1000*row, row 0 = northernmost (lat 39.99 + 0.001*(10 - row - 0.5)).
        double lonCentre(int col) => -75.01 + (col + 0.5) * 0.001;
        double latCentre(int row) => 39.99 + (9 - row + 0.5) * 0.001;

        Assert.InRange(grid.Sample(latCentre(3), lonCentre(4)), 100 + 40 + 3000 - 1e-3, 100 + 40 + 3000 + 1e-3);
        Assert.InRange(grid.Sample(latCentre(3), lonCentre(4) + 0.0005), 100 + 45 + 3000 - 1e-3, 100 + 45 + 3000 + 1e-3);
        Assert.True(double.IsNaN(grid.Sample(41.0, -75.0)), "Outside coverage must be NaN.");
    }

    [Fact]
    public void DemSurface_ConstantDem_DropsByCurvatureOverTenKilometres()
    {
        // A constant ellipsoidal height is a sphere cap: at 10 km it sits ~ d^2/(2R) = 7.8 m below the tangent plane.
        var path = Path.Combine(Path.GetTempPath(), $"4trac_flat_{Guid.NewGuid():N}.asc");
        File.WriteAllText(path,
            "ncols 3\nnrows 3\nxllcorner -75.5\nyllcorner 39.5\ncellsize 1.0\nNODATA_value -9999\n" +
            "100 100 100\n100 100 100\n100 100 100\n");
        var grid = DemGrid.Load(path);
        var surface = new DemSurface(grid, 40.0, -75.0);

        double h = surface.Height(0.0, 10_000.0);
        double expected = -10_000.0 * 10_000.0 / (2.0 * 6_378_137.0);
        Assert.InRange(h, expected - 0.5, expected + 0.5);
    }

    private static string WriteDem()
    {
        // 10 x 10 cells of 0.001 deg; height = 100 + 10*col + 1000*row (row 0 = northernmost).
        var sb = new System.Text.StringBuilder();
        sb.Append("ncols 10\nnrows 10\nxllcorner -75.01\nyllcorner 39.99\ncellsize 0.001\nNODATA_value -9999\n");
        for (int row = 0; row < 10; row++)
        {
            for (int col = 0; col < 10; col++)
                sb.Append(100 + 10 * col + 1000 * row).Append(col == 9 ? "\n" : " ");
        }
        var path = Path.Combine(Path.GetTempPath(), $"4trac_flat_{Guid.NewGuid():N}.asc");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void Analysis_DemTooSmallForOrigins_ReportsNoRegionAndNoExtrapolation()
    {
        var req = new FlatAnalysisRequest(
            ImpactLatDeg: 39.995, ImpactLonDeg: -75.005, ImpactElevationM: 100.0, OriginHeightM: 0.0,
            ExteriorAngleDeg: 30.0, ExteriorSigmaDeg: 0.5, AzimuthDeg: 270.0, AzimuthSigmaDeg: 0.5,
            MvMinMps: 2550 * UnitConversions.FpsToMps, MvMaxMps: 2700 * UnitConversions.FpsToMps,
            BcLbIn2: 0.462, BcSigmaFraction: 0.03, ReferenceStandard: "G1",
            TemperatureC: (59 - 32) * 5.0 / 9.0, PressurePa: 29.92 * 3386.39, RelativeHumidity: 0.5,
            WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 0.5,
            Coriolis: true, Spin: null, Hypotheses: 60, Seed: 7UL,
            DemPath: WriteDem());

        var r = FlatAnalysis.Run(req, allowUncalibratedHypothesisCount: true);

        // The 0.01-degree test DEM cannot cover a 3.7 km origin: the result must say so.
        Assert.NotNull(r.Error);
        Assert.Contains("DEM coverage", r.Error);
        Assert.Contains(r.Notes, n => n.StartsWith("Origin surface: DEM"));
    }

    [Fact]
    public void CaseReport_ContainsInputsModelAndNotes()
    {
        var req = new FlatAnalysisRequest(
            ImpactLatDeg: 40.0, ImpactLonDeg: -75.0, ImpactElevationM: 100.0, OriginHeightM: 0.0,
            ExteriorAngleDeg: 30.0, ExteriorSigmaDeg: 0.5, AzimuthDeg: 270.0, AzimuthSigmaDeg: 0.5,
            MvMinMps: 2550 * UnitConversions.FpsToMps, MvMaxMps: 2700 * UnitConversions.FpsToMps,
            BcLbIn2: 0.462, BcSigmaFraction: 0.03, ReferenceStandard: "G1",
            TemperatureC: (59 - 32) * 5.0 / 9.0, PressurePa: 29.92 * 3386.39, RelativeHumidity: 0.5,
            WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 0.5,
            Coriolis: true, Spin: null, Hypotheses: 60, Seed: 42UL);
        var r = FlatAnalysis.Run(req, allowUncalibratedHypothesisCount: true);

        var html = CaseReport.Html(req, r, new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("seed <code>42</code>", html);
        Assert.Contains("<b>flat</b> over the search area", html);
        Assert.Contains("10000 m", html);
        Assert.Contains("Spin drift</td><td>off", html);
        Assert.Contains("CALIBRATION", html);
        Assert.Contains("95 % region area", html);
        Assert.Contains("<html", html);
    }

    [Fact]
    public void Coverage_FlatOriginPrior_Smoke()
    {
        var config = new CoverageConfig(
            LatDeg: 40.0,
            OriginHeightM: 0.0,
            Atmosphere: Std,
            Coriolis: true,
            Reference: DragTableCatalog.ByStandard("G1"),
            BcNominalLbIn2: 0.462,
            BcSigmaLbIn2: 0.462 * 0.03,
            LaunchElevationMinDeg: 0.0,
            LaunchElevationMaxDeg: 0.0,
            LaunchAzimuthDeg: 0.0,
            MvMinMps: 2550 * UnitConversions.FpsToMps,
            MvMaxMps: 2700 * UnitConversions.FpsToMps,
            WindEastMps: 0.0,
            WindNorthMps: 0.0,
            WindSigmaMps: 0.5,
            AngleSigmaDeg: 0.5,
            AzimuthSigmaDeg: 0.5,
            Spin: null,
            OriginRangeMinM: 3000.0,
            OriginRangeMaxM: 4500.0);

        var result = CoverageStudy.Run(config, trials: 12, hypothesesPerTrial: 80, seed: 20261030UL);

        Assert.True(result.Trials.Count >= 10, $"Too few completed trials: {result.Trials.Count}.");
        Assert.True(result.Coverage95 >= 0.75, $"95% coverage {result.Coverage95:P0} is far below nominal.");
    }

    [Fact]
    public void Coverage_TruthDrawnCalibration_Smoke()
    {
        var config = new CoverageConfig(
            LatDeg: 40.0,
            OriginHeightM: -5.0,
            Atmosphere: Std,
            Coriolis: true,
            Reference: DragTableCatalog.ByStandard("G1"),
            BcNominalLbIn2: 0.40,
            BcSigmaLbIn2: 0.01,
            LaunchElevationMinDeg: 20.0,
            LaunchElevationMaxDeg: 40.0,
            LaunchAzimuthDeg: 60.0,
            MvMinMps: 820.0,
            MvMaxMps: 880.0,
            WindEastMps: 2.0,
            WindNorthMps: -1.0,
            WindSigmaMps: 0.5,
            AngleSigmaDeg: 0.25,
            AzimuthSigmaDeg: 0.25,
            Spin: null);

        var result = CoverageStudy.Run(config, trials: 12, hypothesesPerTrial: 40, seed: 20261029UL);

        Assert.True(result.Trials.Count >= 8, $"Too few completed trials: {result.Trials.Count}.");
        Assert.True(result.Coverage95 >= 0.75, $"95% coverage {result.Coverage95:P0} is far below nominal.");
    }

    private static FlatForwardTruth Forward(
        IDragModel drag,
        FlatScene scene,
        double mv,
        double elevDeg,
        double azDeg,
        double windE,
        double windN,
        FlatSpin? spin,
        out double px,
        out double py,
        out double exterior,
        out double azimuth)
    {
        var model = new FlatTrajectoryModel(drag, scene.Atmosphere, scene.LatDeg, windE, windN, scene.Coriolis);
        var v0 = EnuFrame.DirectionFromAzimuthElevation(azDeg, elevDeg) * mv;
        var fwd = FlatIntegrators.Forward(model, new Cartesian3(0, 0, scene.OriginHeightM), v0);
        Assert.True(fwd.Hit);

        var impactPos = fwd.Position;
        var impactVel = fwd.Velocity;
        if (spin is not null)
        {
            double sg = SpinDrift.StabilityFactor(spin, mv);
            double az0 = EnuFrame.AzimuthDeg(fwd.Velocity);
            double drift = 0, lat = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                drift = SpinDrift.DisplacementM(sg, fwd.TimeOfFlightS, spin.RightHandTwist);
                lat = SpinDrift.LateralVelocityMps(sg, fwd.TimeOfFlightS, spin.RightHandTwist);
                az0 = EnuFrame.AzimuthDeg(fwd.Velocity + SpinDrift.RightOf(az0) * lat);
            }
            var right = SpinDrift.RightOf(az0);
            impactPos = fwd.Position + right * drift;
            impactVel = fwd.Velocity + right * lat;
        }

        px = impactPos.X;
        py = impactPos.Y;
        exterior = -EnuFrame.ElevationDeg(impactVel);
        azimuth = EnuFrame.AzimuthDeg(impactVel);
        return new FlatForwardTruth(impactPos, impactVel);
    }

    private sealed record FlatForwardTruth(Cartesian3 Impact, Cartesian3 Velocity);
}
