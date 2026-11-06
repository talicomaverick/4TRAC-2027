using FourTrac.Core.Geo;

namespace FourTrac.Core.Dynamics;

/// <summary>Options for forward trajectory integration (production path).</summary>
public sealed class TrajectoryOptions
{
    /// <summary>Fixed RK4 step (s). 1 ms resolves small-arms trajectories to well under a millimetre of drop error.</summary>
    public double Dt { get; set; } = 0.001;

    /// <summary>Hard flight-time limit (s).</summary>
    public double MaxTime { get; set; } = 120.0;

    /// <summary>Target height above the ellipsoid (m): terrain or a user elevation plane.</summary>
    public double TargetHeightM { get; set; } = 0.0;

    /// <summary>Ground-crossing bisection tolerance (m of height).</summary>
    public double EventToleranceM { get; set; } = 1e-4;

    /// <summary>Stop if speed drops below this (m/s).</summary>
    public double MinSpeedMps { get; set; } = 1.0;

    /// <summary>Record per-step samples (memory). When false, only the final state is reported.</summary>
    public bool RecordSamples { get; set; } = true;

    /// <summary>Hard cap on recorded samples (safety).</summary>
    public int MaxSamples { get; set; } = 500_000;
}

/// <summary>
/// Fixed-step RK4 trajectory engine with ground-crossing event detection
/// (bisection-refined to <see cref="TrajectoryOptions.EventToleranceM"/>).
/// The fixed-step + event-refine design integrates cleanly through the impact
/// event; the adaptive RK45 lives alongside for reference solutions.
/// </summary>
public sealed class TrajectoryEngine
{
    public TrajectoryResult Integrate(
        TrajectoryModel model, Cartesian3 startPositionEcef, Cartesian3 startVelocityEcef,
        TrajectoryOptions? options = null)
    {
        var o = options ?? new TrajectoryOptions();
        if (o.Dt <= 0) throw new ArgumentOutOfRangeException(nameof(o), "Dt must be positive.");
        if (o.MaxTime <= 0) throw new ArgumentOutOfRangeException(nameof(o), "MaxTime must be positive.");

        int n = model.Dimension;
        var y = new double[n];
        var yprev = new double[n];
        var k1 = new double[n]; var k2 = new double[n]; var k3 = new double[n]; var k4 = new double[n];
        var ytmp = new double[n]; var yout = new double[n];

        y[0] = startPositionEcef.X; y[1] = startPositionEcef.Y; y[2] = startPositionEcef.Z;
        y[3] = startVelocityEcef.X; y[4] = startVelocityEcef.Y; y[5] = startVelocityEcef.Z;

        double t = 0.0;
        var samples = new List<TrajectorySample>(1024);

        // A launch exactly at the target height is legal (the trajectory ascends
        // first); only genuinely below-target starts are rejected.
        double h0 = Geodetic.Height(startPositionEcef) - o.TargetHeightM;
        if (h0 < -o.EventToleranceM)
        {
            Record(samples, o, t, y, model);
            return Finish(samples, TrajectoryEndReason.StartedBelowTarget, t, y, model);
        }

        while (true)
        {
            if (t >= o.MaxTime)
                return Finish(samples, TrajectoryEndReason.TimeLimit, t, y, model);
            if (samples.Count >= o.MaxSamples)
                return Finish(samples, TrajectoryEndReason.TimeLimit, t, y, model);

            double speed = Math.Sqrt(y[3] * y[3] + y[4] * y[4] + y[5] * y[5]);
            if (speed < o.MinSpeedMps)
                return Finish(samples, TrajectoryEndReason.MinSpeed, t, y, model);

            Record(samples, o, t, y, model);

            double h = Math.Min(o.Dt, o.MaxTime - t);
            Array.Copy(y, yprev, n);
            Rk4.Step(model, t, y, h, k1, k2, k3, k4, ytmp, yout);
            Array.Copy(yout, y, n);

            double heightNow = Geodetic.Height(new Cartesian3(y[0], y[1], y[2])) - o.TargetHeightM;
            if (heightNow <= 0.0)
            {
                // Bisection refine on [0, h] of the pre-step state.
                double lo = 0.0, hi = h;
                for (int i = 0; i < 40 && hi - lo > 1e-12; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    Rk4.Step(model, t, yprev, mid, k1, k2, k3, k4, ytmp, yout);
                    double hm = Geodetic.Height(new Cartesian3(yout[0], yout[1], yout[2])) - o.TargetHeightM;
                    if (hm <= 0.0) hi = mid;
                    else lo = mid;
                }
                Rk4.Step(model, t, yprev, lo, k1, k2, k3, k4, ytmp, yout);
                Array.Copy(yout, y, n);
                t += lo;
                Record(samples, o, t, y, model);
                return Finish(samples, TrajectoryEndReason.GroundHit, t, y, model);
            }

            t += h;
        }
    }

    private static void Record(List<TrajectorySample> samples, TrajectoryOptions o,
        double t, double[] y, TrajectoryModel model)
    {
        if (!o.RecordSamples) return;
        var r = new Cartesian3(y[0], y[1], y[2]);
        var v = new Cartesian3(y[3], y[4], y[5]);
        double speed = v.Length;
        double height = Geodetic.Height(r);
        double soundSpeed = model.Atmosphere.SpeedOfSound(height);
        samples.Add(new TrajectorySample(
            t, r, v, speed,
            soundSpeed > 0 ? speed / soundSpeed : 0.0,
            model.Atmosphere.Density(height),
            height));
    }

    private static TrajectoryResult Finish(List<TrajectorySample> samples,
        TrajectoryEndReason reason, double t, double[] y, TrajectoryModel model)
    {
        var r = new Cartesian3(y[0], y[1], y[2]);
        var v = new Cartesian3(y[3], y[4], y[5]);
        double speed = v.Length;
        double height = Geodetic.Height(r);
        double soundSpeed = model.Atmosphere.SpeedOfSound(height);
        return new TrajectoryResult(
            samples, reason, t, r, v, speed,
            soundSpeed > 0 ? speed / soundSpeed : 0.0,
            height);
    }
}
