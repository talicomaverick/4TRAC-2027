using FourTrac.Core.Atm;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;
using FourTrac.Core.Solver;

namespace FourTrac.Validation;

/// <summary>
/// Fixed scene constants of the V2 protocol - declared, not hidden: every
/// value here is a deliberate choice of the validation design and is repeated
/// in the generated report.
/// </summary>
public static class V2Scene
{
    public const double LatitudeDeg = 45.0;      // mid-latitude: Coriolis representative
    public const double LongitudeDeg = 7.5;
    public const double HeightM = 100.0;         // launch plane height above ellipsoid
    public const double TruthBc = 0.40;          // scalar G1 reference bullet
    public const double MinEligibleAngleDeg = 15.0;   // eligibility floor for the origin-error criterion
    public const double MaxEligibleRangeM = 1000.0;

    public static Atmosphere Atmosphere { get; } = Atmosphere.IsaAtElevation(HeightM);

    public static BulletProfile Bullet { get; } = new()
    {
        Name = "V2 reference bullet (scalar G1)",
        MassGr = 10.6,
        DragMode = DragMode.ScalarBc,
        ReferenceStandard = "G1",
        BcLbIn2 = TruthBc,
    };
}

/// <summary>Deterministic truth trajectory for one scene combo.</summary>
public sealed record TruthScene(
    SceneCombo Combo,
    double MuzzleVelocityMps,
    double ExteriorAngleDeg,
    double TravelAzimuthDeg,
    double ImpactSpeedMps,
    double TimeOfFlightS,
    Cartesian3 OriginPositionEcef,
    Cartesian3 ImpactPositionEcef,
    EnuFrame ImpactFrame,
    double TruthWindEastMps,
    double TruthWindNorthMps);

/// <summary>
/// Builds the forward truth for a design combo: the muzzle velocity is solved
/// (secant on the monotone range(MV)) so the shot lands exactly at the
/// cell's range; the impact observables are extracted in the impact-anchored
/// frame, the frame the inverse reports in (M3.1/M3.2 convention).
/// </summary>
public static class SceneFactory
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double RangeToleranceM = 0.05;     // 5 cm on the solved range
    private const int MaxSecantIterations = 12;

    public static bool TrySolveCombo(SceneCombo combo, out TruthScene scene, out string exclusionReason)
    {
        ArgumentNullException.ThrowIfNull(combo);
        scene = null!;
        exclusionReason = string.Empty;

        // Full-value crosswind: air perpendicular to the line of fire (worst
        // lateral deflection per unit wind; direction choice is documented).
        double windFromDeg = (combo.AzimuthDeg + 90.0) % 360.0;
        var wind = combo.WindSpeedMps <= 0.0
            ? WindProfile.Calm
            : WindProfile.Constant(combo.WindSpeedMps, windFromDeg);

        double RangeAt(double mv) =>
            ForwardSolver.Solve(BuildScenario(combo, mv, wind)).DownRangeM;

        // Secant on f(mv) = range(mv) - target; range is strictly increasing
        // in MV, so the root is unique. Start from the vacuum estimate
        // R = mv^2 sin(2 theta)/g inflated by 6 % for drag.
        double sin2 = Math.Sin(2.0 * combo.LaunchElDeg * Deg2Rad);
        double mvPrev = Math.Sqrt(Math.Max(1.0, combo.RangeM * 9.80665 / Math.Max(1e-3, sin2))) * 1.0;
        double fPrev = RangeAt(mvPrev) - combo.RangeM;
        double mvCur = mvPrev * 1.06;
        double fCur = RangeAt(mvCur) - combo.RangeM;
        double mv = mvCur;
        bool converged = false;

        for (int i = 0; i < MaxSecantIterations && !converged; i++)
        {
            double denom = fCur - fPrev;
            double mvNext = Math.Abs(denom) < 1e-9
                ? mvCur * 1.02
                : mvCur - fCur * (mvCur - mvPrev) / denom;
            mvNext = Math.Clamp(mvNext, 0.5 * mvPrev, 2.5 * mvCur);

            double fNext = RangeAt(mvNext) - combo.RangeM;
            mvPrev = mvCur; fPrev = fCur;
            mvCur = mvNext; fCur = fNext;
            mv = mvNext;
            converged = Math.Abs(fNext) <= RangeToleranceM;
        }

        if (!converged)
        {
            exclusionReason =
                $"MV solve for range {combo.RangeM:F0} m at {combo.LaunchElDeg:F0} deg did not converge " +
                $"(last residual {fCur:F2} m after {MaxSecantIterations} iterations)";
            return false;
        }

        // Table-validity gate: the solved muzzle velocity must stay above the
        // Mach 0.2 floor (see DesignMatrix.MinValidMach for the rationale);
        // everything below is a degenerate lob no drag standard describes.
        if (mv < DesignMatrix.MinValidMuzzleVelocityMps)
        {
            exclusionReason =
                $"solved MV {mv:F1} m/s is below the Mach {DesignMatrix.MinValidMach:F1} validity floor " +
                $"({DesignMatrix.MinValidMuzzleVelocityMps:F1} m/s at the V2 scene) - " +
                "the cell asks for a lob no drag standard meaningfully describes";
            return false;
        }

        var forward = ForwardSolver.Solve(BuildScenario(combo, mv, wind));
        if (forward.Trajectory.EndReason != TrajectoryEndReason.GroundHit)
        {
            exclusionReason = $"forward truth ended with {forward.Trajectory.EndReason}";
            return false;
        }

        var impactPos = forward.Trajectory.FinalPosition;
        Geodetic.EcefToLla(impactPos, out double iLat, out double iLon, out double iH);
        var impactFrame = EnuFrame.AtOrigin(iLat, iLon, iH);
        var vEnu = impactFrame.VectorToEnu(forward.Trajectory.FinalVelocity);

        // Truth wind at the impact anchor, in scene ENU components - the
        // measurement the inverse's wind prior is centred on.
        var windEnu = impactFrame.VectorToEnu(wind.WindVectorEcef(0.0, forward.Scene));

        scene = new TruthScene(
            Combo: combo,
            MuzzleVelocityMps: mv,
            ExteriorAngleDeg: -EnuFrame.ElevationDeg(vEnu),
            TravelAzimuthDeg: EnuFrame.AzimuthDeg(vEnu),
            ImpactSpeedMps: forward.Trajectory.FinalSpeed,
            TimeOfFlightS: forward.TimeOfFlightS,
            OriginPositionEcef: forward.Scene.OriginEcef,
            ImpactPositionEcef: impactPos,
            ImpactFrame: impactFrame,
            TruthWindEastMps: windEnu.X,
            TruthWindNorthMps: windEnu.Y);
        return true;
    }

    private static ShotScenario BuildScenario(SceneCombo combo, double mv, WindProfile wind) => new()
    {
        LatitudeDeg = V2Scene.LatitudeDeg,
        LongitudeDeg = V2Scene.LongitudeDeg,
        HeightM = V2Scene.HeightM,
        AzimuthDeg = combo.AzimuthDeg,
        LaunchElevationDeg = combo.LaunchElDeg,
        MuzzleVelocityMps = mv,
        Bullet = V2Scene.Bullet,
        Atmosphere = V2Scene.Atmosphere,
        Wind = wind,
        IncludeCoriolis = true,               // production path, both directions
        TargetHeightM = V2Scene.HeightM,      // flat scene (no DEM - V3/M4)
        MaxTimeS = 60.0,
    };
}
