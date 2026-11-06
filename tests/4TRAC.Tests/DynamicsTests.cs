using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// V0 code verification for the equations of motion (PLAN.md §7):
/// gravity field probes at equator/pole/mid-latitude, Coriolis sign and
/// magnitude against closed-form ENU expressions, wind frame handling, the
/// vacuum parabola against the analytic flat-Earth solution, and the quadratic
/// wind-retardation law.
/// </summary>
public class DynamicsTests
{
    private static TrajectoryModel MakeModel(
        bool includeDrag = true, bool includeCoriolis = true, WindProfile? wind = null)
    {
        var drag = new BcDragModel(DragTableCatalog.G1, new ConstantBcProvider(0.2));
        var atm = Atmosphere.IsaAtElevation(0.0);
        return new TrajectoryModel(drag, atm, Cartesian3.Zero, includeDrag, includeCoriolis);
    }

    // ---------------------------------------------------------------- gravity

    [Theory]
    [InlineData(-90.0)]
    [InlineData(-45.0)]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(89.0)]
    public void StationaryProbe_GravityMatchesNormalGravityModel(double latDeg)
    {
        foreach (double height in new[] { 0.0, 1000.0 })
        {
            var model = MakeModel(includeCoriolis: false);
            var r = Geodetic.LlaToEcef(latDeg, 30.0, height);
            var y = new[] { r.X, r.Y, r.Z, 0.0, 0.0, 0.0 };
            var dydt = new double[6];
            model.Derivatives(0.0, y, dydt);

            var a = new Cartesian3(dydt[3], dydt[4], dydt[5]);
            double expected = Wgs84.NormalGravity(latDeg, height);

            Assert.True(Math.Abs(a.Length - expected) / expected < 1e-12,
                $"|g| mismatch at lat={latDeg}, h={height}: {a.Length} vs {expected}");

            // Direction: exactly opposite the geodetic normal.
            var normal = Geodetic.GeodeticNormal(latDeg, 30.0);
            double alignment = Cartesian3.Dot(a, normal) / a.Length;
            Assert.True(Math.Abs(alignment + 1.0) < 1e-12,
                $"gravity not anti-parallel to normal at lat={latDeg}: dot={alignment}");
        }
    }

    [Fact]
    public void StationaryProbe_CoriolisVanishesAtRest()
    {
        var model = MakeModel(includeCoriolis: true);
        var r = Geodetic.LlaToEcef(45.0, 0.0, 0.0);
        var y = new[] { r.X, r.Y, r.Z, 0.0, 0.0, 0.0 };
        var withCoriolis = new double[6];
        model.Derivatives(0.0, y, withCoriolis);
        var noCoriolis = new double[6];
        MakeModel(includeCoriolis: false).Derivatives(0.0, y, noCoriolis);
        for (int i = 3; i < 6; i++)
            Assert.Equal(noCoriolis[i], withCoriolis[i], 15);
    }

    [Fact]
    public void NormalGravity_KnownValues()
    {
        // WGS84 normal-gravity anchors, consistent with the constants this code
        // uses (NIMA TR8350.2 form: gamma_e = 9.7803253359, k = 0.00193185265241).
        // Note: the GRS80 system quotes a pole value 1.4e-6 m/s^2 higher - far
        // below any ballistics-relevant threshold (~1e-5 m/s velocity error over
        // a 7 s flight); we pin our own system's consistent values here.
        Assert.Equal(9.7803253359, Wgs84.NormalGravity(0.0, 0.0), 9);
        Assert.Equal(9.8321849379, Wgs84.NormalGravity(90.0, 0.0), 9);
        // Free-air gradient: -3.086e-6 m/s^2 per metre near the equator.
        double dg = Wgs84.NormalGravity(0.0, 0.0) - Wgs84.NormalGravity(0.0, 1000.0);
        Assert.True(Math.Abs(dg - 0.003086) < 1e-5, $"free-air drop over 1000 m: {dg}");
    }

    // --------------------------------------------------------------- Coriolis

    [Theory]
    [InlineData(45.0, 100.0, 0.0)]   // eastward at 45 N: deflects south + Eötvös up
    [InlineData(45.0, 0.0, 100.0)]   // northward at 45 N: deflects east
    [InlineData(-45.0, 0.0, 100.0)]  // northward at 45 S: deflects west
    public void Coriolis_MatchesClosedFormEnuExpression(double latDeg, double vEast, double vNorth)
    {
        var model = MakeModel(includeDrag: false, includeCoriolis: true);
        var frame = EnuFrame.AtOrigin(latDeg, 0.0, 1000.0);
        var r = frame.OriginEcef;
        var v = frame.East * vEast + frame.North * vNorth;
        var y = new[] { r.X, r.Y, r.Z, v.X, v.Y, v.Z };
        var dydt = new double[6];
        model.Derivatives(0.0, y, dydt);

        var aTotal = new Cartesian3(dydt[3], dydt[4], dydt[5]);

        // Subtract analytic gravity.
        Geodetic.EcefToLla(r, out double lat, out double lon, out double h);
        var g = Geodetic.GeodeticNormal(lat, lon) * (-Wgs84.NormalGravity(lat, h));
        var aCor = aTotal - g;

        // Project into ENU: a_cor = -2 Omega x v with Omega in ENU = (0, cos phi, sin phi).
        double phi = latDeg * Math.PI / 180.0;
        double omega = Wgs84.Omega;
        var expectedEnu = new Cartesian3(
            2.0 * omega * Math.Sin(phi) * vNorth,          // east
            -2.0 * omega * Math.Sin(phi) * vEast,          // north
            2.0 * omega * Math.Cos(phi) * vEast);          // up (Eötvös)

        double e = Cartesian3.Dot(frame.East, aCor);
        double n = Cartesian3.Dot(frame.North, aCor);
        double u = Cartesian3.Dot(frame.Up, aCor);

        Assert.Equal(expectedEnu.X, e, 10);
        Assert.Equal(expectedEnu.Y, n, 10);
        Assert.Equal(expectedEnu.Z, u, 10);
    }

    // ------------------------------------------------------------------- wind

    [Fact]
    public void WindProfile_FromDirectionConvention()
    {
        // Wind FROM east (90 deg) blows toward west: ENU = (-5, 0, 0).
        var profile = WindProfile.Constant(5.0, 90.0);
        var enu = profile.WindVectorEnu(10.0);
        Assert.Equal(-5.0, enu.X, 12);
        Assert.Equal(0.0, enu.Y, 12);
        Assert.Equal(0.0, enu.Z, 12);

        // FROM north: blows toward south.
        var fromNorth = WindProfile.Constant(3.0, 0.0).WindVectorEnu(0.0);
        Assert.Equal(0.0, fromNorth.X, 12);
        Assert.Equal(-3.0, fromNorth.Y, 12);
    }

    [Fact]
    public void WindProfile_EcefRoundTrip()
    {
        var frame = EnuFrame.AtOrigin(45.0, 10.0, 0.0);
        var profile = WindProfile.Constant(5.0, 90.0);
        var wEcef = profile.WindVectorEcef(10.0, frame);
        Assert.Equal(5.0, wEcef.Length, 9);

        double e = Cartesian3.Dot(frame.East, wEcef);
        double n = Cartesian3.Dot(frame.North, wEcef);
        double u = Cartesian3.Dot(frame.Up, wEcef);
        Assert.Equal(-5.0, e, 9);
        Assert.Equal(0.0, n, 9);
        Assert.Equal(0.0, u, 9);
    }

    [Fact]
    public void WindProfile_LayerInterpolation()
    {
        var profile = new WindProfile(new[]
        {
            new WindProfile.Layer(0.0, 5.0, 0.0),    // from north at surface
            new WindProfile.Layer(30.0, 15.0, 180.0) // from south at 30 m
        });
        // Midpoint: vectors (0,-5) and (0,+15) interpolate to (0, +5) -> toward north.
        var mid = profile.WindVectorEnu(15.0);
        Assert.Equal(0.0, mid.X, 9);
        Assert.Equal(5.0, mid.Y, 9);

        // Clamped outside span.
        Assert.Equal(-5.0, profile.WindVectorEnu(-10.0).Y, 9);
        Assert.Equal(15.0, profile.WindVectorEnu(100.0).Y, 9);
    }

    [Fact]
    public void Wind_QuadraticRetardationLaw()
    {
        // Constant-CD bullet: retardation ratio under a 20 m/s headwind must be
        // exactly (120/100)^2 = 1.44.
        var cd = DragTable.FromPoints(new[] { (0.0, 0.3), (5.0, 0.3) });
        var drag = new CdTableDragModel(cd, 0.00782, 0.0109);
        var atm = Atmosphere.IsaAtElevation(0.0);
        var frame = EnuFrame.AtOrigin(0.0, 0.0, 100.0);
        var r = frame.OriginEcef;
        var v = frame.East * 100.0;

        Cartesian3 DragAccel(WindProfile windProfile)
        {
            var windEcef = windProfile.WindVectorEcef(100.0, frame);
            var model = new TrajectoryModel(drag, atm, windEcef, includeDrag: true, includeCoriolis: false);
            var y = new[] { r.X, r.Y, r.Z, v.X, v.Y, v.Z };
            var dydt = new double[6];
            model.Derivatives(0.0, y, dydt);
            var aTotal = new Cartesian3(dydt[3], dydt[4], dydt[5]);
            var g = Geodetic.GeodeticNormal(0.0, 0.0) * (-Wgs84.NormalGravity(0.0, 100.0));
            return aTotal - g;
        }

        var still = DragAccel(WindProfile.Calm);
        var headwind = DragAccel(WindProfile.Constant(20.0, 90.0)); // from east -> toward west

        double ratio = headwind.Length / still.Length;
        Assert.True(Math.Abs(ratio - 1.44) < 1e-9, $"retardation ratio {ratio}");

        // Drag opposes air-relative motion: at lon 0 the ENU "east" axis is +Y
        // in ECEF, so project rather than assuming axes.
        Assert.True(Math.Abs(Cartesian3.Dot(frame.East, headwind) + headwind.Length) < 1e-9,
            $"drag not anti-parallel to east: {headwind}");
        Assert.True(Math.Abs(Cartesian3.Dot(frame.North, headwind)) < 1e-9);
        Assert.True(Math.Abs(Cartesian3.Dot(frame.Up, headwind)) < 1e-9);
    }

    // --------------------------------------------------- analytic parabola

    [Fact]
    public void VacuumTrajectory_MatchesFlatEarthParabola()
    {
        // 50 m/s at 45 deg from the equator, flying north. Analytic flat-Earth
        // vacuum solution (g = normal gravity at the equator):
        //   R    = v^2 sin(2 theta) / g = 2500 / 9.7803253359 ~ 255.6152 m
        //   hMax = v^2 sin^2(theta) / (2 g) = 625 / g          ~  63.9038 m
        //   T    = 2 v sin(theta) / g                           ~   7.2299 s
        // Expected deviation of the ECEF solution: curvature of the target
        // surface (~5 mm of extra drop -> ~5 mm extra range) plus the altitude
        // variation of gravity along the arc (~3 mm). Budget: 0.02 m.
        double g = Wgs84.NormalGravity(0.0, 0.0);
        double v0 = 50.0;
        double expectedRange = v0 * v0 / g;
        double expectedApex = v0 * v0 * 0.5 / (2.0 * g);
        double expectedTof = 2.0 * v0 * Math.Sin(Math.PI / 4.0) / g;

        var model = MakeModel(includeDrag: false, includeCoriolis: false);
        var frame = EnuFrame.AtOrigin(0.0, 0.0, 0.0);
        var dir = EnuFrame.DirectionFromAzimuthElevation(0.0, 45.0); // due north, 45 deg up
        var vEcef = frame.East * (dir.X * v0) + frame.North * (dir.Y * v0) + frame.Up * (dir.Z * v0);

        var engine = new TrajectoryEngine();
        var result = engine.Integrate(model, frame.OriginEcef, vEcef, new TrajectoryOptions
        {
            Dt = 0.001,
            TargetHeightM = 0.0,
        });

        Assert.Equal(TrajectoryEndReason.GroundHit, result.EndReason);

        var impactEnu = frame.ToEnu(result.FinalPosition);
        Assert.True(Math.Abs(impactEnu.X) < 0.01, $"lateral drift {impactEnu.X}");  // due north, no east/west
        Assert.True(impactEnu.Y > 0, "impact should be north of launch");
        Assert.True(Math.Abs(impactEnu.Y - expectedRange) < 0.02,
            $"range {impactEnu.Y} vs expected {expectedRange}");

        double apex = result.Samples.Max(s => s.HeightM);
        Assert.True(Math.Abs(apex - expectedApex) < 0.02, $"apex {apex} vs expected {expectedApex}");

        Assert.True(Math.Abs(result.TimeOfFlight - expectedTof) < 0.005,
            $"TOF {result.TimeOfFlight} vs expected {expectedTof}");

        // Impact speed: symmetric launch and impact heights -> speed returns to
        // v0 in the flat vacuum limit (energy conservation). The vertical
        // component reverses; the horizontal stays.
        Assert.True(Math.Abs(result.FinalSpeed - v0) < 0.02,
            $"impact speed {result.FinalSpeed} vs expected {v0}");
        var impactVelocityEnu = new Cartesian3(
            Cartesian3.Dot(frame.East, result.FinalVelocity),
            Cartesian3.Dot(frame.North, result.FinalVelocity),
            Cartesian3.Dot(frame.Up, result.FinalVelocity));
        Assert.True(Math.Abs(impactVelocityEnu.Y - v0 * Math.Sqrt(0.5)) < 0.05,
            $"horizontal component {impactVelocityEnu.Y}");
        Assert.True(Math.Abs(impactVelocityEnu.Z + v0 * Math.Sqrt(0.5)) < 0.05,
            $"vertical component {impactVelocityEnu.Z}");
    }

    [Fact]
    public void StartedBelowTarget_ReportsImmediately()
    {
        var model = MakeModel();
        var r = Geodetic.LlaToEcef(0.0, 0.0, 0.0);
        var engine = new TrajectoryEngine();
        var result = engine.Integrate(model, r, Cartesian3.Zero, new TrajectoryOptions { TargetHeightM = 10.0 });
        Assert.Equal(TrajectoryEndReason.StartedBelowTarget, result.EndReason);
    }

    [Fact]
    public void TimeLimit_TerminatesLongTrajectories()
    {
        var model = MakeModel(includeDrag: false, includeCoriolis: false);
        var frame = EnuFrame.AtOrigin(0.0, 0.0, 0.0);
        // Straight up: hangs above target height until MaxTime.
        var vUp = frame.Up * 50.0;
        var engine = new TrajectoryEngine();
        var result = engine.Integrate(model, frame.OriginEcef, vUp, new TrajectoryOptions
        {
            Dt = 0.01,
            MaxTime = 5.0,
            RecordSamples = false,
        });
        Assert.Equal(TrajectoryEndReason.TimeLimit, result.EndReason);
        Assert.Equal(5.0, result.TimeOfFlight, 12);
    }
}
