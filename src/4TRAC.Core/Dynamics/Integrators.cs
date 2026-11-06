namespace FourTrac.Core.Dynamics;

/// <summary>First-order ODE system y' = f(t, y) for the state vector y.</summary>
public interface IOdeSystem
{
    int Dimension { get; }
    void Derivatives(double t, double[] y, double[] dydt);
}

/// <summary>
/// Classic fixed-step 4th-order Runge-Kutta. Used for production trajectory
/// integration (with event bisection) and as the order-reference in V0 tests.
/// </summary>
public static class Rk4
{
    public static void Step(IOdeSystem sys, double t, double[] y, double h,
        double[] k1, double[] k2, double[] k3, double[] k4, double[] ytmp, double[] yout)
    {
        int n = sys.Dimension;

        sys.Derivatives(t, y, k1);
        for (int i = 0; i < n; i++) ytmp[i] = y[i] + 0.5 * h * k1[i];

        sys.Derivatives(t + 0.5 * h, ytmp, k2);
        for (int i = 0; i < n; i++) ytmp[i] = y[i] + 0.5 * h * k2[i];

        sys.Derivatives(t + 0.5 * h, ytmp, k3);
        for (int i = 0; i < n; i++) ytmp[i] = y[i] + h * k3[i];

        sys.Derivatives(t + h, ytmp, k4);
        for (int i = 0; i < n; i++)
            yout[i] = y[i] + h / 6.0 * (k1[i] + 2.0 * k2[i] + 2.0 * k3[i] + k4[i]);
    }
}

/// <summary>
/// Adaptive Dormand-Prince RK45 with PI step control. Used for validation and
/// reference solutions (V0/V1); the fixed-step RK4 path is used in production
/// because it integrates smoothly through the ground-crossing event.
/// </summary>
public sealed class Rk45
{
    // Dormand-Prince coefficients.
    private static readonly double[] C = { 0, 1.0 / 5.0, 3.0 / 10.0, 4.0 / 5.0, 8.0 / 9.0, 1.0, 1.0 };
    private static readonly double[,] A =
    {
        { 0, 0, 0, 0, 0, 0 },
        { 1.0 / 5.0, 0, 0, 0, 0, 0 },
        { 3.0 / 40.0, 9.0 / 40.0, 0, 0, 0, 0 },
        { 44.0 / 45.0, -56.0 / 15.0, 32.0 / 9.0, 0, 0, 0 },
        { 19372.0 / 6561.0, -25360.0 / 2187.0, 64448.0 / 6561.0, -212.0 / 729.0, 0, 0 },
        { 9017.0 / 3168.0, -355.0 / 33.0, 46732.0 / 5247.0, 49.0 / 176.0, -5103.0 / 18656.0, 0 },
        { 35.0 / 384.0, 0, 500.0 / 1113.0, 125.0 / 192.0, -2187.0 / 6784.0, 11.0 / 84.0 },
    };
    private static readonly double[] B5 = { 35.0 / 384.0, 0, 500.0 / 1113.0, 125.0 / 192.0, -2187.0 / 6784.0, 11.0 / 84.0, 0 };
    private static readonly double[] B4 = { 5179.0 / 57600.0, 0, 7571.0 / 16695.0, 393.0 / 640.0, -92097.0 / 339200.0, 187.0 / 2100.0, 1.0 / 40.0 };

    private const double Safety = 0.9;
    private const double MinFactor = 0.2;
    private const double MaxFactor = 5.0;

    public double RelTol { get; }
    public double AbsTol { get; }

    public Rk45(double relTol = 1e-10, double absTol = 1e-12)
    {
        RelTol = relTol;
        AbsTol = absTol;
    }

    /// <summary>
    /// Integrate one adaptive step. Advances <paramref name="y"/> by the step
    /// actually taken (after any internal rejections) and returns both that
    /// taken step and the suggested next step size.
    /// </summary>
    public (double HTaken, double HNext) Step(IOdeSystem sys, double t, double[] y, double hRequest, double hMax)
    {
        int n = sys.Dimension;
        var k = new double[7][];
        for (int i = 0; i < 7; i++) k[i] = new double[n];
        var ytmp = new double[n];
        var y5 = new double[n];
        var y4 = new double[n];

        double h = Math.Min(hRequest, hMax);
        for (int attempt = 0; attempt < 60; attempt++)
        {

            sys.Derivatives(t, y, k[0]);
            for (int s = 1; s < 7; s++)
            {
                for (int i = 0; i < n; i++)
                {
                    double sum = 0.0;
                    for (int j = 0; j < s; j++) sum += A[s, j] * k[j][i];
                    ytmp[i] = y[i] + h * sum;
                }
                sys.Derivatives(t + C[s] * h, ytmp, k[s]);
            }

            for (int i = 0; i < n; i++)
            {
                double sum5 = 0.0, sum4 = 0.0;
                for (int j = 0; j < 7; j++)
                {
                    sum5 += B5[j] * k[j][i];
                    sum4 += B4[j] * k[j][i];
                }
                y5[i] = y[i] + h * sum5;
                y4[i] = y[i] + h * sum4;
            }

            // Scaled RMS error norm.
            double err = 0.0;
            for (int i = 0; i < n; i++)
            {
                double sc = AbsTol + RelTol * Math.Max(Math.Abs(y[i]), Math.Abs(y5[i]));
                double e = (y5[i] - y4[i]) / sc;
                err += e * e;
            }
            err = Math.Sqrt(err / n);

            if (err <= 1.0)
            {
                Array.Copy(y5, y, n);
                double factor = err < 1e-10 ? MaxFactor : Safety * Math.Pow(err, -0.2);
                factor = Math.Clamp(factor, MinFactor, MaxFactor);
                return (h, Math.Min(h * factor, hMax));
            }

            double shrink = Safety * Math.Pow(err, -0.25);
            h *= Math.Clamp(shrink, MinFactor, 0.9);
            if (h < 1e-12)
                throw new InvalidOperationException("RK45 step size collapsed below 1 ns.");
        }

        throw new InvalidOperationException("RK45 failed to converge within 60 attempts.");
    }
}
