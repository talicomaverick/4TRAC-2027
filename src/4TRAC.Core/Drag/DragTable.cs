namespace FourTrac.Core.Drag;

/// <summary>Interpolation scheme between tabulated CD(Mach) nodes.</summary>
public enum InterpolationKind
{
    /// <summary>
    /// Piecewise-linear. The convention of the published reference standards:
    /// the entire reference ecosystem (JBM, gehtsoft BallisticCalculator1, Exbal)
    /// evaluates the G-tables linearly between nodes, and the tables themselves
    /// are published as piecewise-linear data. Golden acceptance (M2) pinned that
    /// linear evaluation reproduces the reference tables measurably better than
    /// PCHIP in the transonic zone (PCHIP smoothing shifts integrated drag by
    /// ~0.3 %, which shows up as ~1 % drop error at 1000 yd).
    /// </summary>
    Linear,

    /// <summary>
    /// Monotone cubic (Fritsch-Carlson/PCHIP). Reproduces node values exactly
    /// and cannot overshoot between nodes - used for sparse user-supplied CD
    /// tables where linear chords would cut badly through the transonic rise.
    /// </summary>
    Pchip,
}

/// <summary>
/// Tabulated drag data (CD vs Mach). Node values are exact; between nodes the
/// scheme is selectable: <see cref="InterpolationKind.Linear"/> for the bundled
/// reference standards (reference-ecosystem convention),
/// <see cref="InterpolationKind.Pchip"/> for sparse user tables (monotonicity-
/// preserving, no overshoot on the sharp transonic CD rise).
/// </summary>
public sealed class DragTable
{
    private readonly double[] _mach;
    private readonly double[] _cd;
    private readonly double[]? _slope; // Hermite slopes at nodes (PCHIP mode only)
    private readonly InterpolationKind _kind;

    private DragTable(double[] mach, double[] cd, InterpolationKind kind)
    {
        _mach = mach;
        _cd = cd;
        _kind = kind;
        _slope = kind == InterpolationKind.Pchip ? ComputePchipSlopes(mach, cd) : null;
    }

    public string? Id { get; private init; }
    public int Count => _mach.Length;
    public double MinMach => _mach[0];
    public double MaxMach => _mach[^1];
    public double CdAtNode(int index) => _cd[index];
    public double MachAtNode(int index) => _mach[index];

    public static DragTable FromPoints(IReadOnlyList<(double Mach, double Cd)> points, string? id = null,
        InterpolationKind kind = InterpolationKind.Pchip)
    {
        if (points.Count < 2)
            throw new ArgumentException("Drag table needs at least two points.", nameof(points));

        var sorted = points.OrderBy(p => p.Mach).ToArray();
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Mach - sorted[i - 1].Mach < 1e-12)
                throw new ArgumentException($"Duplicate Mach value {sorted[i].Mach} in drag table.");
            if (sorted[i].Cd < 0 || sorted[i - 1].Cd < 0)
                throw new ArgumentException("Drag coefficients must be non-negative.");
        }

        var mach = sorted.Select(p => p.Mach).ToArray();
        var cd = sorted.Select(p => p.Cd).ToArray();
        return new DragTable(mach, cd, kind) { Id = id };
    }

    /// <summary>
    /// Drag coefficient at Mach. Outside the tabulated span the value is clamped
    /// to the nearest endpoint (documented policy; the tables span M = 0..5,
    /// beyond small-arms flight envelopes).
    /// </summary>
    public double Cd(double mach)
    {
        if (mach <= _mach[0]) return _cd[0];
        if (mach >= _mach[^1]) return _cd[^1];

        // Binary search for the containing interval.
        int lo = 0, hi = _mach.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_mach[mid] <= mach) lo = mid;
            else hi = mid;
        }

        double h = _mach[hi] - _mach[lo];
        double t = (mach - _mach[lo]) / h;

        // Linear mode carries no Hermite slopes (the constructor only computes
        // them for PCHIP); the null check also narrows _slope for the branch
        // below so the compiler can prove the dereferences safe.
        if (_kind == InterpolationKind.Linear || _slope is null)
            return _cd[lo] + t * (_cd[hi] - _cd[lo]);

        double t2 = t * t;
        double t3 = t2 * t;
        // Cubic Hermite basis.
        double h00 = 2 * t3 - 3 * t2 + 1;
        double h10 = t3 - 2 * t2 + t;
        double h01 = -2 * t3 + 3 * t2;
        double h11 = t3 - t2;
        return h00 * _cd[lo] + h10 * h * _slope[lo] + h01 * _cd[hi] + h11 * h * _slope[hi];
    }

    /// <summary>PCHIP (Fritsch-Carlson) slopes: monotonicity-preserving on monotone data.</summary>
    private static double[] ComputePchipSlopes(double[] x, double[] y)
    {
        int n = x.Length;
        var h = new double[n - 1];
        var delta = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
            delta[i] = (y[i + 1] - y[i]) / h[i];
        }

        var d = new double[n];

        // Endpoint slopes: three-point formula, clamped to 3x the adjacent secant
        // (prevents overshoot at the table ends).
        d[0] = EndSlope(h[0], h.Length > 1 ? h[1] : h[0], delta[0], delta.Length > 1 ? delta[1] : delta[0]);
        d[n - 1] = EndSlope(h[n - 2], h.Length > 1 ? h[n - 3] : h[n - 2],
            delta[n - 2], delta.Length > 1 ? delta[n - 3] : delta[n - 2]);

        // Interior: weighted harmonic mean (Fritsch-Butland form of Fritsch-Carlson).
        for (int i = 1; i < n - 1; i++)
        {
            if (delta[i - 1] * delta[i] <= 0)
            {
                d[i] = 0.0;
            }
            else
            {
                double w1 = 2.0 * h[i] + h[i - 1];
                double w2 = h[i] + 2.0 * h[i - 1];
                d[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
            }
        }

        return d;
    }

    private static double EndSlope(double h0, double h1, double d0, double d1)
    {
        double slope = ((2.0 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
        if (slope * d0 <= 0.0)
            slope = 0.0;
        else if (Math.Abs(slope) > 3.0 * Math.Abs(d0))
            slope = 3.0 * d0;
        return slope;
    }
}
