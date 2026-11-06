using FourTrac.Core.Dynamics;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// Exponential decay y' = -k y with exact solution e^(-k t).
/// </summary>
internal sealed class DecaySystem : IOdeSystem
{
    private readonly double _k;
    public DecaySystem(double k) => _k = k;
    public int Dimension => 1;
    public void Derivatives(double t, double[] y, double[] dydt) => dydt[0] = -_k * y[0];
}

/// <summary>
/// Harmonic oscillator y'' = -w^2 y as a first-order system [y, v].
/// </summary>
internal sealed class OscillatorSystem : IOdeSystem
{
    private readonly double _w;
    public OscillatorSystem(double omega) => _w = omega;
    public int Dimension => 2;
    public void Derivatives(double t, double[] y, double[] dydt)
    {
        dydt[0] = y[1];
        dydt[1] = -_w * _w * y[0];
    }
}

/// <summary>
/// V0 code verification for the integrators (PLAN.md §7): observed order of
/// convergence on analytically solvable ODEs, adaptive-tolerance accuracy.
/// </summary>
public class IntegratorTests
{
    private static double IntegrateRk4(IOdeSystem sys, double[] y0, double tEnd, double h)
    {
        int n = sys.Dimension;
        var y = (double[])y0.Clone();
        var k1 = new double[n]; var k2 = new double[n]; var k3 = new double[n]; var k4 = new double[n];
        var ytmp = new double[n]; var yout = new double[n];
        double t = 0.0;
        while (t < tEnd - 1e-15)
        {
            double step = Math.Min(h, tEnd - t);
            Rk4.Step(sys, t, y, step, k1, k2, k3, k4, ytmp, yout);
            (yout, y) = (y, yout);
            t += step;
        }
        return y[0];
    }

    [Fact]
    public void Rk4_ExponentialDecay_FourthOrderConvergence()
    {
        const double k = 3.0;
        const double tEnd = 1.0;
        var sys = new DecaySystem(k);
        double exact = Math.Exp(-k * tEnd);

        double e1 = Math.Abs(IntegrateRk4(sys, new[] { 1.0 }, tEnd, 0.02) - exact);
        double e2 = Math.Abs(IntegrateRk4(sys, new[] { 1.0 }, tEnd, 0.01) - exact);
        double e3 = Math.Abs(IntegrateRk4(sys, new[] { 1.0 }, tEnd, 0.005) - exact);

        Assert.True(e1 > 1e-12, "errors too small to measure order");
        // Halving h must cut the error by ~2^4 = 16.
        double ratio12 = e1 / e2;
        double ratio23 = e2 / e3;
        Assert.InRange(ratio12, 14.0, 18.0);
        Assert.InRange(ratio23, 14.0, 18.0);
    }

    [Fact]
    public void Rk4_ExponentialDecay_AccurateAtProductionStep()
    {
        const double k = 3.0;
        var sys = new DecaySystem(k);
        double y = IntegrateRk4(sys, new[] { 1.0 }, 2.0, 0.001);
        Assert.True(Math.Abs(y - Math.Exp(-6.0)) < 1e-10, $"y={y}");
    }

    [Fact]
    public void Rk45_Oscillator_TenPeriods_MachinePrecision()
    {
        const double omega = 2.0 * Math.PI;
        var sys = new OscillatorSystem(omega);
        var rk45 = new Rk45(relTol: 1e-12, absTol: 1e-14);

        var y = new[] { 1.0, 0.0 };
        double t = 0.0, h = 0.1;
        double tEnd = 10.0; // 10 full periods
        while (t < tEnd - 1e-12)
        {
            var (hTaken, hNext) = rk45.Step(sys, t, y, h, tEnd - t);
            t += hTaken;
            h = hNext;
        }

        // y(10) = 1, v(10) = 0.
        Assert.True(Math.Abs(y[0] - 1.0) < 1e-8, $"y={y[0]}");
        Assert.True(Math.Abs(y[1] - 0.0) < 1e-8, $"v={y[1]}");
    }

    [Fact]
    public void Rk45_RejectsBadToleranceCollapse()
    {
        // Sanity: the adaptive stepper must actually advance without throwing.
        var sys = new DecaySystem(1.0);
        var rk45 = new Rk45();
        var y = new[] { 1.0 };
        var (hTaken, hNext) = rk45.Step(sys, 0.0, y, 0.1, 1.0);
        Assert.True(hTaken > 0.0 && hTaken <= 0.1, $"taken step {hTaken}");
        Assert.True(hNext > 0.01, $"suggested next step {hNext}");
        Assert.True(y[0] < 1.0 && y[0] > Math.Exp(-1.1), $"y={y[0]}");
    }
}
