namespace FourTrac.Core.Geo;

/// <summary>
/// WGS84 / UTM projection (transverse Mercator, scale factor k0 = 0.9996).
/// Used for metric reporting of contour extents and distances; display maps
/// use Web Mercator (M4), never this.
/// </summary>
public static class Utm
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;
    private const double K0 = 0.9996;
    private const double FalseEasting = 500000.0;
    private const double FalseNorthingS = 10000000.0;

    private static readonly double E2 = Wgs84.E2;
    private static readonly double Ep2 = E2 / (1.0 - E2);

    public readonly record struct Coordinate(double Easting, double Northing, int Zone, bool South);

    public static int ZoneOf(double lonDeg)
    {
        int zone = (int)Math.Floor((lonDeg + 180.0) / 6.0) + 1;
        if (zone < 1) zone = 1;
        if (zone > 60) zone = 60;
        return zone;
    }

    /// <summary>Geodetic to UTM.</summary>
    public static Coordinate LatLonToUtm(double latDeg, double lonDeg, double heightM = 0.0)
    {
        double lat = latDeg * Deg2Rad;
        int zone = ZoneOf(lonDeg);
        double lonCm = (zone * 6 - 183) * Deg2Rad; // central meridian
        double lon = lonDeg * Deg2Rad;

        double sinLat = Math.Sin(lat);
        double cosLat = Math.Cos(lat);
        double tanLat = Math.Tan(lat);

        double n = Wgs84.A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);
        double t = tanLat * tanLat;
        double c = Ep2 * cosLat * cosLat;
        double a = (lon - lonCm) * cosLat;

        double m = MeridionalArc(lat);

        double easting = K0 * n * (a + (1.0 - t + c) * a * a * a / 6.0
            + (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * Ep2) * a * a * a * a * a / 120.0)
            + FalseEasting;

        double northing = K0 * (m + n * tanLat * (a * a / 2.0
            + (5.0 - t + 9.0 * c + 4.0 * c * c) * a * a * a * a / 24.0
            + (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * Ep2) * a * a * a * a * a * a / 720.0));

        bool south = latDeg < 0.0;
        if (south)
            northing += FalseNorthingS;

        return new Coordinate(easting, northing, zone, south);
    }

    /// <summary>UTM to geodetic (Newton-refined on the meridional arc).</summary>
    public static void UtmToLatLon(Coordinate c, out double latDeg, out double lonDeg)
    {
        double x = c.Easting - FalseEasting;
        double yM = (c.Northing - (c.South ? FalseNorthingS : 0.0)) / K0; // remove the scale factor

        double mu = yM / (Wgs84.A * (1.0 - E2 / 4.0 - 3.0 * E2 * E2 / 64.0 - 5.0 * E2 * E2 * E2 / 256.0));
        double e1 = (1.0 - Math.Sqrt(1.0 - E2)) / (1.0 + Math.Sqrt(1.0 - E2));
        double phi1 = mu
            + (3.0 * e1 / 2.0 - 27.0 * e1 * e1 * e1 / 32.0) * Math.Sin(2.0 * mu)
            + (21.0 * e1 * e1 / 16.0 - 55.0 * e1 * e1 * e1 * e1 / 32.0) * Math.Sin(4.0 * mu)
            + (151.0 * e1 * e1 * e1 / 96.0) * Math.Sin(6.0 * mu);

        // Newton polish on the meridional arc: M(phi) = y, dM/dphi = N(1-e2)/(1-e2 sin^2 phi).
        double phi = phi1;
        for (int i = 0; i < 4; i++)
        {
            double sinPhi = Math.Sin(phi);
            double nphi = Wgs84.A / Math.Sqrt(1.0 - E2 * sinPhi * sinPhi);
            double dm = nphi * (1.0 - E2) / (1.0 - E2 * sinPhi * sinPhi);
            phi += (yM - MeridionalArc(phi)) / dm;
        }

        double sinLat = Math.Sin(phi);
        double cosLat = Math.Cos(phi);
        double tanLat = Math.Tan(phi);
        double nphiFinal = Wgs84.A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);
        double t = tanLat * tanLat;
        double c2 = Ep2 * cosLat * cosLat;
        double r = nphiFinal * (1.0 - E2) / (1.0 - E2 * sinLat * sinLat);

        double d = x / (nphiFinal * K0);
        latDeg = (phi - nphiFinal * tanLat / r * (d * d / 2.0
            - (5.0 + 3.0 * t + 10.0 * c2 - 4.0 * c2 * c2 - 9.0 * Ep2) * d * d * d * d / 24.0
            + (61.0 + 90.0 * t + 298.0 * c2 + 45.0 * t * t - 252.0 * Ep2 - 3.0 * c2 * c2)
                * d * d * d * d * d * d / 720.0)) * Rad2Deg;

        double lonCm = (c.Zone * 6 - 183) * Deg2Rad;
        double lon = lonCm + (d - (1.0 + 2.0 * t + c2) * d * d * d / 6.0
            + (5.0 - 2.0 * c2 + 28.0 * t - 3.0 * c2 * c2 + 8.0 * Ep2 + 24.0 * t * t)
                * d * d * d * d * d / 120.0) / cosLat;
        lonDeg = lon * Rad2Deg;
        if (lonDeg > 180.0) lonDeg -= 360.0;
        if (lonDeg < -180.0) lonDeg += 360.0;
    }

    /// <summary>Meridional arc from equator to latitude (rad input), IUGG series.</summary>
    private static double MeridionalArc(double latRad)
    {
        double e2 = E2;
        double e4 = e2 * e2;
        double e6 = e4 * e2;
        return Wgs84.A * (
            (1.0 - e2 / 4.0 - 3.0 * e4 / 64.0 - 5.0 * e6 / 256.0) * latRad
            - (3.0 * e2 / 8.0 + 3.0 * e4 / 32.0 + 45.0 * e6 / 1024.0) * Math.Sin(2.0 * latRad)
            + (15.0 * e4 / 256.0 + 45.0 * e6 / 1024.0) * Math.Sin(4.0 * latRad)
            - (35.0 * e6 / 3072.0) * Math.Sin(6.0 * latRad));
    }
}
