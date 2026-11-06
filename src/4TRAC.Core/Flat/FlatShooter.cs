using FourTrac.Core.Geo;

namespace FourTrac.Core.Flat;

/// <summary>
/// Shooting problem for truth generation: the launch (elevation, azimuth) that
/// carries a shot from a given origin onto the impact point, for a given muzzle
/// velocity. Newton iteration on the two launch angles with a finite-difference
/// Jacobian. Used only to generate synthetic truth; the solver itself goes the
/// other way (impact to origin).
/// </summary>
public static class FlatShooter
{
    /// <param name="model">Flat model (drag, atmosphere, wind, Coriolis).</param>
    /// <param name="muzzleVelocityMps">Muzzle speed of the shot.</param>
    /// <param name="ox">Origin east of the impact (m).</param>
    /// <param name="oy">Origin north of the impact (m).</param>
    /// <param name="originHeightM">Origin height relative to the impact plane (m); the origin sits on the surface.</param>
    /// <returns>Impact velocity, or null if the shot cannot be aimed.</returns>
    public static Cartesian3? ImpactVelocity(
        FlatTrajectoryModel model, double muzzleVelocityMps, double ox, double oy, double originHeightM)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!(muzzleVelocityMps > 0)) throw new ArgumentOutOfRangeException(nameof(muzzleVelocityMps));

        var start = new Cartesian3(0.0, 0.0, originHeightM);
        // Forward impact must land at (-ox, -oy): the origin sits at (ox, oy) relative to the impact.
        double tx = -ox, ty = -oy;
        double mv = muzzleVelocityMps;

        bool Miss(double e, double a, out double dx, out double dy, out Cartesian3 vel)
        {
            var f = FlatIntegrators.Forward(model, start, EnuFrame.DirectionFromAzimuthElevation(a, e) * mv);
            dx = f.Position.X - tx;
            dy = f.Position.Y - ty;
            vel = f.Velocity;
            return f.Hit;
        }

        double az0 = Math.Atan2(-ox, -oy) * UnitConversions.RadToDeg;
        const double step = 1e-5;

        // Several starting elevations: drag makes the vacuum estimate poor at long range.
        foreach (double elev0 in new[] { 1.0, 3.0, 6.0, 10.0, 16.0, 24.0, 35.0, 50.0 })
        {
            double elev = elev0, az = az0;
            for (int it = 0; it < 30; it++)
            {
                if (!Miss(elev, az, out double rx, out double ry, out _)) break;
                if (Math.Sqrt(rx * rx + ry * ry) < 1e-3)
                {
                    Miss(elev, az, out _, out _, out var v);
                    return v;
                }
                Miss(elev + step, az, out double ex1, out double ey1, out _);
                Miss(elev, az + step, out double ex2, out double ey2, out _);
                double j11 = (ex1 - rx) / step, j12 = (ex2 - rx) / step;
                double j21 = (ey1 - ry) / step, j22 = (ey2 - ry) / step;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 1e-12) break;
                double de = (j22 * rx - j12 * ry) / det;
                double da = (-j21 * rx + j11 * ry) / det;
                elev -= de;
                az -= da;
                if (elev <= 0.0 || elev >= 89.0) break;
            }
        }
        return null;
    }
}
