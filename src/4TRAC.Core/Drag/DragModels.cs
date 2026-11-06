using FourTrac.Core.Geo;

namespace FourTrac.Core.Drag;

/// <summary>
/// Drag models all expose a "ballistic factor" beta with units m^2/kg such that
/// the deceleration (per unit mass, relative to the air) is exactly
///     a = -0.5 * rho * beta(|v_air|, Mach_air) * |v_air| * v_air.
/// This single scaling convention makes the three input modes (scalar G-BC,
/// velocity-dependent BC, full CD table) interchangeable and unit-safe.
/// </summary>
public interface IDragModel
{
    /// <summary>Beta [m^2/kg] at air-relative speed and Mach number.</summary>
    double Beta(double airRelativeSpeedMps, double mach);
}

/// <summary>Constant ballistic coefficient provider (scalar published BC).</summary>
public interface IBcProvider
{
    /// <summary>G-style ballistic coefficient in lb/in^2 at the given air-relative speed.</summary>
    double BcLbIn2(double airRelativeSpeedMps);
}

public sealed class ConstantBcProvider : IBcProvider
{
    private readonly double _bcLbIn2;

    public ConstantBcProvider(double bcLbIn2)
    {
        if (bcLbIn2 <= 0) throw new ArgumentOutOfRangeException(nameof(bcLbIn2));
        _bcLbIn2 = bcLbIn2;
    }

    public double BcLbIn2(double airRelativeSpeedMps) => _bcLbIn2;
}

/// <summary>
/// Velocity-dependent BC (Pejsa/Litz style: BC calibrated at two or more
/// velocities). Constant extrapolation outside the tabulated span.
/// BC(v) is interpreted as the bullet's G-style BC evaluated at the current
/// air-relative speed; the reference drag curve supplies the Mach-dependent
/// shape, BC(v) supplies the scaling - the standard "BC vs velocity" convention.
/// </summary>
public sealed class TabulatedBcProvider : IBcProvider
{
    private readonly double[] _speed;
    private readonly double[] _bc;

    public TabulatedBcProvider(IReadOnlyList<(double SpeedMps, double BcLbIn2)> points)
    {
        if (points.Count < 2)
            throw new ArgumentException("BC(v) needs at least two points.", nameof(points));
        var sorted = points.OrderBy(p => p.SpeedMps).ToArray();
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].SpeedMps - sorted[i - 1].SpeedMps < 1e-12)
                throw new ArgumentException("Duplicate speed in BC(v) table.");
            if (sorted[i].BcLbIn2 <= 0 || sorted[i - 1].BcLbIn2 <= 0)
                throw new ArgumentException("BC values must be positive.");
        }
        _speed = sorted.Select(p => p.SpeedMps).ToArray();
        _bc = sorted.Select(p => p.BcLbIn2).ToArray();
    }

    public double BcLbIn2(double airRelativeSpeedMps)
    {
        if (airRelativeSpeedMps <= _speed[0]) return _bc[0];
        if (airRelativeSpeedMps >= _speed[^1]) return _bc[^1];

        int lo = 0, hi = _speed.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_speed[mid] <= airRelativeSpeedMps) lo = mid;
            else hi = mid;
        }
        double t = (airRelativeSpeedMps - _speed[lo]) / (_speed[hi] - _speed[lo]);
        return _bc[lo] + t * (_bc[hi] - _bc[lo]);
    }
}

/// <summary>
/// G-model drag: beta = (pi/4) * CD_ref(Mach) / BC_SI.
/// This follows from BC = m/(i*d^2), i = CD_bullet/CD_ref, A = pi*d^2/4:
///     (A/m)*CD_bullet = (pi/4)*CD_ref/BC.
/// BC is either scalar or velocity-dependent via <see cref="IBcProvider"/>.
/// </summary>
public sealed class BcDragModel : IDragModel
{
    private readonly DragTable _reference;
    private readonly IBcProvider _bc;

    public BcDragModel(DragTable reference, IBcProvider bc)
    {
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        _bc = bc ?? throw new ArgumentNullException(nameof(bc));
    }

    public DragTable ReferenceTable => _reference;
    public IBcProvider BcProvider => _bc;

    public double Beta(double airRelativeSpeedMps, double mach) =>
        (Math.PI / 4.0) * _reference.Cd(mach) / (_bc.BcLbIn2(airRelativeSpeedMps) * UnitConversions.LbIn2ToKgM2);
}

/// <summary>
/// Full drag-curve mode: the user supplies the bullet's own CD(Mach) table plus
/// physical caliber and mass; beta = (A/m) * CD(Mach). No BC involved.
/// </summary>
public sealed class CdTableDragModel : IDragModel
{
    private readonly DragTable _cd;
    private readonly double _areaOverMass; // m^2/kg

    public CdTableDragModel(DragTable cd, double caliberM, double massKg)
    {
        if (caliberM <= 0) throw new ArgumentOutOfRangeException(nameof(caliberM));
        if (massKg <= 0) throw new ArgumentOutOfRangeException(nameof(massKg));
        _cd = cd ?? throw new ArgumentNullException(nameof(cd));
        double area = Math.PI * caliberM * caliberM / 4.0;
        _areaOverMass = area / massKg;
    }

    public DragTable CdTable => _cd;
    public double AreaOverMass => _areaOverMass;

    public double Beta(double airRelativeSpeedMps, double mach) => _areaOverMass * _cd.Cd(mach);
}
