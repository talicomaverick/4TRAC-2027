using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Flat;

/// <summary>
/// Flat-earth point-mass equations of motion in a local East-North-Up frame.
/// Declared envelope: horizontal range up to <see cref="FlatSolveOptions.EnvelopeM"/> (10 km).
///
///     a = (0, 0, -g) + a_drag(v - w) + a_coriolis
///     a_coriolis = -2 * Omega x v,  Omega_ENU = (0, Omega cos(lat), Omega sin(lat))
///
/// State y = [x, y, z, vx, vy, vz]: x east, y north, z up (metres, seconds). The
/// frame is anchored at the impact point; z = 0 is the impact plane. Wind is a
/// air-motion vector (east, north) in m/s: constant, or with an optional two-layer change aloft (FlatWindShear). Atmosphere heights are the
/// anchor height plus z.
/// </summary>
public sealed class FlatTrajectoryModel : IOdeSystem
{
    /// <summary>Standard gravity (m/s^2), g = 9.80665 as declared in the build brief.</summary>
    public const double Gravity = 9.80665;

    private readonly IDragModel _drag;
    private readonly Atmosphere _atmosphere;
    private readonly double _windEast;
    private readonly double _windNorth;
    private readonly double _sinLat;
    private readonly double _cosLat;
    private readonly bool _coriolis;
    private readonly double _shearE;
    private readonly double _shearN;
    private readonly double _shearH;

    public FlatTrajectoryModel(
        IDragModel drag,
        Atmosphere atmosphere,
        double latDeg,
        double windEastMps,
        double windNorthMps,
        bool coriolis,
        FlatWindShear? shear = null)
    {
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        _atmosphere = atmosphere ?? throw new ArgumentNullException(nameof(atmosphere));
        double lat = latDeg * UnitConversions.DegToRad;
        _sinLat = Math.Sin(lat);
        _cosLat = Math.Cos(lat);
        _windEast = windEastMps;
        _windNorth = windNorthMps;
        _coriolis = coriolis;
        if (shear is { IsActive: true })
        {
            _shearE = shear.DeltaEastMps;
            _shearN = shear.DeltaNorthMps;
            _shearH = shear.HeightM;
        }
    }

    public int Dimension => 6;

    public void Derivatives(double t, double[] y, double[] dydt)
    {
        double vx = y[3], vy = y[4], vz = y[5];
        double ax = 0.0, ay = 0.0, az = -Gravity;

        double wE = _windEast, wN = _windNorth;
        if (_shearH > 0.0)
        {
            double f = y[2] <= 0.0 ? 0.0 : (y[2] >= _shearH ? 1.0 : y[2] / _shearH);
            wE += f * _shearE;
            wN += f * _shearN;
        }

        double avx = vx - wE;
        double avy = vy - wN;
        double avz = vz;
        double s = Math.Sqrt(avx * avx + avy * avy + avz * avz);
        if (s > 1e-9)
        {
            double height = _atmosphere.AnchorHeightM + y[2];
            double mach = s / _atmosphere.SpeedOfSound(height);
            double k = -0.5 * _atmosphere.Density(height) * _drag.Beta(s, mach) * s;
            ax += k * avx;
            ay += k * avy;
            az += k * avz;
        }

        if (_coriolis)
        {
            double w = Wgs84.Omega;
            ax += -2.0 * (w * _cosLat * vz - w * _sinLat * vy);
            ay += -2.0 * (w * _sinLat * vx);
            az += -2.0 * (-w * _cosLat * vx);
        }

        dydt[0] = vx;
        dydt[1] = vy;
        dydt[2] = vz;
        dydt[3] = ax;
        dydt[4] = ay;
        dydt[5] = az;
    }
}
