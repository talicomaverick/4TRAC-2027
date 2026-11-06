using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Solver;

/// <summary>
/// Interpolated trajectory state at a downrange distance (drop-chart row).
/// Height is relative to the launch plane; Lateral is right-positive relative
/// to the fired azimuth.
/// </summary>
public readonly record struct RangePoint(
    double RangeM,
    double TimeS,
    double SpeedMps,
    double Mach,
    double HeightM,
    double LateralM,
    double EnergyJ);

/// <summary>Impact kinematics at the target plane.</summary>
public sealed record ForwardImpact(
    double LatitudeDeg,
    double LongitudeDeg,
    double HeightM,
    double SpeedMps,
    double Mach,
    double EnergyJ,
    /// <summary>Incidence angle (deg below horizontal, positive down - PLAN.md normative).</summary>
    double IncidenceAngleDeg,
    /// <summary>Azimuth of the impact velocity vector (deg from true north).</summary>
    double VelocityAzimuthDeg);

/// <summary>Result of a forward solve: trajectory plus derived forensic observables.</summary>
public sealed class ForwardSolution
{
    private readonly double[] _range;      // downrange per sample (m)
    private readonly double[] _lateral;    // right-positive lateral per sample (m)

    public TrajectoryResult Trajectory { get; }
    public EnuFrame Scene { get; }
    public ShotScenario Scenario { get; }

    public ForwardImpact Impact { get; }
    public double TimeOfFlightS { get; }
    /// <summary>Downrange distance muzzle -> impact (m, launch-plane projection).</summary>
    public double DownRangeM { get; }
    /// <summary>Lateral impact offset, right-positive relative to fired azimuth (m).</summary>
    public double CrossRangeM { get; }
    /// <summary>Impact height above the launch plane (m; negative = below).</summary>
    public double DropM { get; }
    /// <summary>Retained velocity fraction at impact.</summary>
    public double RetainedVelocityFraction { get; }

    internal ForwardSolution(
        TrajectoryResult trajectory, EnuFrame scene, ShotScenario scenario,
        ForwardImpact impact, double timeOfFlightS, double downRangeM,
        double crossRangeM, double dropM, double[] range, double[] lateral)
    {
        Trajectory = trajectory;
        Scene = scene;
        Scenario = scenario;
        Impact = impact;
        TimeOfFlightS = timeOfFlightS;
        DownRangeM = downRangeM;
        CrossRangeM = crossRangeM;
        DropM = dropM;
        _range = range;
        _lateral = lateral;
        RetainedVelocityFraction = scenario.MuzzleVelocityMps > 0
            ? impact.SpeedMps / scenario.MuzzleVelocityMps
            : double.NaN;
    }

    /// <summary>
    /// Interpolated trajectory state at a downrange distance (m). Returns null
    /// beyond the last recorded sample. Linear interpolation between 1 ms
    /// samples: height error &lt; 3e-6 m, speed error &lt; 1e-4 m/s at rifle decelerations.
    /// </summary>
    public RangePoint? AtDownRange(double rangeM)
    {
        int n = _range.Length;
        if (n < 2 || rangeM < _range[0] || rangeM > _range[^1]) return null;

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_range[mid] <= rangeM) lo = mid;
            else hi = mid;
        }

        double span = _range[hi] - _range[lo];
        double t = span > 1e-12 ? (rangeM - _range[lo]) / span : 0.0;
        var a = Trajectory.Samples[lo];
        var b = Trajectory.Samples[hi];

        double speed = a.Speed + t * (b.Speed - a.Speed);
        double massKg = Scenario.Bullet.MassGr * UnitConversions.GrToKg;
        return new RangePoint(
            rangeM,
            a.T + t * (b.T - a.T),
            speed,
            a.Mach + t * (b.Mach - a.Mach),
            a.HeightM - Scenario.HeightM + t * ((b.HeightM - a.HeightM)),
            _lateral[lo] + t * (_lateral[hi] - _lateral[lo]),
            0.5 * massKg * speed * speed);
    }
}

/// <summary>
/// Forward solve: given launch conditions, integrate the trajectory and report
/// the impact point and every observable the inverse problem will condition on.
/// </summary>
public static class ForwardSolver
{
    /// <summary>Solve a forward trajectory.</summary>
    public static ForwardSolution Solve(ShotScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        scenario.Bullet.Validate();
        if (scenario.MuzzleVelocityMps <= 0)
            throw new ArgumentOutOfRangeException(nameof(scenario), "Muzzle velocity must be positive.");

        var scene = EnuFrame.AtOrigin(scenario.LatitudeDeg, scenario.LongitudeDeg, scenario.HeightM);
        double targetHeight = scenario.TargetHeightM ?? scenario.HeightM;

        var launchDirEnu = EnuFrame.DirectionFromAzimuthElevation(scenario.AzimuthDeg, scenario.LaunchElevationDeg);
        var startVelocity = scene.VectorToEcef(launchDirEnu) * scenario.MuzzleVelocityMps;

        var model = new TrajectoryModel(
            scenario.Bullet.CreateDragModel(),
            scenario.Atmosphere,
            scenario.Wind,
            scene,
            includeDrag: true,
            includeCoriolis: scenario.IncludeCoriolis);

        var engine = new TrajectoryEngine();
        var result = engine.Integrate(model, scene.OriginEcef, startVelocity, new TrajectoryOptions
        {
            Dt = scenario.Dt,
            MaxTime = scenario.MaxTimeS,
            TargetHeightM = targetHeight,
            RecordSamples = true,
        });

        // Scene-frame observables per sample: downrange (along the fired
        // azimuth) and lateral (right-positive).
        int count = result.Samples.Count;
        var range = new double[count];
        var lateral = new double[count];
        double azRad = scenario.AzimuthDeg * UnitConversions.DegToRad;
        double sinAz = Math.Sin(azRad), cosAz = Math.Cos(azRad);
        for (int i = 0; i < count; i++)
        {
            var enu = scene.ToEnu(result.Samples[i].Position);
            range[i] = enu.X * sinAz + enu.Y * cosAz;
            lateral[i] = enu.X * cosAz - enu.Y * sinAz;
        }

        var impactEnu = scene.ToEnu(result.FinalPosition);
        var impactVelEnu = scene.VectorToEnu(result.FinalVelocity);
        double incidence = -EnuFrame.ElevationDeg(impactVelEnu); // positive = descending below horizontal
        double massKg = scenario.Bullet.MassGr * UnitConversions.GrToKg;
        var impact = new ForwardImpact(
            LatitudeDeg: 0, LongitudeDeg: 0, // filled below from ECEF
            result.FinalHeightM,
            result.FinalSpeed,
            result.FinalMach,
            0.5 * massKg * result.FinalSpeed * result.FinalSpeed,
            incidence,
            EnuFrame.AzimuthDeg(impactVelEnu));

        Geodetic.EcefToLla(result.FinalPosition, out double latDeg, out double lonDeg, out _);
        impact = impact with { LatitudeDeg = latDeg, LongitudeDeg = lonDeg };

        return new ForwardSolution(
            result, scene, scenario, impact,
            result.TimeOfFlight,
            range.Length > 0 ? range[^1] : double.NaN,
            impactEnu.X * cosAz - impactEnu.Y * sinAz,
            impactEnu.Z,
            range, lateral);
    }
}
