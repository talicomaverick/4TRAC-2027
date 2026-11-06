namespace FourTrac.Core.Geo;

/// <summary>
/// WGS84 reference-ellipsoid constants and normal-gravity model (IUGG / Somigliana).
/// </summary>
/// <remarks>
/// Gravity model choice (documented in PLAN.md §3.1):
/// the trajectory uses WGS84 *normal* gravity (effective gravity: attraction +
/// centrifugal, per Somigliana) directed along the geodetic normal, plus an
/// explicit Coriolis term. The centrifugal acceleration must therefore NEVER be
/// added separately - doing so would double-count it. The difference between the
/// plumb line and the ellipsoid normal (vertical deflection, ~10..50 arcsec) is a
/// documented limitation for M1; see PLAN.md §10.
/// </remarks>
public static class Wgs84
{
    /// <summary>Semi-major axis (m).</summary>
    public const double A = 6378137.0;

    /// <summary>Flattening, 1/f = 298.257223563.</summary>
    public const double F = 1.0 / 298.257223563;

    /// <summary>Semi-minor axis (m).</summary>
    public const double B = A * (1.0 - F);

    /// <summary>First eccentricity squared, e^2 = f(2 - f).</summary>
    public const double E2 = F * (2.0 - F);

    /// <summary>Earth sidereal rotation rate (rad/s).</summary>
    public const double Omega = 7.292115e-5;

    /// <summary>Earth gravitational constant GM (m^3/s^2), IERS conventions.</summary>
    public const double Gm = 3.986004418e14;

    /// <summary>Normal gravity at the equator on the ellipsoid (m/s^2), IUGG.</summary>
    public const double GammaE = 9.7803253359;

    /// <summary>Somigliana's dynamic-form constant k, IUGG.</summary>
    public const double KSomigliana = 0.00193185265241;

    /// <summary>
    /// Somigliana's m parameter, m = omega^2 * a^2 * b / GM (computed from the
    /// constants above rather than hard-coded, so the value stays self-consistent).
    /// </summary>
    public const double MSomigliana = Omega * Omega * A * A * B / Gm;

    private const double Deg2Rad = Math.PI / 180.0;

    /// <summary>
    /// WGS84 normal gravity (m/s^2) at geodetic latitude <paramref name="latDeg"/>
    /// and height <paramref name="heightM"/> above the ellipsoid, with the standard
    /// free-air correction. Valid for small arms trajectory altitudes.
    /// </summary>
    public static double NormalGravity(double latDeg, double heightM)
    {
        double sinLat = Math.Sin(latDeg * Deg2Rad);
        double s2 = sinLat * sinLat;
        double gamma0 = GammaE * (1.0 + KSomigliana * s2) / Math.Sqrt(1.0 - E2 * s2);
        // Free-air gradient with latitude dependence (IUGG standard coefficients).
        double k = (2.0 / A) * (1.0 + F + MSomigliana - 2.0 * F * s2);
        return gamma0 * (1.0 - k * heightM);
    }
}
