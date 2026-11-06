namespace FourTrac.Core.Stats;

/// <summary>
/// One highest-density-region band: the boundary rings of the set of grid
/// cells whose kernel density reaches the band's threshold (the smallest
/// density level whose cells hold at least the requested fraction of the
/// cloud's mass).
/// </summary>
/// <param name="Fraction">Requested containment fraction (e.g. 0.5, 0.95).</param>
/// <param name="DensityThreshold">KDE density cutoff (1/m^2) that realised it.</param>
/// <param name="Rings">Closed boundary rings (first point repeated at the end); CCW = outer, CW = hole.</param>
/// <param name="AreaM2">Net region area: the sum of the rings' signed shoelace areas (holes subtract).</param>
public sealed record HdrBand(
    double Fraction,
    double DensityThreshold,
    IReadOnlyList<IReadOnlyList<Vec2>> Rings,
    double AreaM2);

/// <summary>
/// HDR analysis of an origin cloud: Gaussian-kernel density on a regular
/// grid, mass-ranked thresholds per requested fraction, marching-squares
/// contours, plus basic location/spread summaries of the raw cloud.
/// </summary>
public sealed record HdrResult(
    IReadOnlyList<HdrBand> Bands,
    Vec2 Mean,
    Vec2 Median,
    Vec2 Mode,
    double CellSizeM,
    double BandwidthM);

/// <summary>
/// Kernel-density HDR builder (PLAN.md §5: the solver never prints a point,
/// only 50 %/95 % regions). The grid is padded by 3 bandwidths + 2 cells past
/// the cloud so every supported region closes inside the grid.
/// </summary>
public static class Hdr
{
    private const double KernelExtentInBandwidths = 3.0;
    private const long MaxGridNodes = 16_000_000;

    /// <summary>
    /// Compute HDR bands for the given origin cloud (metres, one frame).
    /// <paramref name="fractions"/> defaults to {0.5, 0.95}; pass an explicit
    /// sequence to override. <paramref name="bandwidthM"/> = 0 selects
    /// Scott's 2-D rule-of-thumb (pooled sigma * n^-1/6) from the data -
    /// appropriate for unimodal clouds; strongly non-convex clouds (e.g. a
    /// ring-shaped truth) should pass an explicit bandwidth matched to their
    /// local spread, or the pooled sigma over-smooths them into a disk.
    /// </summary>
    /// <param name="sampleCalibrated">
    /// false (default): the Gaussian-ellipse eigen-axis calibration of the mass
    /// target (PLAN.md §6). true: the threshold is set so that the requested
    /// fraction of the cloud's OWN points lies inside the band. The Gaussian
    /// calibration is exact only for blob-shaped clouds; on the banana-shaped
    /// origin clouds of the flat model it under-covers the 50 % band (measured
    /// in CoverageStudy). The sample calibration is non-parametric.
    /// </param>
    public static HdrResult Compute(
        IReadOnlyList<Vec2> points,
        double cellSizeM,
        double[]? fractions = null,
        double bandwidthM = 0.0,
        bool sampleCalibrated = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 1)
            throw new ArgumentException("Need at least one point.", nameof(points));
        if (!(cellSizeM > 0))
            throw new ArgumentOutOfRangeException(nameof(cellSizeM), "Cell size must be positive.");
        if (!(bandwidthM >= 0))
            throw new ArgumentOutOfRangeException(nameof(bandwidthM), "Bandwidth must be non-negative.");

        fractions ??= new[] { 0.5, 0.95 };
        foreach (double f in fractions)
        {
            if (f <= 0 || f > 1)
                throw new ArgumentOutOfRangeException(nameof(fractions), "Fractions must be in (0, 1].");
        }

        // Location summaries (independent of the grid).
        Vec2 mean = Mean(points);
        Vec2 median = Median(points);

        // Bandwidth: Scott's 2-D rule-of-thumb, with a degenerate fallback.
        double sxx = 0, syy = 0;
        if (points.Count > 1)
        {
            foreach (var p in points)
            {
                sxx += (p.X - mean.X) * (p.X - mean.X);
                syy += (p.Y - mean.Y) * (p.Y - mean.Y);
            }
            sxx /= points.Count - 1;
            syy /= points.Count - 1;
        }
        double pooledSigma = Math.Sqrt(0.5 * (sxx + syy));

        double h = bandwidthM;
        if (h == 0.0)
        {
            h = Math.Max(pooledSigma, 0.5 * cellSizeM) * Math.Pow(points.Count, -1.0 / 6.0);
            if (!(h > 0))
                h = cellSizeM;
        }

        // Kernel-smoothing bias correction (V2 coverage calibration): a level
        // set of the SMOOTHED density at mass p' does NOT contain p' of the
        // underlying cloud - the kernel widens every axis by h, so the level
        // set is inflated. Under a Gaussian model the set is the ellipse
        // Z1^2/k1 + Z2^2/k2 <= c^2 (c^2 = -2 ln(1-p'), Z_i standard normal,
        // k_i = 1 + (h/sigma_i)^2 the kernel's variance addition along the
        // covariance EIGEN-AXES), and its true coverage follows from a 1-D
        // quadrature (SmoothedLevelCoverage). Each band's mass target is
        // therefore bisection-inverted so the published band's TRUE
        // containment equals its label: a "95 %" band contains 95 % of the
        // unsmoothed cloud, at any n and any anisotropy.
        //
        // Measured on the V2 demo (n = 40 clouds, elongated ~4:1) with a
        // pooled-sigma correction: 50 % band covered 69.3 % -> 65.9 %, 95 %
        // covered 97.8 % -> 97.4 % - pooled sigma under-corrects because
        // coverage is limited by the WIDE axis; the eigen-axis integral
        // captures exactly that. Calibration is exact for single-blob
        // Gaussian clouds and approximate for multi-modal ones (the harness
        // measures the residual).
        double sxy = 0;
        if (points.Count > 1)
        {
            foreach (var p in points)
                sxy += (p.X - mean.X) * (p.Y - mean.Y);
            sxy /= points.Count - 1;
        }
        double trace = sxx + syy;
        double disc = Math.Sqrt(Math.Max(0.0, 0.25 * trace * trace - (sxx * syy - sxy * sxy)));
        double sig1 = Math.Sqrt(Math.Max(0.0, 0.5 * trace + disc));   // wide axis
        double sig2 = Math.Sqrt(Math.Max(0.0, 0.5 * trace - disc));   // narrow axis
        double k1 = sig1 > 0 ? 1.0 + (h / sig1) * (h / sig1) : 1.0;
        double k2 = sig2 > 0 ? 1.0 + (h / sig2) * (h / sig2) : 1.0;

        // Grid with padding: kernels are truncated at 3h, regions must fit.
        double pad = KernelExtentInBandwidths * h + 2.0 * cellSizeM;
        double minX = points[0].X, maxX = points[0].X, minY = points[0].Y, maxY = points[0].Y;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        double x0 = minX - pad, x1 = maxX + pad;
        double y0 = minY - pad, y1 = maxY + pad;
        int nx = (int)Math.Ceiling((x1 - x0) / cellSizeM) + 1;
        int ny = (int)Math.Ceiling((y1 - y0) / cellSizeM) + 1;
        if (nx < 3 || ny < 3) { nx = Math.Max(nx, 3); ny = Math.Max(ny, 3); }
        if ((long)nx * ny > MaxGridNodes)
            throw new InvalidOperationException(
                $"Grid {nx}x{ny} exceeds {MaxGridNodes} nodes - increase the cell size.");

        // Gaussian KDE, separable per axis, truncated at 3h.
        var rho = new double[nx * ny];
        double invTwoH2 = 1.0 / (2.0 * h * h);
        double span = KernelExtentInBandwidths * h;

        foreach (var p in points)
        {
            int i0 = Math.Max(0, (int)Math.Floor((p.X - span - x0) / cellSizeM));
            int i1 = Math.Min(nx - 1, (int)Math.Ceiling((p.X + span - x0) / cellSizeM));
            int j0 = Math.Max(0, (int)Math.Floor((p.Y - span - y0) / cellSizeM));
            int j1 = Math.Min(ny - 1, (int)Math.Ceiling((p.Y + span - y0) / cellSizeM));

            var wx = new double[i1 - i0 + 1];
            for (int i = i0; i <= i1; i++)
            {
                double d = x0 + i * cellSizeM - p.X;
                wx[i - i0] = Math.Exp(-d * d * invTwoH2);
            }

            for (int j = j0; j <= j1; j++)
            {
                double d = y0 + j * cellSizeM - p.Y;
                double wy = Math.Exp(-d * d * invTwoH2);
                if (wy < 1e-12) continue;
                int row = j * nx;
                for (int i = i0; i <= i1; i++)
                    rho[row + i] += wx[i - i0] * wy;
            }
        }

        double norm = 1.0 / (points.Count * 2.0 * Math.PI * h * h);
        for (int k = 0; k < rho.Length; k++)
            rho[k] *= norm;

        // Mass-ranked node order (computed once; thresholds are nested).
        var order = Enumerable.Range(0, rho.Length)
            .OrderByDescending(k => rho[k])
            .ToArray();

        double cellArea = cellSizeM * cellSizeM;
        double totalMass = 0.0;
        foreach (double v in rho) totalMass += v * cellArea;

        var bands = new List<HdrBand>(fractions.Length);
        foreach (double fraction in fractions)
        {
            double threshold;
            if (sampleCalibrated)
            {
                threshold = SampleThreshold(points, rho, nx, x0, y0, cellSizeM, fraction);
            }
            else
            {
                // Deflated mass target: the label stays `fraction`, the threshold
                // compensates for the kernel widening (see k1/k2 above).
                double calibrated = CalibratedMass(fraction, k1, k2);
                threshold = SolveThreshold(rho, order, cellArea, totalMass, calibrated);
            }

            var rings = MarchingSquares.Extract(rho, nx, ny, x0, y0, cellSizeM, threshold);
            if (rings.Count == 0)
            {
                // Degenerate (nothing straddles): fall back to the padded
                // grid rectangle rather than reporting a zero-area region.
                rings = new List<IReadOnlyList<Vec2>>
                {
                    new[]
                    {
                        new Vec2(x0, y0),
                        new Vec2(x0 + (nx - 1) * cellSizeM, y0),
                        new Vec2(x0 + (nx - 1) * cellSizeM, y0 + (ny - 1) * cellSizeM),
                        new Vec2(x0, y0 + (ny - 1) * cellSizeM),
                        new Vec2(x0, y0),
                    },
                };
            }

            double area = 0.0;
            foreach (var ring in rings)
                area += MarchingSquares.SignedArea(ring);

            // Net area: outers (CCW) positive, holes (CW) negative.
            bands.Add(new HdrBand(fraction, threshold, rings, Math.Abs(area)));
        }

        // MAP estimate: the densest sampled node (the KDE mode).
        int modeIdx = 0;
        double modeVal = double.NegativeInfinity;
        for (int k = 0; k < rho.Length; k++)
        {
            if (rho[k] > modeVal) { modeVal = rho[k]; modeIdx = k; }
        }
        var mode = new Vec2(x0 + (modeIdx % nx) * cellSizeM, y0 + (modeIdx / nx) * cellSizeM);

        return new HdrResult(bands, mean, median, mode, cellSizeM, h);
    }

    /// <summary>
    /// Even-odd point-in-region test against a band's rings (all rings
    /// together): a point inside an outer ring but inside a hole counts as
    /// OUTSIDE - crossing-count parity handles the nesting automatically.
    /// </summary>
    public static bool Contains(IReadOnlyList<IReadOnlyList<Vec2>> rings, Vec2 point)
    {
        ArgumentNullException.ThrowIfNull(rings);
        int crossings = 0;
        foreach (var ring in rings)
        {
            for (int i = 0; i + 1 < ring.Count; i++)
            {
                Vec2 a = ring[i], b = ring[i + 1];
                // Half-open vertical crossing rule (both endpoints counted once).
                if ((a.Y > point.Y) != (b.Y > point.Y))
                {
                    double xAt = a.X + (point.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                    if (xAt > point.X)
                        crossings++;
                }
            }
        }
        return (crossings & 1) == 1;
    }

    /// <summary>
    /// Error function, Abramowitz &amp; Stegun 7.1.26 (max |ε| ≈ 1.5e-7 - the
    /// BCL ships no Math.Erf, and this precision is far below the
    /// calibration's needs).
    /// </summary>
    private static double Erf(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        double a = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.3275911 * a);
        double poly = (((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592;
        return sign * (1.0 - poly * t * Math.Exp(-a * a));
    }

    /// <summary>
    /// True (unsmoothed) Gaussian coverage of the level set of the SMOOTHED
    /// density at mass <paramref name="pPrime"/>: the set is the ellipse
    /// Z1²/k1 + Z2²/k2 ≤ c² with c² = −2 ln(1−p′) (Z_i standard normal along
    /// the covariance eigen-axes, k_i the per-axis kernel variance addition);
    /// the coverage integral reduces to a 1-D Gaussian quadrature over Z2.
    /// </summary>
    private static double SmoothedLevelCoverage(double pPrime, double k1, double k2)
    {
        double c2 = -2.0 * Math.Log(1.0 - pPrime);
        double bound = Math.Sqrt(k2 * c2);
        if (!(bound > 0))
            return 0.0;

        const int steps = 400;
        double dz = 2.0 * bound / steps;
        double sum = 0.0;
        for (int i = 0; i <= steps; i++)
        {
            double z = -bound + i * dz;
            double inner = k1 * (c2 - z * z / k2);
            double f = inner <= 0.0
                ? 0.0
                : Erf(Math.Sqrt(inner / 2.0)) * Math.Exp(-0.5 * z * z) / Math.Sqrt(2.0 * Math.PI);
            sum += (i == 0 || i == steps) ? f : (i % 2 == 1 ? 4.0 * f : 2.0 * f);
        }
        return sum * dz / 3.0;
    }

    /// <summary>
    /// Bisection inverse of <see cref="SmoothedLevelCoverage"/>: the smoothed
    /// mass whose level set contains <paramref name="targetCoverage"/> of the
    /// underlying cloud (monotone in p′). With k1 = k2 = 1 (no smoothing or
    /// degenerate cloud) this is the identity.
    /// </summary>
    private static double CalibratedMass(double targetCoverage, double k1, double k2)
    {
        if (k1 <= 1.0 && k2 <= 1.0)
            return targetCoverage;

        double lo = 1e-12, hi = 1.0 - 1e-12;
        for (int i = 0; i < 48; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (SmoothedLevelCoverage(mid, k1, k2) < targetCoverage)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// Density level such that at least <paramref name="fraction"/> of the cloud's
    /// points have a density at or above it (the points' own grid densities).
    /// </summary>
    private static double SampleThreshold(
        IReadOnlyList<Vec2> points, double[] rho, int nx, double x0, double y0, double cellSizeM, double fraction)
    {
        int ny = rho.Length / nx;
        var dens = new double[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            int i = Math.Clamp((int)Math.Round((points[k].X - x0) / cellSizeM), 0, nx - 1);
            int j = Math.Clamp((int)Math.Round((points[k].Y - y0) / cellSizeM), 0, ny - 1);
            dens[k] = rho[j * nx + i];
        }
        Array.Sort(dens);
        int need = (int)Math.Ceiling(fraction * points.Count);
        need = Math.Clamp(need, 1, points.Count);
        return dens[points.Count - need];
    }

    /// <summary>
    /// Density cutoff whose cell mass (accumulated in descending order)
    /// reaches <paramref name="fraction"/> of the total. Ties at the cutoff
    /// cell are all kept by the ≥ test, so the region never contains LESS
    /// than the requested fraction.
    /// </summary>
    private static double SolveThreshold(double[] rho, int[] order, double cellArea, double totalMass, double fraction)
    {
        if (totalMass <= 0)
            return double.PositiveInfinity;

        double target = fraction * totalMass;
        double cum = 0.0;
        double last = rho[order[0]];
        foreach (int k in order)
        {
            last = rho[k];
            cum += last * cellArea;
            if (cum >= target)
                return last;
        }
        return last;
    }

    /// <summary>
    /// The p-quantile of the radial distance from <paramref name="center"/>
    /// (empirical, linear interpolation between order statistics).
    /// </summary>
    public static double RadialQuantile(IReadOnlyList<Vec2> points, Vec2 center, double p)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("Need at least one point.", nameof(points));
        if (p <= 0 || p > 1)
            throw new ArgumentOutOfRangeException(nameof(p), "p must be in (0, 1].");

        var d = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
            d[i] = (points[i] - center).Length;
        Array.Sort(d);

        double idx = p * (d.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi)
            return d[lo];
        return d[lo] + (idx - lo) * (d[hi] - d[lo]);
    }

    private static Vec2 Mean(IReadOnlyList<Vec2> points)
    {
        double sx = 0, sy = 0;
        foreach (var p in points) { sx += p.X; sy += p.Y; }
        return new Vec2(sx / points.Count, sy / points.Count);
    }

    private static Vec2 Median(IReadOnlyList<Vec2> points)
    {
        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++) { xs[i] = points[i].X; ys[i] = points[i].Y; }
        Array.Sort(xs);
        Array.Sort(ys);
        return new Vec2(MedianOf(xs), MedianOf(ys));

        static double MedianOf(double[] a)
        {
            int m = a.Length / 2;
            return a.Length % 2 == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
        }
    }
}
