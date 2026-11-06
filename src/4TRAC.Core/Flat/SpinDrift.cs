using FourTrac.Core.Geo;

namespace FourTrac.Core.Flat;

/// <summary>
/// Bullet and rifle data needed for spin drift. Lengths in inches, mass in
/// grains, twist as inches per turn (the published units of the Miller and
/// Litz formulas).
/// </summary>
public sealed record FlatSpin(
    double DiameterIn,
    double MassGr,
    double TwistIn,
    double LengthIn,
    bool RightHandTwist)
{
    public void Validate()
    {
        if (DiameterIn <= 0) throw new ArgumentOutOfRangeException(nameof(DiameterIn));
        if (MassGr <= 0) throw new ArgumentOutOfRangeException(nameof(MassGr));
        if (TwistIn <= 0) throw new ArgumentOutOfRangeException(nameof(TwistIn));
        if (LengthIn <= 0) throw new ArgumentOutOfRangeException(nameof(LengthIn));
    }
}

/// <summary>
/// Spin drift as a deterministic lateral displacement (Litz form):
///     SG  = Miller gyroscopic stability factor
///     D(t) = 1.25 (SG + 1.2) t^1.83   inches, t = time of flight (s)
/// positive to the RIGHT of the direction of travel for a right-hand twist.
/// It is applied as a post-integration lateral offset of the impact (and the
/// matching lateral velocity), not as an EOM force term: this is the published
/// engineering form, and it is declared as such in every report.
/// </summary>
public static class SpinDrift
{
    /// <summary>
    /// Miller stability factor: SG = 30 m / (t^2 d^3 l (1 + l^2)) * (v / 2800)^(1/3),
    /// with t = twist in calibers per turn, l = length in calibers, d in inches,
    /// m in grains, v in fps.
    /// </summary>
    public static double StabilityFactor(FlatSpin spin, double muzzleVelocityMps)
    {
        ArgumentNullException.ThrowIfNull(spin);
        spin.Validate();
        if (muzzleVelocityMps <= 0) throw new ArgumentOutOfRangeException(nameof(muzzleVelocityMps));

        double d = spin.DiameterIn;
        double t = spin.TwistIn / d;
        double l = spin.LengthIn / d;
        double vFps = muzzleVelocityMps * UnitConversions.MpsToFps;
        return 30.0 * spin.MassGr / (t * t * d * d * d * l * (1.0 + l * l)) * Math.Cbrt(vFps / 2800.0);
    }

    /// <summary>Signed lateral displacement (m) at time of flight <paramref name="tofS"/>; + = right.</summary>
    public static double DisplacementM(double stabilityFactor, double tofS, bool rightHandTwist)
    {
        double inches = 1.25 * (stabilityFactor + 1.2) * Math.Pow(Math.Max(tofS, 0.0), 1.83);
        return Sign(rightHandTwist) * inches * UnitConversions.InchToM;
    }

    /// <summary>Signed lateral velocity (m/s) = d/dt of <see cref="DisplacementM"/>; + = right.</summary>
    public static double LateralVelocityMps(double stabilityFactor, double tofS, bool rightHandTwist)
    {
        double inchesPerS = 1.25 * (stabilityFactor + 1.2) * 1.83 * Math.Pow(Math.Max(tofS, 0.0), 0.83);
        return Sign(rightHandTwist) * inchesPerS * UnitConversions.InchToM;
    }

    /// <summary>Unit vector to the right of travel for a horizontal travel azimuth (deg clockwise from north).</summary>
    public static Cartesian3 RightOf(double azimuthDeg)
    {
        double az = azimuthDeg * UnitConversions.DegToRad;
        return new Cartesian3(Math.Cos(az), -Math.Sin(az), 0.0);
    }

    private static double Sign(bool rightHandTwist) => rightHandTwist ? 1.0 : -1.0;
}
