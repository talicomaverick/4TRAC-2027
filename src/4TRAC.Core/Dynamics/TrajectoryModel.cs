using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Dynamics;

/// <summary>
/// Point-mass equations of motion in ECEF, implementing PLAN.md §3:
///     a = g_normal(lat, h) + a_drag(v_air) + a_Coriolis(v)
/// with g directed along the geodetic normal (WGS84 normal gravity with
/// free-air correction; centrifugal is included in normal gravity and must not
/// be added again), drag against the air-relative velocity, and explicit
/// Coriolis -2 x v. Wind is the air velocity in ECEF (precomputed by
/// <see cref="WindProfile"/> at the scene frame).
/// Spin drift (Litz) is added in M4 - see PLAN.md §8.
/// </summary>
public sealed class TrajectoryModel : IOdeSystem
{
    private const double Deg2Rad = Math.PI / 180.0;

    private readonly IDragModel _drag;
    private readonly Atmosphere _atmosphere;
    private readonly Cartesian3 _windEcef;
    private readonly WindProfile? _windProfile;
    private readonly EnuFrame? _windFrame;
    private readonly bool _includeDrag;
    private readonly bool _includeCoriolis;

    public TrajectoryModel(
        IDragModel drag,
        Atmosphere atmosphere,
        Cartesian3 windEcef,
        bool includeDrag = true,
        bool includeCoriolis = true)
    {
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        _atmosphere = atmosphere ?? throw new ArgumentNullException(nameof(atmosphere));
        _windEcef = windEcef;
        _windProfile = null;
        _windFrame = null;
        _includeDrag = includeDrag;
        _includeCoriolis = includeCoriolis;
    }

    /// <summary>
    /// Height-varying wind: the profile is evaluated at every derivative call at
    /// the current height above the atmosphere anchor (the scene elevation).
    /// Layer heights are therefore interpreted as metres above the scene.
    /// </summary>
    public TrajectoryModel(
        IDragModel drag,
        Atmosphere atmosphere,
        WindProfile wind,
        EnuFrame sceneFrame,
        bool includeDrag = true,
        bool includeCoriolis = true)
    {
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        _atmosphere = atmosphere ?? throw new ArgumentNullException(nameof(atmosphere));
        _windEcef = Cartesian3.Zero;
        _windProfile = wind ?? throw new ArgumentNullException(nameof(wind));
        _windFrame = sceneFrame ?? throw new ArgumentNullException(nameof(sceneFrame));
        _includeDrag = includeDrag;
        _includeCoriolis = includeCoriolis;
    }

    public int Dimension => 6;

    public Atmosphere Atmosphere => _atmosphere;
    public IDragModel Drag => _drag;
    public bool IncludeDrag => _includeDrag;
    public bool IncludeCoriolis => _includeCoriolis;

    /// <summary>
    /// State layout: y = [rx, ry, rz, vx, vy, vz] (ECEF, SI).
    /// </summary>
    public void Derivatives(double t, double[] y, double[] dydt)
    {
        var r = new Cartesian3(y[0], y[1], y[2]);
        var v = new Cartesian3(y[3], y[4], y[5]);

        Geodetic.EcefToLla(r, out double latDeg, out double lonDeg, out double heightM);

        // Gravity: normal gravity along the geodetic normal, pointing "down".
        double gamma = Wgs84.NormalGravity(latDeg, heightM);
        var normal = Geodetic.GeodeticNormal(latDeg, lonDeg);
        var a = normal * (-gamma);

        // Drag against air-relative velocity.
        if (_includeDrag)
        {
            var windEcef = _windProfile is null
                ? _windEcef
                : _windProfile.WindVectorEcef(heightM - _atmosphere.AnchorHeightM, _windFrame!);
            var vAir = v - windEcef;
            double s = vAir.Length;
            if (s > 1e-9)
            {
                double soundSpeed = _atmosphere.SpeedOfSound(heightM);
                double mach = s / soundSpeed;
                double rho = _atmosphere.Density(heightM);
                a += vAir * (-0.5 * rho * _drag.Beta(s, mach) * s);
            }
        }

        // Coriolis (ECEF rotates with the Earth): a = -2 x v.
        if (_includeCoriolis)
            a += new Cartesian3(2.0 * Wgs84.Omega * v.Y, -2.0 * Wgs84.Omega * v.X, 0.0);

        dydt[0] = v.X;
        dydt[1] = v.Y;
        dydt[2] = v.Z;
        dydt[3] = a.X;
        dydt[4] = a.Y;
        dydt[5] = a.Z;
    }
}
