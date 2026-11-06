using FourTrac.Core.Atm;
using FourTrac.Core.Dynamics;

namespace FourTrac.Core.Solver;

/// <summary>
/// Everything needed for one forward trajectory solve (PLAN.md §3/§5).
/// Angles: azimuth clockwise from true north; launch elevation degrees above the
/// horizontal plane (the forensic incidence angle is its negative: a +2 deg
/// launch = -2 deg incidence from above). Units SI at this boundary.
/// </summary>
public sealed class ShotScenario
{
    /// <summary>Scene latitude (deg).</summary>
    public double LatitudeDeg { get; set; }

    /// <summary>Scene longitude (deg).</summary>
    public double LongitudeDeg { get; set; }

    /// <summary>Launch elevation above the ellipsoid (m).</summary>
    public double HeightM { get; set; }

    /// <summary>Fired azimuth (deg clockwise from true north).</summary>
    public double AzimuthDeg { get; set; }

    /// <summary>Launch elevation (deg above the horizontal plane; negative = downhill).</summary>
    public double LaunchElevationDeg { get; set; }

    /// <summary>Muzzle velocity (m/s).</summary>
    public double MuzzleVelocityMps { get; set; }

    /// <summary>Bullet/ammunition profile.</summary>
    public required BulletProfile Bullet { get; set; }

    /// <summary>Atmosphere anchored at the scene elevation.</summary>
    public required Atmosphere Atmosphere { get; set; }

    /// <summary>Wind profile (layers above the scene elevation; calm by default).</summary>
    public WindProfile Wind { get; set; } = WindProfile.Calm;

    /// <summary>Include Earth-rotation Coriolis acceleration (on by default per PLAN.md §3.4).</summary>
    public bool IncludeCoriolis { get; set; } = true;

    /// <summary>Impact plane (m above the ellipsoid). Null = the launch elevation plane.</summary>
    public double? TargetHeightM { get; set; }

    /// <summary>Fixed RK4 step (s).</summary>
    public double Dt { get; set; } = 0.001;

    /// <summary>Hard flight-time limit (s).</summary>
    public double MaxTimeS { get; set; } = 60.0;
}
