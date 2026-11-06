namespace FourTrac.Core.Atm;

/// <summary>
/// Point-mass atmosphere: temperature, pressure, humidity and air density along
/// the vertical column above a reference (scene) elevation, using the ISA
/// lapse rate in the troposphere. Humidity is handled as a fixed vapour-pressure
/// fraction of total pressure along the column (documented approximation; the
/// effect on rho is &lt; 0.5 % for forensic altitude spans).
/// </summary>
public sealed class Atmosphere
{
    // ISA constants
    private const double LapseRate = 0.0065;          // K/m
    private const double Rd = 287.05287;              // J/(kg K) dry air
    private const double Rv = 461.495;                // J/(kg K) water vapour
    private const double G0 = 9.80665;                // m/s^2
    public const double SeaLevelDensity = 1.225;      // kg/m^3
    private const double SeaLevelTemp = 288.15;       // K
    private const double SeaLevelPressure = 101325.0; // Pa

    private readonly double _anchorHeightM;
    private readonly double _t0K;
    private readonly double _p0Pa;
    private readonly double _e0Pa; // vapour pressure at the anchor

    private Atmosphere(double anchorHeightM, double t0K, double p0Pa, double e0Pa)
    {
        _anchorHeightM = anchorHeightM;
        _t0K = t0K;
        _p0Pa = p0Pa;
        _e0Pa = e0Pa;
    }

    /// <summary>
    /// Atmosphere from measurements at the scene: temperature (deg C), station
    /// pressure (Pa) and relative humidity (0..1), at elevation
    /// <paramref name="anchorHeightM"/> above the ellipsoid.
    /// </summary>
    public static Atmosphere FromMeasurements(double temperatureC, double pressurePa,
        double relativeHumidity, double anchorHeightM)
    {
        if (pressurePa <= 0) throw new ArgumentOutOfRangeException(nameof(pressurePa));
        if (relativeHumidity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(relativeHumidity));
        double t0 = temperatureC + 273.15;
        if (t0 < 150.0 || t0 > 400.0) throw new ArgumentOutOfRangeException(nameof(temperatureC));
        double e0 = relativeHumidity * SaturationVapourPressure(t0);
        if (e0 >= pressurePa) e0 = 0.99 * pressurePa; // guard: physically impossible mix
        return new Atmosphere(anchorHeightM, t0, pressurePa, e0);
    }

    /// <summary>
    /// Standard atmosphere anchored at a given site elevation (ISA): T0 and P0
    /// from the sea-level ISA values with the tropospheric lapse rate; RH = 0.
    /// </summary>
    public static Atmosphere IsaAtElevation(double elevationM)
    {
        double t0 = SeaLevelTemp - LapseRate * elevationM;
        double p0 = SeaLevelPressure * Math.Pow(t0 / SeaLevelTemp, G0 / (Rd * LapseRate));
        return new Atmosphere(elevationM, t0, p0, 0.0);
    }

    public double AnchorHeightM => _anchorHeightM;
    public double AnchorTemperatureK => _t0K;
    public double AnchorPressurePa => _p0Pa;

    /// <summary>Temperature (K) at ellipsoidal height h (linear ISA lapse from the anchor).</summary>
    public double Temperature(double heightM) => _t0K - LapseRate * (heightM - _anchorHeightM);

    /// <summary>Total pressure (Pa) at ellipsoidal height h.</summary>
    public double Pressure(double heightM)
    {
        double t = Temperature(heightM);
        return _p0Pa * Math.Pow(t / _t0K, G0 / (Rd * LapseRate));
    }

    /// <summary>Water vapour pressure (Pa) at ellipsoidal height h.</summary>
    public double VapourPressure(double heightM) => _e0Pa * Pressure(heightM) / _p0Pa;

    /// <summary>Air density (kg/m^3) at ellipsoidal height h, humid air included.</summary>
    public double Density(double heightM)
    {
        double t = Temperature(heightM);
        double p = Pressure(heightM);
        double e = VapourPressure(heightM);
        return (p - e) / (Rd * t) + e / (Rv * t);
    }

    /// <summary>Speed of sound (m/s) at ellipsoidal height h (dry-air composition, gamma = 1.4).</summary>
    public double SpeedOfSound(double heightM) => Math.Sqrt(1.4 * Rd * Temperature(heightM));

    /// <summary>
    /// Density altitude (m): the ISA sea-level-referenced altitude having the
    /// given density. Monotonically increasing with height for our input ranges.
    /// </summary>
    public static double DensityAltitude(double densityKgM3)
    {
        double exponent = Rd * LapseRate / (G0 - Rd * LapseRate);
        return SeaLevelTemp / LapseRate
            * (1.0 - Math.Pow(densityKgM3 / SeaLevelDensity, exponent));
    }

    /// <summary>
    /// Saturation vapour pressure (Pa) over water at temperature T (K),
    /// August-Roche-Magnus (valid approx. -45..60 deg C).
    /// </summary>
    public static double SaturationVapourPressure(double temperatureK)
    {
        double tc = temperatureK - 273.15;
        return 610.94 * Math.Exp(17.625 * tc / (tc + 243.04));
    }
}
