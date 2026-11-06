namespace FourTrac.Core.Geo;

/// <summary>
/// Conversions between geodetic (lat, lon, ellipsoidal height) and ECEF
/// coordinates on the WGS84 ellipsoid.
/// </summary>
public static class Geodetic
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>Geodetic latitude (deg), longitude (deg), height above ellipsoid (m) to ECEF (m).</summary>
    public static Cartesian3 LlaToEcef(double latDeg, double lonDeg, double heightM)
    {
        double lat = latDeg * Deg2Rad;
        double lon = lonDeg * Deg2Rad;
        double sinLat = Math.Sin(lat);
        double cosLat = Math.Cos(lat);
        double n = Wgs84.A / Math.Sqrt(1.0 - Wgs84.E2 * sinLat * sinLat);
        double nh = n + heightM;
        return new Cartesian3(
            nh * cosLat * Math.Cos(lon),
            nh * cosLat * Math.Sin(lon),
            (n * (1.0 - Wgs84.E2) + heightM) * sinLat);
    }

    /// <summary>
    /// ECEF (m) to geodetic latitude (deg), longitude (deg), ellipsoidal height (m).
    /// Fixed-point iteration on the latitude (4 passes), which converges to well
    /// below 1e-12 rad for every non-singular point on and above the ellipsoid.
    /// </summary>
    public static void EcefToLla(Cartesian3 r, out double latDeg, out double lonDeg, out double heightM)
    {
        double p = Math.Sqrt(r.X * r.X + r.Y * r.Y);
        double lon = Math.Atan2(r.Y, r.X);

        if (p < 1e-9)
        {
            // On the spin axis: latitude is +/-90 deg, longitude undefined.
            latDeg = r.Z >= 0 ? 90.0 : -90.0;
            lonDeg = 0.0;
            heightM = Math.Abs(r.Z) - Wgs84.B;
            return;
        }

        double lat = Math.Atan2(r.Z, p * (1.0 - Wgs84.E2));
        double n = Wgs84.A;
        for (int i = 0; i < 4; i++)
        {
            double sinLat = Math.Sin(lat);
            n = Wgs84.A / Math.Sqrt(1.0 - Wgs84.E2 * sinLat * sinLat);
            lat = Math.Atan2(r.Z + Wgs84.E2 * n * sinLat, p);
        }

        double sinFinal = Math.Sin(lat);
        double cosFinal = Math.Cos(lat);
        double h;
        if (Math.Abs(cosFinal) > 1e-7)
            h = p / cosFinal - n;
        else
            h = Math.Abs(r.Z) / Math.Abs(sinFinal) - n * (1.0 - Wgs84.E2);

        latDeg = lat * Rad2Deg;
        lonDeg = lon * Rad2Deg;
        heightM = h;
    }

    /// <summary>Height above the ellipsoid (m) of an ECEF position.</summary>
    public static double Height(Cartesian3 r)
    {
        EcefToLla(r, out _, out _, out double h);
        return h;
    }

    /// <summary>Geodetic latitude (deg) of an ECEF position.</summary>
    public static double Latitude(Cartesian3 r)
    {
        EcefToLla(r, out double lat, out _, out _);
        return lat;
    }

    /// <summary>Longitude (deg) of an ECEF position.</summary>
    public static double Longitude(Cartesian3 r)
    {
        EcefToLla(r, out _, out double lon, out _);
        return lon;
    }

    /// <summary>
    /// Geodetic normal unit vector at (latDeg, lonDeg), pointing outward.
    /// This is the "up" direction used by the normal-gravity model.
    /// </summary>
    public static Cartesian3 GeodeticNormal(double latDeg, double lonDeg)
    {
        double lat = latDeg * Deg2Rad;
        double lon = lonDeg * Deg2Rad;
        double sinLat = Math.Sin(lat);
        double cosLat = Math.Cos(lat);
        return new Cartesian3(
            cosLat * Math.Cos(lon),
            cosLat * Math.Sin(lon),
            sinLat);
    }
}
