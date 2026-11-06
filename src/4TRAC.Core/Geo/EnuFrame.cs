namespace FourTrac.Core.Geo;

/// <summary>
/// Local East-North-Up frame anchored at a geodetic origin. Used for scene
/// measurements (azimuth/elevation), wind vectors, and reporting.
/// </summary>
public sealed class EnuFrame
{
    private const double Deg2Rad = Math.PI / 180.0;

    public double OriginLatDeg { get; }
    public double OriginLonDeg { get; }
    public double OriginHeightM { get; }
    public Cartesian3 OriginEcef { get; }

    private readonly Cartesian3 _east;
    private readonly Cartesian3 _north;
    private readonly Cartesian3 _up;

    private EnuFrame(double latDeg, double lonDeg, double heightM)
    {
        OriginLatDeg = latDeg;
        OriginLonDeg = lonDeg;
        OriginHeightM = heightM;
        OriginEcef = Geodetic.LlaToEcef(latDeg, lonDeg, heightM);
        _up = Geodetic.GeodeticNormal(latDeg, lonDeg);
        double lat = latDeg * Deg2Rad;
        double lon = lonDeg * Deg2Rad;
        _east = new Cartesian3(-Math.Sin(lon), Math.Cos(lon), 0.0);
        _north = Cartesian3.Cross(_up, _east);
    }

    public static EnuFrame AtOrigin(double latDeg, double lonDeg, double heightM = 0.0) =>
        new(latDeg, lonDeg, heightM);

    public Cartesian3 East => _east;
    public Cartesian3 North => _north;
    public Cartesian3 Up => _up;

    /// <summary>Project an ECEF vector (or position) into this ENU frame.</summary>
    public Cartesian3 ToEnu(Cartesian3 ecef)
    {
        Cartesian3 d = ecef - OriginEcef;
        return new Cartesian3(
            Cartesian3.Dot(_east, d),
            Cartesian3.Dot(_north, d),
            Cartesian3.Dot(_up, d));
    }

    /// <summary>Project a free vector (velocity, wind) into this ENU frame - no origin shift.</summary>
    public Cartesian3 VectorToEnu(Cartesian3 ecefVector) => new(
        Cartesian3.Dot(_east, ecefVector),
        Cartesian3.Dot(_north, ecefVector),
        Cartesian3.Dot(_up, ecefVector));

    /// <summary>Convert an ENU vector (or position) to ECEF.</summary>
    public Cartesian3 ToEcef(Cartesian3 enu) =>
        OriginEcef + enu.X * _east + enu.Y * _north + enu.Z * _up;

    /// <summary>
    /// Convert a free ENU vector (velocity, acceleration) to ECEF - basis only,
    /// no origin shift. Do NOT use <see cref="ToEcef"/> for vectors; it adds the
    /// origin position.
    /// </summary>
    public Cartesian3 VectorToEcef(Cartesian3 enu) =>
        enu.X * _east + enu.Y * _north + enu.Z * _up;

    /// <summary>
    /// Ground-track azimuth (deg clockwise from true north, 0..360) of an ENU vector.
    /// </summary>
    public static double AzimuthDeg(Cartesian3 enu)
    {
        double az = Math.Atan2(enu.X, enu.Y) * Rad2Deg;
        return az < 0 ? az + 360.0 : az;
    }

    /// <summary>
    /// Elevation angle (deg, positive above the horizontal plane) of an ENU vector.
    /// </summary>
    public static double ElevationDeg(Cartesian3 enu)
    {
        double horiz = Math.Sqrt(enu.X * enu.X + enu.Y * enu.Y);
        return Math.Atan2(enu.Z, horiz) * Rad2Deg;
    }

    /// <summary>
    /// Build a unit ENU direction vector from azimuth (deg clockwise from true
    /// north) and elevation (deg above horizontal).
    /// </summary>
    public static Cartesian3 DirectionFromAzimuthElevation(double azimuthDeg, double elevationDeg)
    {
        double az = azimuthDeg * Deg2Rad;
        double el = elevationDeg * Deg2Rad;
        double cosEl = Math.Cos(el);
        return new Cartesian3(
            Math.Sin(az) * cosEl,
            Math.Cos(az) * cosEl,
            Math.Sin(el));
    }

    private const double Rad2Deg = 180.0 / Math.PI;
}
