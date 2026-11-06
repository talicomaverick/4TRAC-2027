namespace FourTrac.Core.Geo;

/// <summary>
/// Unit conversions used at the UI/BC boundary. Internal dynamics are strictly SI.
/// </summary>
public static class UnitConversions
{
    /// <summary>
    /// lb/in^2 to kg/m^2 (exact: 1 lb = 0.45359237 kg, 1 in = 0.0254 m).
    /// This is the standard conversion for G-model ballistic coefficients.
    /// </summary>
    public const double LbIn2ToKgM2 = 0.45359237 / (0.0254 * 0.0254); // = 703.069578296...

    public const double FpsToMps = 0.3048;
    public const double MpsToFps = 1.0 / 0.3048;
    public const double MphToMps = 0.44704;
    public const double DegToRad = Math.PI / 180.0;
    public const double RadToDeg = 180.0 / Math.PI;
    public const double InchToM = 0.0254;
    public const double GrToKg = 0.00006479891;
    public const double LbToKg = 0.45359237;
}
