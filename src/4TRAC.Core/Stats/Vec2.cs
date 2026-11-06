namespace FourTrac.Core.Stats;

/// <summary>
/// A 2D point/offset in metres (scene ENU east-north, or grid world
/// coordinates). Kept separate from <c>Cartesian3</c> so the statistics layer
/// never implies a third axis.
/// </summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0.0, 0.0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => a * s;

    public double LengthSquared => X * X + Y * Y;
    public double Length => Math.Sqrt(LengthSquared);
}
