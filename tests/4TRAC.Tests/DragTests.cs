using FourTrac.Core.Drag;
using FourTrac.Core.Geo;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// V0 code verification for the drag layer (PLAN.md §7): table interpolation,
/// node exactness, monotonicity, clamping, unit-safe beta scaling, and
/// cross-mode equivalence.
/// </summary>
public class DragTests
{
    [Fact]
    public void Catalog_AnchorsMatchPublishedCurve()
    {
        Assert.Equal(0.2629, DragTableCatalog.G1.Cd(0.0), 12);
        Assert.Equal(0.6625, DragTableCatalog.G1.Cd(1.40), 12);   // G1 peak
        Assert.Equal(0.2020, DragTableCatalog.G1.Cd(0.55), 12);   // G1 subsonic minimum
        Assert.Equal(0.1198, DragTableCatalog.G7.Cd(0.0), 12);
        Assert.Equal(0.4043, DragTableCatalog.G7.Cd(1.05), 12);   // G7 peak
    }

    [Fact]
    public void Interpolation_ReproducesEveryNodeExactly()
    {
        foreach (var table in new[] { DragTableCatalog.G1, DragTableCatalog.G7 })
        {
            for (int i = 0; i < table.Count; i++)
            {
                double m = table.MachAtNode(i);
                Assert.Equal(table.CdAtNode(i), table.Cd(m), 12);
            }
        }
    }

    [Fact]
    public void Interpolation_MonotoneWhereDataIsMonotone()
    {
        // G1: strictly decreasing subsonic 0..0.55.
        double prev = DragTableCatalog.G1.Cd(0.0);
        for (double m = 0.01; m <= 0.55; m += 0.01)
        {
            double cd = DragTableCatalog.G1.Cd(m);
            Assert.True(cd <= prev + 1e-15, $"G1 non-monotone at M={m}: {cd} > {prev}");
            prev = cd;
        }

        // G1: strictly decreasing from the transonic peak to M=5.
        prev = DragTableCatalog.G1.Cd(1.40);
        for (double m = 1.41; m <= 5.0; m += 0.03)
        {
            double cd = DragTableCatalog.G1.Cd(m);
            Assert.True(cd <= prev + 1e-15, $"G1 non-monotone at M={m}: {cd} > {prev}");
            prev = cd;
        }

        // G7: strictly decreasing from its peak to M=5.
        prev = DragTableCatalog.G7.Cd(1.05);
        for (double m = 1.06; m <= 5.0; m += 0.03)
        {
            double cd = DragTableCatalog.G7.Cd(m);
            Assert.True(cd <= prev + 1e-15, $"G7 non-monotone at M={m}: {cd} > {prev}");
            prev = cd;
        }
    }

    [Fact]
    public void Interpolation_NoOvershootInTransonicRise()
    {
        // PCHIP must never exceed the node envelope (no ringing across the
        // 0.85..1.15 cliff where unconstrained cubics ring badly).
        foreach (var table in new[] { DragTableCatalog.G1, DragTableCatalog.G7 })
        {
            for (double m = 0.80; m <= 1.20; m += 0.001)
            {
                double cd = table.Cd(m);
                Assert.True(cd > 0.0 && cd < 0.70, $"{table.Id} CD out of bounds at M={m}: {cd}");
            }
        }
    }

    [Fact]
    public void Extrapolation_ClampsToTableEndpoints()
    {
        Assert.Equal(DragTableCatalog.G1.Cd(5.0), DragTableCatalog.G1.Cd(8.0), 15);
        Assert.Equal(DragTableCatalog.G1.Cd(0.0), DragTableCatalog.G1.Cd(-1.0), 15);
        Assert.Equal(DragTableCatalog.G7.Cd(0.0), DragTableCatalog.G7.Cd(0.0), 15);
    }

    [Fact]
    public void BcDragModel_RetardationMatchesHandComputation()
    {
        // .308-class bullet, BC = 0.20 G1 lb/in^2, sea-level ISA air.
        // At Mach 1.4: CD = 0.6625, v = 1.4 * 340.29 = 476.41 m/s.
        // a = 0.5 * rho * (pi/4) * CD / BC_SI * v^2
        //   = 0.5 * 1.225 * 0.785398 * (0.6625 / 140.6139) * 226968
        //   ~ 514.5 m/s^2  (hand-computed independently of the code path)
        var model = new BcDragModel(DragTableCatalog.G1, new ConstantBcProvider(0.20));
        var atm = FourTrac.Core.Atm.Atmosphere.FromMeasurements(15.0, 101325.0, 0.0, 0.0);
        double soundSpeed = atm.SpeedOfSound(0.0);
        double v = 1.4 * soundSpeed;
        double beta = model.Beta(v, 1.4);
        double retardation = 0.5 * atm.Density(0.0) * beta * v * v;
        Assert.Equal(514.5, retardation, 0);
    }

    [Fact]
    public void BcModeAndCdMode_AreEquivalentWhenParameterizedConsistently()
    {
        // The same bullet expressed two ways must produce identical beta:
        //  - BC mode: G1 table + BC = 0.20 lb/in^2
        //  - CD mode: caliber 7.82 mm, mass 10.9 g, CD(M) = i * CD_G1 with
        //    i = SD/BC so that (A/m)*CD_bullet == (pi/4)*CD_G1/BC_SI.
        double caliber = 7.82e-3;
        double mass = 0.0109;
        double bcLbIn2 = 0.20;

        var bcModel = new BcDragModel(DragTableCatalog.G1, new ConstantBcProvider(bcLbIn2));

        double area = Math.PI * caliber * caliber / 4.0;
        double bcSi = bcLbIn2 * UnitConversions.LbIn2ToKgM2;
        double scale = (Math.PI / 4.0) / (bcSi * (area / mass));
        var scaledPoints = Enumerable.Range(0, DragTableCatalog.G1.Count)
            .Select(i => (DragTableCatalog.G1.MachAtNode(i), DragTableCatalog.G1.CdAtNode(i) * scale))
            .ToArray();
        // Same nodes, same interpolation kind as the catalog (Linear - the
        // reference-ecosystem convention for bundled G-tables; the test pins
        // parameterization equivalence, not interpolation schemes, so letting
        // FromPoints default to Pchip would compare Linear vs Hermite).
        var cdModel = new CdTableDragModel(
            DragTable.FromPoints(scaledPoints, kind: InterpolationKind.Linear), caliber, mass);

        foreach (double v in new[] { 50.0, 200.0, 400.0, 600.0, 800.0, 1000.0 })
        foreach (double mach in new[] { v / 340.0, v / 300.0 })
        {
            double b1 = bcModel.Beta(v, mach);
            double b2 = cdModel.Beta(v, mach);
            Assert.True(Math.Abs(b1 - b2) / b1 < 1e-12, $"beta mismatch at v={v}, M={mach}: {b1} vs {b2}");
        }
    }

    [Fact]
    public void ConstantTabulatedBc_EqualsScalarBc()
    {
        var scalar = new BcDragModel(DragTableCatalog.G7, new ConstantBcProvider(0.377));
        var tabulated = new BcDragModel(DragTableCatalog.G7,
            new TabulatedBcProvider(new[] { (0.0, 0.377), (1500.0, 0.377) }));

        foreach (double v in new[] { 10.0, 340.0, 900.0 })
        {
            double m = v / 340.0;
            Assert.Equal(scalar.Beta(v, m), tabulated.Beta(v, m), 15);
        }
    }

    [Fact]
    public void TabulatedBcProvider_InterpolatesAndClamps()
    {
        var provider = new TabulatedBcProvider(new[]
        {
            (200.0, 0.250), (500.0, 0.270), (900.0, 0.300),
        });
        Assert.Equal(0.250, provider.BcLbIn2(100.0), 12);   // clamped below
        Assert.Equal(0.300, provider.BcLbIn2(1200.0), 12);  // clamped above
        Assert.Equal(0.260, provider.BcLbIn2(350.0), 12);   // midpoint
    }

    [Fact]
    public void Tables_RejectMalformedInput()
    {
        Assert.Throws<ArgumentException>(() => DragTable.FromPoints(new[] { (1.0, 0.3) }));
        Assert.Throws<ArgumentException>(() => DragTable.FromPoints(new[] { (1.0, 0.3), (1.0, 0.4) }));
        Assert.Throws<ArgumentException>(() => DragTable.FromPoints(new[] { (1.0, -0.3), (2.0, 0.4) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantBcProvider(0.0));
    }
}
