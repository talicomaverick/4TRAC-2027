namespace FourTrac.Core.Geo;

/// <summary>
/// Immutable 3-D Cartesian vector (meters, or m/s for velocities) used for
/// ECEF and ENU state vectors.
/// </summary>
public readonly record struct Cartesian3(double X, double Y, double Z)
{
    public static readonly Cartesian3 Zero = new(0, 0, 0);

    public static Cartesian3 operator +(Cartesian3 a, Cartesian3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Cartesian3 operator -(Cartesian3 a, Cartesian3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Cartesian3 operator -(Cartesian3 a) => new(-a.X, -a.Y, -a.Z);
    public static Cartesian3 operator *(Cartesian3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Cartesian3 operator *(double s, Cartesian3 a) => a * s;
    public static Cartesian3 operator /(Cartesian3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    public static double Dot(Cartesian3 a, Cartesian3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Cartesian3 Cross(Cartesian3 a, Cartesian3 b) =>
        new(a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

    public Cartesian3 Normalized()
    {
        double l = Length;
        return l > 0 ? this / l : Zero;
    }

    public override string ToString() => $"({X:F6}, {Y:F6}, {Z:F6})";
}
