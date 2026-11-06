using FourTrac.Core.Atm;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Drag;
using FourTrac.Core.Geo;
using FourTrac.Core.Solver;

namespace _4TRAC.Tests;

/// <summary>
/// Solver-level verification (PLAN.md §7 V1 support): integrator agreement
/// (RK4 production vs adaptive RK45 reference, step-halving convergence),
/// wind-profile layering, Coriolis deflection signs, drag-model wiring and
/// bullet-profile JSON round-trips.
/// </summary>
public class ForwardSolverTests
{
    private static BulletProfile AnchorBullet() => new()
    {
        Name = "65gr BTHP anchor",
        MassGr = 65,
        DiameterIn = 0.224,
        DragMode = DragMode.ScalarBc,
        ReferenceStandard = "G1",
        BcLbIn2 = 0.365,
    };

    private static Atmosphere StdAtm() => Atmosphere.FromMeasurements(15.0, 101325.0, 0.5, 0.0);

    private static ShotScenario Scenario(double elevDeg = 0, double dt = 0.001, double tEnd = 3.0,
        WindProfile? wind = null, bool coriolis = false, double azimuthDeg = 0, double targetDropM = 5000,
        BulletProfile? bullet = null, double mvFps = 2600) => new()
    {
        LatitudeDeg = 45, LongitudeDeg = 0, HeightM = 0,
        AzimuthDeg = azimuthDeg, LaunchElevationDeg = elevDeg,
        MuzzleVelocityMps = mvFps * UnitConversions.FpsToMps,
        Bullet = bullet ?? AnchorBullet(), Atmosphere = StdAtm(),
        Wind = wind ?? WindProfile.Calm,
        IncludeCoriolis = coriolis,
        TargetHeightM = -targetDropM,
        MaxTimeS = tEnd,
        Dt = dt,
    };

    [Fact]
    public void Rk4OneMs_MatchesRk45Reference()
    {
        var s = Scenario(dt: 0.001, tEnd: 3.0);
        var sol = ForwardSolver.Solve(s);
        Assert.Equal(TrajectoryEndReason.TimeLimit, sol.Trajectory.EndReason);

        // Adaptive RK45 reference on the same model, same start, to exactly tEnd.
        // Compared as displacement from the muzzle so the RK45 error norm (relative
        // to the ECEF state) behaves sensibly.
        var (dispRk45, velRk45) = IntegrateRk45(s, 3.0, relTol: 1e-11, absTol: 1e-12);
        var start = EnuFrame.AtOrigin(s.LatitudeDeg, s.LongitudeDeg, s.HeightM).OriginEcef;
        var f = sol.Trajectory.FinalPosition;
        var dispRk4 = new[] { f.X - start.X, f.Y - start.Y, f.Z - start.Z };
        var velRk4 = new[] { sol.Trajectory.FinalVelocity.X, sol.Trajectory.FinalVelocity.Y, sol.Trajectory.FinalVelocity.Z };

        // RK4 truncation at 1 ms over 3 s is sub-micrometre; the tolerance here is
        // dominated by the RK45 reference's own relative-error allowance.
        Assert.True(Norm(Diff(dispRk4, dispRk45)) < 5e-4,
            $"RK4/RK45 displacement gap {Norm(Diff(dispRk4, dispRk45)):G6} m");
        Assert.True(Norm(Diff(velRk4, velRk45)) < 1e-5,
            $"RK4/RK45 velocity gap {Norm(Diff(velRk4, velRk45)):G6} m/s");

        // The RK45 reference itself must be tolerance-stable (it actually adapted:
        // two tolerance levels agree, and it flew the whole trajectory).
        var (disp2, vel2) = IntegrateRk45(s, 3.0, relTol: 1e-9, absTol: 1e-11);
        Assert.True(Norm(Diff(disp2, dispRk45)) < 5e-4);
        Assert.True(velRk45[0] + velRk45[1] + velRk45[2] != 0);
    }

    [Fact]
    public void Rk4_StepHalving_ConvergesAtFourthOrder()
    {
        // Smooth high-drag probe: a CONSTANT CD(Mach) table over the whole flight
        // (no interpolation kinks at all - the G-standard tables' transonic CD
        // kinks destroyed the h^4 scaling of the earlier low-BC probe, making
        // errors non-monotone between step sizes), 50 gr / .30 cal with a
        // 3160 fps muzzle so drag deceleration starts ~2.1 km/s^2: RK4 truncation
        // at dt 32 ms stays far above the ECEF round-off floor (~1e-7 m over the
        // ~760 m flight) while the smooth RHS lets the 4th-order ratio show.
        // What this test pins is the order of the scheme.
        var probe = new BulletProfile
        {
            Name = "smooth CD convergence probe",
            MassGr = 50,
            DiameterIn = 0.30,
            DragMode = DragMode.CdTable,
            CdPoints = new List<double[]>
            {
                new[] { 0.1, 0.25 },
                new[] { 1.0, 0.25 },
                new[] { 2.0, 0.25 },
                new[] { 3.0, 0.25 },
            },
        };

        var y32 = IntegrateRk4Positions(Scenario(bullet: probe, mvFps: 3160, dt: 0.032, tEnd: 2.0));
        var y16 = IntegrateRk4Positions(Scenario(bullet: probe, mvFps: 3160, dt: 0.016, tEnd: 2.0));
        var y8 = IntegrateRk4Positions(Scenario(bullet: probe, mvFps: 3160, dt: 0.008, tEnd: 2.0));

        double e1 = Norm(Diff(y32, y16));
        double e2 = Norm(Diff(y16, y8));

        // RK4: e(h,h/2)/e(h/2,h/4) -> 16. Assert >= 8 (conservative) with a
        // signal floor that keeps the ratio above the noise budget.
        Assert.True(e1 > 1e-6, $"step-halving signal {e1:G6} m too small for a stable ratio");
        Assert.True(e1 >= 8.0 * e2,
            $"convergence ratio {e1 / e2:F1} (expected ~16 for RK4): e(32ms)={e1:G6} m, e(16ms)={e2:G6} m");
    }

    [Fact]
    public void WindProfile_Layering_ConstantEquivalenceAndElevatedLayer()
    {
        // A single layer spanning all heights must be indistinguishable from Constant.
        var constant = WindProfile.Constant(10.0, 90.0);
        var singleLayer = new WindProfile(new[] { new WindProfile.Layer(1e6, 10.0, 90.0) });

        var a = ForwardSolver.Solve(Scenario(dt: 0.001, tEnd: 3.0, wind: constant));
        var b = ForwardSolver.Solve(Scenario(dt: 0.001, tEnd: 3.0, wind: singleLayer));
        Assert.True(Norm(Diff(Pos(a), Pos(b))) < 1e-9);
        Assert.True(Norm(Diff(Vel(a), Vel(b))) < 1e-9);

        // Wind confined below 20 m: the lob crosses the layer only at launch and
        // during the final descent to the deep target (which falls well below
        // the launch plane), so it must collect far less drift than the
        // constant-wind run - but not a tiny fraction, because that deep final
        // fall spends seconds re-crossing the layer.
        var layered = new WindProfile(new[]
        {
            new WindProfile.Layer(20.0, 10.0, 90.0),
            new WindProfile.Layer(40.0, 0.0, 0.0),
        });
        var c = ForwardSolver.Solve(Scenario(elevDeg: 30, dt: 0.001, tEnd: 60.0, wind: constant));
        var d = ForwardSolver.Solve(Scenario(elevDeg: 30, dt: 0.001, tEnd: 60.0, wind: layered));

        Assert.True(Math.Abs(c.CrossRangeM) > 5.0,
            $"constant-wind cross-range only {c.CrossRangeM:F3} m");
        Assert.True(Math.Abs(d.CrossRangeM) < 0.5 * Math.Abs(c.CrossRangeM),
            $"layered cross-range {d.CrossRangeM:F3} m vs constant {c.CrossRangeM:F3} m");
        Assert.True(Math.Abs(d.CrossRangeM) > 10.0,
            $"layered cross-range only {d.CrossRangeM:F3} m - the below-layer windows should still push");
    }

    [Theory]
    [InlineData(DragMode.ScalarBc)]
    [InlineData(DragMode.BcVelocity)]
    [InlineData(DragMode.CdTable)]
    public void BulletProfile_JsonRoundTrip_AllDragModes(DragMode mode)
    {
        var p = new BulletProfile
        {
            Name = $"Round trip {mode}",
            Caliber = "6.5 mm",
            MassGr = 147,
            DiameterIn = 0.264,
            DragMode = mode,
            ReferenceStandard = "G7",
            BcLbIn2 = mode == DragMode.ScalarBc ? 0.351 : null,
            BcPoints = mode == DragMode.BcVelocity
                ? new List<double[]> { new[] { 1000.0, 0.330 }, new[] { 2000.0, 0.360 }, new[] { 3000.0, 0.370 } }
                : null,
            CdPoints = mode == DragMode.CdTable
                ? new List<double[]> { new[] { 0.8, 0.390 }, new[] { 1.5, 0.320 }, new[] { 2.5, 0.240 } }
                : null,
            Provenance = "unit test",
        };

        string path = Path.Combine(AppContext.BaseDirectory, $"profile_roundtrip_{mode}.json");
        p.Save(path);
        var q = BulletProfile.Load(path);

        Assert.Equal(p.Name, q.Name);
        Assert.Equal(p.Caliber, q.Caliber);
        Assert.Equal(p.MassGr, q.MassGr);
        Assert.Equal(p.DiameterIn, q.DiameterIn);
        Assert.Equal(p.DragMode, q.DragMode);
        Assert.Equal(p.ReferenceStandard, q.ReferenceStandard);
        Assert.Equal(p.BcLbIn2, q.BcLbIn2);
        Assert.Equal(p.Provenance, q.Provenance);
        if (p.BcPoints is not null)
            Assert.Equal(p.BcPoints.Count, q.BcPoints!.Count);
        if (p.CdPoints is not null)
            Assert.Equal(p.CdPoints.Count, q.CdPoints!.Count);

        // The reloaded profile must build a numerically identical drag model.
        var before = p.CreateDragModel();
        var after = q.CreateDragModel();
        foreach (var (s, m) in new[] { (300.0, 0.9), (500.0, 1.5), (700.0, 2.2) })
            Assert.Equal(before.Beta(s, m), after.Beta(s, m), 12);
    }

    [Fact]
    public void ScalarBcProfile_Wiring_MatchesDirectDragModel()
    {
        var profile = AnchorBullet();
        var viaProfile = profile.CreateDragModel();
        var direct = new BcDragModel(
            DragTableCatalog.ByStandard("G1"),
            new ConstantBcProvider(0.365));

        foreach (var (s, m) in new[] { (250.0, 0.8), (400.0, 1.2), (600.0, 1.8), (800.0, 2.5) })
            Assert.Equal(direct.Beta(s, m), viaProfile.Beta(s, m), 15);
    }

    [Fact]
    public void Coriolis_NorthDeflectsRight_EastGetsEotvosLift()
    {
        // 1.5 s of flight at 45 N: expected deflections ~0.08 m (2*Omega*sin(phi)*v*t^2/2).
        var northOn = ForwardSolver.Solve(Scenario(tEnd: 1.5, coriolis: true));
        var northOff = ForwardSolver.Solve(Scenario(tEnd: 1.5, coriolis: false));
        var eastOn = ForwardSolver.Solve(Scenario(tEnd: 1.5, coriolis: true, azimuthDeg: 90));
        var eastOff = ForwardSolver.Solve(Scenario(tEnd: 1.5, coriolis: false, azimuthDeg: 90));

        // North shot: Coriolis deflects right (east) - right-positive lateral > 0.
        Assert.True(northOn.CrossRangeM > 0.02,
            $"north-shot Coriolis lateral {northOn.CrossRangeM:F4} m (expected eastward ~0.08 m)");
        Assert.True(Math.Abs(northOff.CrossRangeM) < 1e-6,
            $"calm no-Coriolis lateral should vanish, got {northOff.CrossRangeM:G6} m");

        // East shot: Eotvos - the eastward component of velocity reduces apparent
        // gravity, so the Coriolis run lands measurably higher at the same time.
        Assert.True(eastOn.DropM > eastOff.DropM + 0.02,
            $"Eotvos lift only {eastOn.DropM - eastOff.DropM:F4} m at 1.5 s");

        // East shot: Coriolis lateral is right of heading (south) > 0.
        Assert.True(eastOn.CrossRangeM > 0.01,
            $"east-shot Coriolis lateral {eastOn.CrossRangeM:F4} m (expected southward)");
    }

    // ---- helpers ----------------------------------------------------------------

    private static double[] Pos(ForwardSolution s) =>
        new[] { s.Trajectory.FinalPosition.X, s.Trajectory.FinalPosition.Y, s.Trajectory.FinalPosition.Z };

    private static double[] Vel(ForwardSolution s) =>
        new[] { s.Trajectory.FinalVelocity.X, s.Trajectory.FinalVelocity.Y, s.Trajectory.FinalVelocity.Z };

    private static double[] Diff(double[] a, double[] b)
    {
        var d = new double[a.Length];
        for (int i = 0; i < a.Length; i++) d[i] = a[i] - b[i];
        return d;
    }

    private static double Norm(double[] v) => Math.Sqrt(v.Sum(x => x * x));

    /// <summary>RK4 final state via the production engine at exactly tEnd (TimeLimit).</summary>
    private static double[] IntegrateRk4Positions(ShotScenario s)
    {
        var scene = EnuFrame.AtOrigin(s.LatitudeDeg, s.LongitudeDeg, s.HeightM);
        var model = new TrajectoryModel(s.Bullet.CreateDragModel(), s.Atmosphere, s.Wind, scene,
            includeDrag: true, includeCoriolis: s.IncludeCoriolis);
        var v0 = scene.VectorToEcef(EnuFrame.DirectionFromAzimuthElevation(s.AzimuthDeg, s.LaunchElevationDeg)) * s.MuzzleVelocityMps;
        var result = new TrajectoryEngine().Integrate(model, scene.OriginEcef, v0, new TrajectoryOptions
        {
            Dt = s.Dt,
            MaxTime = s.MaxTimeS,
            TargetHeightM = s.TargetHeightM!.Value,
            MinSpeedMps = 1.0,
            RecordSamples = false,
        });
        Assert.Equal(TrajectoryEndReason.TimeLimit, result.EndReason);
        return new[]
        {
            result.FinalPosition.X, result.FinalPosition.Y, result.FinalPosition.Z,
        };
    }

    /// <summary>Adaptive RK45 reference trajectory to exactly tEnd; returns displacement and velocity.</summary>
    private static (double[] Disp, double[] Vel) IntegrateRk45(ShotScenario s, double tEnd,
        double relTol, double absTol)
    {
        var scene = EnuFrame.AtOrigin(s.LatitudeDeg, s.LongitudeDeg, s.HeightM);
        var model = new TrajectoryModel(s.Bullet.CreateDragModel(), s.Atmosphere, s.Wind, scene,
            includeDrag: true, includeCoriolis: s.IncludeCoriolis);
        var v0 = scene.VectorToEcef(EnuFrame.DirectionFromAzimuthElevation(s.AzimuthDeg, s.LaunchElevationDeg)) * s.MuzzleVelocityMps;
        var y = new double[]
        {
            scene.OriginEcef.X, scene.OriginEcef.Y, scene.OriginEcef.Z,
            v0.X, v0.Y, v0.Z,
        };

        var rk = new Rk45(relTol, absTol);
        double t = 0, h = 1e-4;
        while (t < tEnd)
        {
            var (taken, next) = rk.Step(model, t, y, Math.Min(h, tEnd - t), tEnd - t);
            t += taken;
            h = Math.Min(next, 1.0);
        }

        return (
            new[] { y[0] - scene.OriginEcef.X, y[1] - scene.OriginEcef.Y, y[2] - scene.OriginEcef.Z },
            new[] { y[3], y[4], y[5] });
    }
}
