namespace FourTrac.Core.Stats;

/// <summary>
/// Iso-contour extraction on a regular scalar grid (marching squares), used to
/// turn a kernel-density field into HDR boundary rings (PLAN.md §5 output:
/// 50 %/95 % regions as closed contours).
///
/// Conventions:
/// - Values live on grid NODES: <c>values[j * nx + i]</c> at world position
///   <c>(x0 + i*cell, y0 + j*cell)</c>.
/// - Segments are directed with the ABOVE-threshold region on the LEFT, so
///   chaining them yields closed rings that are CCW for outer boundaries and
///   CW for holes; the sum of the rings' signed (shoelace) areas is then the
///   net region area - holes subtract automatically.
/// - Edge crossings are linearly interpolated between the two straddling
///   nodes; a zero-span edge uses the midpoint.
/// - Ambiguous saddles (corner pattern 0101 / 1010) are resolved by the
///   cell-centre average vs the threshold: a centre above the threshold
///   connects the above-threshold corners through the cell centre. This is the
///   standard average decider; both branches are orientation-consistent.
/// - If the region touches the grid border, the chain runs off the grid; such
///   polylines are closed with a straight segment back to their start. Pad the
///   grid (the HDR builder pads by 3 bandwidths) so this never happens for a
///   supported region.
/// </summary>
public static class MarchingSquares
{
    private sealed record Seg(long FromKey, long ToKey, Vec2 FromPt, Vec2 ToPt);

    // Edge keys. Horizontal edge between nodes (i, j)-(i+1, j): even key.
    // Vertical edge between nodes (i, j)-(i, j+1): odd key. (i in the low
    // 32 bits, j in the high 32 bits - collision-free for any grid the HDR
    // builder can allocate, and parity keeps H/V domains disjoint.)
    private static long HKey(int i, int j) => ((long)j << 33) | ((uint)i << 1);
    private static long VKey(int i, int j) => HKey(i, j) + 1;

    /// <summary>
    /// Extract closed rings at <paramref name="threshold"/> (cells with
    /// value ≥ threshold are "inside"). Each ring is returned with its first
    /// point repeated at the end. Empty list if nothing straddles the
    /// threshold; the caller handles that degenerate case.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Vec2>> Extract(
        double[] values, int nx, int ny, double x0, double y0, double cell, double threshold)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length < nx * ny)
            throw new ArgumentException($"values has {values.Length} entries, expected {nx * ny}.", nameof(values));
        if (nx < 2 || ny < 2)
            throw new ArgumentOutOfRangeException(nameof(nx), "Grid must be at least 2x2 nodes.");
        if (cell <= 0)
            throw new ArgumentOutOfRangeException(nameof(cell), "Cell size must be positive.");

        var segments = new List<Seg>();

        for (int j = 0; j + 1 < ny; j++)
        {
            for (int i = 0; i + 1 < nx; i++)
            {
                double v0 = values[j * nx + i];               // LL
                double v1 = values[j * nx + i + 1];           // LR
                double v2 = values[(j + 1) * nx + i + 1];     // UR
                double v3 = values[(j + 1) * nx + i];         // UL

                int caseIndex = 0;
                if (v0 >= threshold) caseIndex |= 1;
                if (v1 >= threshold) caseIndex |= 2;
                if (v2 >= threshold) caseIndex |= 4;
                if (v3 >= threshold) caseIndex |= 8;
                if (caseIndex == 0 || caseIndex == 15)
                    continue;

                // Edge crossing points (computed lazily per cell edge used).
                double cellX = x0 + i * cell;
                double cellY = y0 + j * cell;
                Vec2 P_B() => H(i, j, v0, v1, cellX, cellY, cell, threshold);
                Vec2 P_R() => V(i + 1, j, v1, v2, cellX + cell, cellY, cell, threshold);
                Vec2 P_T() => H(i, j + 1, v3, v2, cellX, cellY + cell, cell, threshold);
                Vec2 P_L() => V(i, j, v0, v3, cellX, cellY, cell, threshold);

                long kB = HKey(i, j), kR = VKey(i + 1, j), kT = HKey(i, j + 1), kL = VKey(i, j);

                switch (caseIndex)
                {
                    case 1:  segments.Add(new Seg(kB, kL, P_B(), P_L())); break;   // LL only
                    case 2:  segments.Add(new Seg(kR, kB, P_R(), P_B())); break;   // LR only
                    case 4:  segments.Add(new Seg(kT, kR, P_T(), P_R())); break;   // UR only
                    case 8:  segments.Add(new Seg(kL, kT, P_L(), P_T())); break;   // UL only
                    case 3:  segments.Add(new Seg(kR, kL, P_R(), P_L())); break;   // bottom band
                    case 6:  segments.Add(new Seg(kT, kB, P_T(), P_B())); break;   // right band
                    case 12: segments.Add(new Seg(kL, kR, P_L(), P_R())); break;   // top band
                    case 9:  segments.Add(new Seg(kB, kT, P_B(), P_T())); break;   // left band
                    case 7:  segments.Add(new Seg(kT, kL, P_T(), P_L())); break;   // UL outside
                    case 11: segments.Add(new Seg(kR, kT, P_R(), P_T())); break;   // UR outside
                    case 13: segments.Add(new Seg(kB, kR, P_B(), P_R())); break;   // LR outside
                    case 14: segments.Add(new Seg(kL, kB, P_L(), P_B())); break;   // LL outside
                    case 5:                                                        // LL+UR diagonal
                        if (0.25 * (v0 + v1 + v2 + v3) >= threshold)
                        {
                            segments.Add(new Seg(kB, kR, P_B(), P_R()));           // centre in: isolate LR, UL
                            segments.Add(new Seg(kT, kL, P_T(), P_L()));
                        }
                        else
                        {
                            segments.Add(new Seg(kB, kL, P_B(), P_L()));           // centre out: two corner blobs
                            segments.Add(new Seg(kT, kR, P_T(), P_R()));
                        }
                        break;
                    case 10:                                                       // LR+UL diagonal
                        if (0.25 * (v0 + v1 + v2 + v3) >= threshold)
                        {
                            segments.Add(new Seg(kL, kB, P_L(), P_B()));           // centre in: isolate LL, UR
                            segments.Add(new Seg(kR, kT, P_R(), P_T()));
                        }
                        else
                        {
                            segments.Add(new Seg(kR, kB, P_R(), P_B()));           // centre out: two corner blobs
                            segments.Add(new Seg(kL, kT, P_L(), P_T()));
                        }
                        break;
                }
            }
        }

        return Chain(segments);
    }

    /// <summary>Shoelace signed area of a closed ring (CCW positive).</summary>
    public static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        if (ring.Count < 3)
            return 0.0;

        double sum = 0.0;
        for (int i = 0; i < ring.Count; i++)
        {
            Vec2 a = ring[i];
            Vec2 b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return 0.5 * sum;
    }

    private static Vec2 H(int i, int j, double va, double vb, double px, double py, double cell, double thr)
    {
        double t = Interpolate(va, vb, thr);
        return new Vec2(px + t * cell, py);
    }

    private static Vec2 V(int i, int j, double va, double vb, double px, double py, double cell, double thr)
    {
        double t = Interpolate(va, vb, thr);
        return new Vec2(px, py + t * cell);
    }

    private static double Interpolate(double va, double vb, double thr)
    {
        double span = vb - va;
        if (Math.Abs(span) < 1e-300)
            return 0.5;
        double t = (thr - va) / span;
        return t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
    }

    private static IReadOnlyList<IReadOnlyList<Vec2>> Chain(List<Seg> segments)
    {
        var rings = new List<IReadOnlyList<Vec2>>();
        if (segments.Count == 0)
            return rings;

        var byFrom = new Dictionary<long, Seg>(segments.Count);
        var byTo = new Dictionary<long, Seg>(segments.Count);
        foreach (var s in segments)
        {
            byFrom[s.FromKey] = s;
            byTo[s.ToKey] = s;
        }

        var used = new HashSet<Seg>();

        foreach (var seed in segments)
        {
            if (!used.Add(seed))
                continue;

            // Forward along inside-left direction.
            var forward = new List<Vec2> { seed.FromPt, seed.ToPt };
            long endKey = seed.ToKey;
            bool closed = false;
            while (byFrom.TryGetValue(endKey, out var next))
            {
                if (next == seed) { closed = true; break; }
                if (!used.Add(next)) break;          // rejoined an already-consumed chain
                forward.Add(next.ToPt);
                endKey = next.ToKey;
            }

            // Backward from the seed's start.
            var backward = new List<Vec2>();
            long startKey = seed.FromKey;
            while (!closed && byTo.TryGetValue(startKey, out var prev))
            {
                if (!used.Add(prev)) break;
                backward.Add(prev.FromPt);
                startKey = prev.FromKey;
                if (startKey == endKey) { closed = true; break; }
            }

            var ring = new List<Vec2>(backward.Count + forward.Count);
            for (int i = backward.Count - 1; i >= 0; i--)
                ring.Add(backward[i]);
            ring.AddRange(forward);

            if (ring.Count < 3)
                continue;

            // Border-touching chain: close it with a straight segment back.
            if (!closed)
                ring.Add(ring[0]);
            else if (ring[0].X != ring[^1].X || ring[0].Y != ring[^1].Y)
                ring.Add(ring[0]);

            rings.Add(ring);
        }

        return rings;
    }
}
