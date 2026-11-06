using FourTrac.Core.Inverse;
using FourTrac.Core.Stats;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// M3.2 (PLAN.md §5): HDR statistics - marching-squares contours, region
/// areas, and cloud summaries. The reference targets are analytic:
///
/// - A paraboloid field's level set is an exact circle (validates the contour
///   extractor itself: interpolation, closure, orientation, shoelace area).
/// - The 2-D Gaussian HDR is an ellipse with area pi * chi2_2(q) * s1 * s2,
///   chi2_2(0.50) = 1.3863, chi2_2(0.95) = 5.9915 - so the 95 %/50 % area
///   RATIO (4.322) must come out robust even though absolute areas carry
///   bandwidth + grid error (tolerances below).
/// - An annulus cloud's 95 % region must be a ring WITH A HOLE (exercises the
///   inside-left orientation: outer CCW, hole CW, net area = shoelace sum).
/// </summary>
public class StatsTests
{
    private const double Chi2TwoQ50 = 1.386294361;
    private const double Chi2TwoQ95 = 5.991464547;

    /// <summary>Exact circle from an analytic paraboloid level set.</summary>
    [Fact]
    public void MarchingSquares_ParaboloidLevelSet_ExtractsCircle()
    {
        const int n = 401;            // nodes, spacing 1 m
        const double cell = 1.0;
        const double radius = 50.0;
        double x0 = -(n - 1) * 0.5 * cell, y0 = x0;

        var values = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            double y = y0 + j * cell;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * cell;
                values[j * n + i] = -(x * x + y * y);   // level set at -r^2 = circle
            }
        }

        var rings = MarchingSquares.Extract(values, n, n, x0, y0, cell, -radius * radius);

        Assert.Single(rings);
        var ring = rings[0];
        Assert.True(ring.Count >= 16, $"Circle should have many vertices, got {ring.Count}.");

        // Closed: first point repeated at the end.
        Assert.Equal(ring[0].X, ring[^1].X, 9);
        Assert.Equal(ring[0].Y, ring[^1].Y, 9);

        // Orientation: outside (below threshold) on the right = CCW = positive area.
        double area = MarchingSquares.SignedArea(ring);
        double exact = Math.PI * radius * radius;
        Assert.True(area > 0, $"Ring must be CCW (outside on the right), area {area}.");
        Assert.True(Math.Abs(area - exact) <= 0.01 * exact,
            $"Circle area {area:F2} vs exact {exact:F2} (1 % tol).");

        // Every vertex sits on the level set (radius within a cell fraction).
        foreach (var p in ring)
        {
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.InRange(r, radius - 0.6, radius + 0.6);
        }
    }

    /// <summary>
    /// Gaussian cloud: HDR areas against the analytic chi-square ellipses,
    /// the area RATIO against 4.322, and ring sanity (closed, CCW, single).
    /// </summary>
    [Fact]
    public void Hdr_GaussianCloud_MatchesChiSquareEllipses()
    {
        const int n = 20_000;
        const double sx = 10.0, sy = 4.0;
        var rng = new Xoshiro256(987654321);
        var pts = new List<Vec2>(n);
        for (int i = 0; i < n; i++)
            pts.Add(new Vec2(sx * rng.NextNormal(), sy * rng.NextNormal()));

        var result = Hdr.Compute(pts, cellSizeM: 1.0);

        Assert.Equal(2, result.Bands.Count);
        var b50 = result.Bands[0];
        var b95 = result.Bands[1];
        Assert.Equal(0.5, b50.Fraction, 9);
        Assert.Equal(0.95, b95.Fraction, 9);

        double exact50 = Math.PI * Chi2TwoQ50 * sx * sy;
        double exact95 = Math.PI * Chi2TwoQ95 * sx * sy;

        // Band density ordering: the tighter region needs the higher density.
        Assert.True(b50.DensityThreshold > b95.DensityThreshold,
            $"50 % threshold {b50.DensityThreshold} must exceed 95 % {b95.DensityThreshold}.");

        // Absolute areas: bandwidth smoothing + grid quantisation, tol 15 %.
        Assert.InRange(b50.AreaM2, 0.85 * exact50, 1.15 * exact50);
        Assert.InRange(b95.AreaM2, 0.85 * exact95, 1.15 * exact95);

        // Ratio is the robust shape check: 4.322 +/- 12 %.
        double ratio = b95.AreaM2 / b50.AreaM2;
        double exactRatio = Chi2TwoQ95 / Chi2TwoQ50;
        Assert.InRange(ratio, 0.88 * exactRatio, 1.12 * exactRatio);

        // Unimodal: one ring each, closed, CCW (positive signed area).
        foreach (var band in result.Bands)
        {
            Assert.Single(band.Rings);
            var ring = band.Rings[0];
            Assert.Equal(ring[0].X, ring[^1].X, 9);
            Assert.Equal(ring[0].Y, ring[^1].Y, 9);
            Assert.True(MarchingSquares.SignedArea(ring) > 0, "Outer ring must be CCW.");
        }

        // Location summaries at the cloud centre.
        Assert.InRange(result.Median.X, -0.5, 0.5);
        Assert.InRange(result.Median.Y, -0.5, 0.5);
        Assert.InRange(result.Mean.X, -0.5, 0.5);
        Assert.InRange(result.Mean.Y, -0.5, 0.5);
        Assert.InRange(result.Mode.X, -1.5, 1.5);   // KDE mode lands on the densest node near centre
        Assert.InRange(result.Mode.Y, -1.5, 1.5);

        // Radial quantiles about the median: monotone and in physical scale.
        double q50 = Hdr.RadialQuantile(pts, result.Median, 0.5);
        double q95 = Hdr.RadialQuantile(pts, result.Median, 0.95);
        Assert.True(q50 > 0 && q50 < q95, $"Quantiles must be positive and ordered: {q50}, {q95}.");
        Assert.InRange(q95, 5.0, 30.0);
    }

    /// <summary>
    /// Ring-shaped cloud: the 95 % region must have a hole (CW inner ring),
    /// and the net area must match the radial band the priors imply.
    /// </summary>
    [Fact]
    public void Hdr_AnnulusCloud_ProducesHoleWithCorrectNetArea()
    {
        const int n = 15_000;
        const double r0 = 50.0, sr = 3.0;
        const double q975 = 1.959963985;                 // standard normal 97.5 %
        var rng = new Xoshiro256(246801357);
        var pts = new List<Vec2>(n);
        for (int i = 0; i < n; i++)
        {
            double theta = 2.0 * Math.PI * rng.NextDouble();
            double r = r0 + sr * rng.NextNormal();
            pts.Add(new Vec2(r * Math.Cos(theta), r * Math.Sin(theta)));
        }

        // Explicit bandwidth: the pooled-sigma Scott rule would over-smooth a
        // ring (global sigma ~ 35 m) into a filled disk - documented in Hdr.Compute.
        var result = Hdr.Compute(pts, cellSizeM: 1.5, new[] { 0.95 }, bandwidthM: 3.0);

        var band = Assert.Single(result.Bands);
        Assert.True(band.Rings.Count >= 2,
            $"Annulus 95 % region needs outer + hole, got {band.Rings.Count} ring(s).");

        int holes = band.Rings.Count(r => MarchingSquares.SignedArea(r) < 0);
        Assert.True(holes >= 1, "Expected at least one CW (hole) ring.");

        // Analytic target: the radial profile convolved with the kernel has
        // sigma_eff = sqrt(sr^2 + h^2) (here h = sr = 3 -> 4.243 m), and a
        // density-level region of that smoothed profile spans
        // r0 +/- 1.96 sigma_eff. (The measurement: observed vs this prediction
        // agree to ~0.2 %; ignoring the kernel widening would predict a
        // 20 %-too-small region - itemized in §8.3, not absorbed.)
        double srEff = Math.Sqrt(sr * sr + 3.0 * 3.0);
        double rOuter = r0 + q975 * srEff, rInner = r0 - q975 * srEff;
        double exact = Math.PI * (rOuter * rOuter - rInner * rInner);
        Assert.InRange(band.AreaM2, 0.90 * exact, 1.10 * exact);

        // The hole must straddle the centre: its ring centroid is near origin.
        var holeRing = band.Rings.First(r => MarchingSquares.SignedArea(r) < 0);
        double cx = 0, cy = 0;
        foreach (var p in holeRing) { cx += p.X; cy += p.Y; }
        cx /= holeRing.Count; cy /= holeRing.Count;
        Assert.True(Math.Sqrt(cx * cx + cy * cy) < 5.0, $"Hole centroid off centre: ({cx:F2}, {cy:F2}).");
    }

    /// <summary>
    /// Point-in-region parity: outer ring counts, hole ring subtracts - the
    /// even-odd rule across ALL rings of a band handles the nesting.
    /// </summary>
    [Fact]
    public void Contains_EvenOdd_AcrossNestedRings()
    {
        // Outer square 0..10 (CCW), hole 4..6 (CW), both closed.
        var outer = new[]
        {
            new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10), new Vec2(0, 0),
        };
        var hole = new[]
        {
            new Vec2(4, 4), new Vec2(4, 6), new Vec2(6, 6), new Vec2(6, 4), new Vec2(4, 4),
        };
        var rings = new[] { outer, hole };

        Assert.True(Hdr.Contains(rings, new Vec2(2, 2)));      // inside outer
        Assert.False(Hdr.Contains(rings, new Vec2(5, 5)));     // inside hole => outside region
        Assert.False(Hdr.Contains(rings, new Vec2(15, 5)));    // beyond outer
        Assert.False(Hdr.Contains(rings, new Vec2(-1, -1)));   // before outer
        Assert.False(Hdr.Contains(rings, new Vec2(4, 4)));     // exactly on the hole corner edge rule

        // Second region far right: parity across disjoint rings stays independent.
        var isle = new[]
        {
            new Vec2(20, 0), new Vec2(22, 0), new Vec2(22, 2), new Vec2(20, 2), new Vec2(20, 0),
        };
        Assert.True(Hdr.Contains(new[] { isle }, new Vec2(21, 1)));
    }

    /// <summary>Degenerate inputs must not throw or divide by zero.</summary>
    [Fact]
    public void Hdr_SinglePoint_IsHandled()
    {
        var result = Hdr.Compute(new[] { new Vec2(3.0, -7.0) }, cellSizeM: 1.0);

        // Default fractions {0.5, 0.95}: both bands exist. On a single point
        // the sampled bump is under-resolved by the 1 m grid, so both mass
        // ranks resolve to the same peak cells and the bands coincide - that
        // is honest behaviour for n = 1, not an error.
        Assert.Equal(2, result.Bands.Count);
        foreach (var band in result.Bands)
        {
            Assert.True(band.Rings.Count >= 1, "A density bump still has a contour.");
            Assert.True(band.AreaM2 > 0 && band.AreaM2 < 30,
                $"Single-point region should be a few cells, got {band.AreaM2:F2} m^2.");
        }

        Assert.Equal(3.0, result.Median.X, 9);
        Assert.Equal(-7.0, result.Median.Y, 9);
        Assert.Equal(0.0, Hdr.RadialQuantile(new[] { new Vec2(3.0, -7.0) }, result.Median, 0.95), 9);
    }
}
