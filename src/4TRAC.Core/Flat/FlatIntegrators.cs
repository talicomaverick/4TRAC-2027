using FourTrac.Core.Dynamics;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Flat;

/// <summary>Result of a forward flat-earth integration.</summary>
public sealed record FlatForwardResult(
    bool Hit,
    Cartesian3 Position,
    Cartesian3 Velocity,
    double TimeOfFlightS);

/// <summary>Why the backward integration stopped.</summary>
public enum FlatBackwardEnd
{
    /// <summary>Crossed the origin plane while ascending in forward time: the launch point.</summary>
    ReachedOriginPlane,

    /// <summary>No valid crossing within the backward time limit (e.g. a descending launch).</summary>
    TimeLimit,

    /// <summary>Speed fell below the minimum before a valid crossing.</summary>
    MinSpeed,

    /// <summary>The track left the DEM coverage (no data): rejected, not extrapolated.</summary>
    OutsideTerrain,

    /// <summary>The integration produced a non-finite state (numerical blow-up): rejected.</summary>
    NonFinite,
}

/// <summary>Result of a backward flat-earth integration.</summary>
public sealed record FlatBackwardResult(
    FlatBackwardEnd EndReason,
    double TimeOfFlightS,
    Cartesian3 Position,
    Cartesian3 Velocity);

/// <summary>
/// Fixed-step RK4 integration in the flat model, forward and time-reversed.
/// </summary>
public static class FlatIntegrators
{
    /// <summary>
    /// Forward flight from <paramref name="start"/> until the first DESCENDING
    /// crossing of the impact plane z = 0 (a roof hit on the way down). The
    /// crossing is refined by bisection on the step fraction.
    /// </summary>
    public static FlatForwardResult Forward(
        FlatTrajectoryModel model,
        Cartesian3 start,
        Cartesian3 startVelocity,
        double dt = 0.001,
        double maxTimeS = 180.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (maxTimeS <= 0) throw new ArgumentOutOfRangeException(nameof(maxTimeS));

        int n = model.Dimension;
        var y = new[] { start.X, start.Y, start.Z, startVelocity.X, startVelocity.Y, startVelocity.Z };
        var yprev = new double[n];
        var k1 = new double[n]; var k2 = new double[n]; var k3 = new double[n]; var k4 = new double[n];
        var ytmp = new double[n]; var yout = new double[n];

        double t = 0.0;
        while (t < maxTimeS)
        {
            double speed = Math.Sqrt(y[3] * y[3] + y[4] * y[4] + y[5] * y[5]);
            if (speed < 1.0)
                break;

            Array.Copy(y, yprev, n);
            double h = Math.Min(dt, maxTimeS - t);
            Rk4.Step(model, t, y, h, k1, k2, k3, k4, ytmp, yout);

            if (yprev[2] > 0.0 && yout[2] <= 0.0)
            {
                double lo = 0.0, hi = h;
                for (int i = 0; i < 60 && hi - lo > 1e-12; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    Rk4.Step(model, t, yprev, mid, k1, k2, k3, k4, ytmp, yout);
                    if (yout[2] > 0.0) lo = mid;
                    else hi = mid;
                }
                Rk4.Step(model, t, yprev, hi, k1, k2, k3, k4, ytmp, yout);
                t += hi;
                return new FlatForwardResult(
                    true,
                    new Cartesian3(yout[0], yout[1], yout[2]),
                    new Cartesian3(yout[3], yout[4], yout[5]),
                    t);
            }

            Array.Copy(yout, y, n);
            t += h;
        }

        return new FlatForwardResult(
            false,
            new Cartesian3(y[0], y[1], y[2]),
            new Cartesian3(y[3], y[4], y[5]),
            t);
    }

    /// <summary>
    /// Time-reversed integration from the impact state. Every term of the
    /// equations of motion is a function of state only, so a negative step
    /// re-traces the forward states exactly. The origin is the first crossing of
    /// <paramref name="originHeightM"/> at which the forward vertical velocity
    /// is POSITIVE (a lofted launch). With <c>descendingLaunch</c> the origin is instead the
    /// first crossing at which the forward vertical velocity is NEGATIVE (a shooter on a surface
    /// above the impact firing down); ascending crossings are then skipped.
    /// </summary>
    public static FlatBackwardResult Backward(
        FlatTrajectoryModel model,
        Cartesian3 impactPosition,
        Cartesian3 impactVelocity,
        double originHeightM,
        double dt = 0.001,
        double maxTimeS = 180.0,
        double minSpeedMps = 1.0,
        IFlatSurface? surface = null,
        bool descendingLaunch = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (maxTimeS <= 0) throw new ArgumentOutOfRangeException(nameof(maxTimeS));

        var terrain = surface ?? new PlaneSurface(originHeightM);
        int n = model.Dimension;
        var y = new[] { impactPosition.X, impactPosition.Y, impactPosition.Z, impactVelocity.X, impactVelocity.Y, impactVelocity.Z };
        var yprev = new double[n];
        var k1 = new double[n]; var k2 = new double[n]; var k3 = new double[n]; var k4 = new double[n];
        var ytmp = new double[n]; var yout = new double[n];

        double elapsed = 0.0;
        double hPrev = Clearance(terrain, impactPosition.X, impactPosition.Y, impactPosition.Z);
        if (double.IsNaN(hPrev))
            return Finish(FlatBackwardEnd.OutsideTerrain, 0.0, y);

        while (true)
        {
            if (elapsed >= maxTimeS)
                return Finish(FlatBackwardEnd.TimeLimit, elapsed, y);

            double speed = Math.Sqrt(y[3] * y[3] + y[4] * y[4] + y[5] * y[5]);
            if (speed < minSpeedMps)
                return Finish(FlatBackwardEnd.MinSpeed, elapsed, y);

            Array.Copy(y, yprev, n);
            double hStep = -Math.Min(dt, maxTimeS - elapsed);
            Rk4.Step(model, -elapsed, y, hStep, k1, k2, k3, k4, ytmp, yout);

            double hNow = Clearance(terrain, yout[0], yout[1], yout[2]);
            if (double.IsNaN(hNow))
            {
                // NaN clearance is either a state that blew up, or a surface with no data here.
                bool finiteState = double.IsFinite(yout[0]) && double.IsFinite(yout[1]) && double.IsFinite(yout[2]);
                return Finish(finiteState ? FlatBackwardEnd.OutsideTerrain : FlatBackwardEnd.NonFinite, elapsed, y);
            }
            if (hPrev * hNow < 0.0 || hNow == 0.0)
            {
                double lo = 0.0, hi = hStep;
                for (int i = 0; i < 60 && Math.Abs(hi - lo) > 1e-12; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    Rk4.Step(model, -elapsed, yprev, mid, k1, k2, k3, k4, ytmp, yout);
                    double hm = Clearance(terrain, yout[0], yout[1], yout[2]);
                    if (hm * hPrev <= 0.0) hi = mid;
                    else lo = mid;
                }
                Rk4.Step(model, -elapsed, yprev, lo, k1, k2, k3, k4, ytmp, yout);

                // Ascending relative to the ground: vertical speed minus the surface's uphill rise along the track.
                // Climbing branch (default): the origin is a crossing that is ascending in forward time.
                // Descending branch: the origin is a crossing that is descending in forward time, i.e. a
                // shooter on a surface above the impact (roof, hillside) firing down.
                if (AscendingRelativeToGround(terrain, yout) != descendingLaunch)
                {
                    Array.Copy(yout, y, n);
                    elapsed += Math.Abs(lo);
                    return Finish(FlatBackwardEnd.ReachedOriginPlane, elapsed, y);
                }

                // Descending crossing: not an origin. The state is the bisected point (yout at
                // step `lo`), so the clock advances by `lo`, not by the whole step (it used to
                // advance by the full step: a time-of-flight overcount of up to dt).
                Array.Copy(yout, y, n);
                elapsed += Math.Abs(lo);
                hPrev = hNow;
                continue;
            }

            Array.Copy(yout, y, n);
            elapsed += Math.Abs(hStep);
            hPrev = hNow;
        }
    }

    private static double Clearance(IFlatSurface s, double x, double y, double z) => z - s.Height(x, y);

    private static bool AscendingRelativeToGround(IFlatSurface s, double[] st)
    {
        const double d = 1.0;
        double sx = (s.Height(st[0] + d, st[1]) - s.Height(st[0] - d, st[1])) / (2 * d);
        double sy = (s.Height(st[0], st[1] + d) - s.Height(st[0], st[1] - d)) / (2 * d);
        if (double.IsNaN(sx) || double.IsNaN(sy)) return false;
        return st[5] - (sx * st[3] + sy * st[4]) > 0.0;
    }

    private static FlatBackwardResult Finish(FlatBackwardEnd reason, double t, double[] y) =>
        new(reason, t, new Cartesian3(y[0], y[1], y[2]), new Cartesian3(y[3], y[4], y[5]));
}
