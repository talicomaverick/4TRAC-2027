namespace FourTrac.Core.Geo;

/// <summary>
/// Great-circle geodesics on the WGS84 ellipsoid (Vincenty inverse solution).
/// </summary>
public static class Geodesic
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    public readonly record struct Result(double DistanceM, double ForwardAzimuthDeg);

    /// <summary>
    /// Vincenty inverse: distance (m) and forward azimuth (deg clockwise from
    /// true north) between two geodetic points. Falls back to the haversine
    /// solution (spherical R = A) for near-antipodal pairs where Vincenty does
    /// not converge - documented accuracy then degrades to ~0.3 %.
    /// </summary>
    public static Result Inverse(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        double a = Wgs84.A;
        double f = Wgs84.F;
        double b = a * (1.0 - f);

        double phi1 = lat1Deg * Deg2Rad;
        double phi2 = lat2Deg * Deg2Rad;
        double l = (lon2Deg - lon1Deg) * Deg2Rad;

        double u1 = Math.Atan((1.0 - f) * Math.Tan(phi1));
        double u2 = Math.Atan((1.0 - f) * Math.Tan(phi2));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1);
        double sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);

        double lambda = l;
        double sinSigma = 0, cosSigma = 0, sigma = 0, cosSqAlpha = 0, cos2SigmaM = 0;
        bool converged = false;

        for (int i = 0; i < 200; i++)
        {
            double sinLambda = Math.Sin(lambda), cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(
                (cosU2 * sinLambda) * (cosU2 * sinLambda)
                + (cosU1 * sinU2 - sinU1 * cosU2 * cosLambda) * (cosU1 * sinU2 - sinU1 * cosU2 * cosLambda));
            if (sinSigma == 0)
            {
                converged = true;
                sigma = 0;
                break; // coincident points
            }

            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);
            double sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1.0 - sinAlpha * sinAlpha;
            cos2SigmaM = cosSqAlpha != 0.0 ? cosSigma - 2.0 * sinU1 * sinU2 / cosSqAlpha : 0.0;

            double c = f / 16.0 * cosSqAlpha * (4.0 + f * (4.0 - 3.0 * cosSqAlpha));
            double lambdaPrev = lambda;
            lambda = l + (1.0 - c) * f * sinAlpha * (
                sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1.0 + 2.0 * cos2SigmaM * cos2SigmaM)));

            if (Math.Abs(lambda - lambdaPrev) < 1e-14)
            {
                converged = true;
                break;
            }
        }

        if (!converged)
            return Haversine(lat1Deg, lon1Deg, lat2Deg, lon2Deg);

        double uSq = cosSqAlpha * (a * a - b * b) / (b * b);
        double a1 = 1.0 + uSq / 16384.0 * (4096.0 + uSq * (-768.0 + uSq * (320.0 - 175.0 * uSq)));
        double b1 = uSq / 1024.0 * (256.0 + uSq * (-128.0 + uSq * (74.0 - 47.0 * uSq)));
        double deltaSigma = b1 * sinSigma * (
            cos2SigmaM + b1 / 4.0 * (
                cosSigma * (-1.0 + 2.0 * cos2SigmaM * cos2SigmaM)
                - b1 / 6.0 * cos2SigmaM * (-3.0 + 4.0 * sinSigma * sinSigma)
                    * (-3.0 + 4.0 * cos2SigmaM * cos2SigmaM)));

        double distance = b * a1 * (sigma - deltaSigma);

        double sinLamF = Math.Sin(lambda), cosLamF = Math.Cos(lambda);
        double azimuth = Math.Atan2(
            cosU2 * sinLamF,
            cosU1 * sinU2 - sinU1 * cosU2 * cosLamF) * Rad2Deg;
        if (azimuth < 0) azimuth += 360.0;

        return new Result(distance, azimuth);
    }

    private static Result Haversine(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        double phi1 = lat1Deg * Deg2Rad, phi2 = lat2Deg * Deg2Rad;
        double dPhi = phi2 - phi1;
        double dLam = (lon2Deg - lon1Deg) * Deg2Rad;
        double s = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLam / 2) * Math.Sin(dLam / 2);
        double distance = 2.0 * Wgs84.A * Math.Atan2(Math.Sqrt(s), Math.Sqrt(1.0 - s));
        double azimuth = Math.Atan2(
            Math.Sin(dLam) * Math.Cos(phi2),
            Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLam)) * Rad2Deg;
        if (azimuth < 0) azimuth += 360.0;
        return new Result(distance, azimuth);
    }
}
