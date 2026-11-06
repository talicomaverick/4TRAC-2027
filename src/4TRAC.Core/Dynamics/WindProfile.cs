using FourTrac.Core.Geo;

namespace FourTrac.Core.Dynamics;

/// <summary>
/// Layered wind profile: speed and FROM-direction (meteorological convention,
/// degrees clockwise from true north) as a function of height above ground.
/// Linear interpolation between layers; constant below the first and above the
/// last layer. The bullet flies tens of metres above the roof - a single
/// surface wind is a known error source, so the profile exists from day one.
/// </summary>
public sealed class WindProfile
{
    public readonly record struct Layer(double HeightAglM, double SpeedMps, double FromDirectionDeg);

    private readonly Layer[] _layers;

    public WindProfile(IEnumerable<Layer> layers)
    {
        var sorted = layers.OrderBy(l => l.HeightAglM).ToArray();
        if (sorted.Length == 0)
            throw new ArgumentException("Wind profile needs at least one layer.", nameof(layers));
        foreach (var l in sorted)
        {
            if (l.SpeedMps < 0) throw new ArgumentOutOfRangeException(nameof(layers), "Wind speed must be >= 0.");
            if (l.FromDirectionDeg < 0 || l.FromDirectionDeg >= 360)
                throw new ArgumentOutOfRangeException(nameof(layers), "Wind direction must be in [0, 360).");
        }
        _layers = sorted;
    }

    public static WindProfile Calm { get; } = new(new[] { new Layer(0.0, 0.0, 0.0) });

    public static WindProfile Constant(double speedMps, double fromDirectionDeg) =>
        new(new[] { new Layer(0.0, speedMps, fromDirectionDeg) });

    /// <summary>
    /// Wind vector in the local ENU frame (m/s): points in the direction the
    /// air is *moving toward*. A wind FROM azimuth theta blows toward theta+180.
    /// </summary>
    public Cartesian3 WindVectorEnu(double heightAglM)
    {
        double speed, fromDeg;
        if (heightAglM <= _layers[0].HeightAglM)
        {
            speed = _layers[0].SpeedMps;
            fromDeg = _layers[0].FromDirectionDeg;
        }
        else if (heightAglM >= _layers[^1].HeightAglM)
        {
            speed = _layers[^1].SpeedMps;
            fromDeg = _layers[^1].FromDirectionDeg;
        }
        else
        {
            int lo = 0, hi = _layers.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_layers[mid].HeightAglM <= heightAglM) lo = mid;
                else hi = mid;
            }
            double t = (heightAglM - _layers[lo].HeightAglM)
                / (_layers[hi].HeightAglM - _layers[lo].HeightAglM);
            speed = _layers[lo].SpeedMps + t * (_layers[hi].SpeedMps - _layers[lo].SpeedMps);
            // Interpolate the unit vector, then rescale - avoids the 0/360 wrap trap.
            var vLo = UnitToward(_layers[lo].FromDirectionDeg) * _layers[lo].SpeedMps;
            var vHi = UnitToward(_layers[hi].FromDirectionDeg) * _layers[hi].SpeedMps;
            var interp = vLo + (vHi - vLo) * t;
            double mag = interp.Length;
            return mag < 1e-12 ? Cartesian3.Zero : interp;
        }

        return UnitToward(fromDeg) * speed;
    }

    /// <summary>
    /// Wind vector (m/s) in ECEF. The ENU-to-ECEF rotation is evaluated once at
    /// the reference frame origin and held fixed: over forensic ranges (<= a few
    /// km) the frame rotation along the path contributes &lt; 0.1 % of the wind
    /// vector - documented approximation.
    /// </summary>
    public Cartesian3 WindVectorEcef(double heightAglM, EnuFrame sceneFrame) =>
        sceneFrame.VectorToEcef(WindVectorEnu(heightAglM));

    private static Cartesian3 UnitToward(double fromDirectionDeg)
    {
        double rad = (fromDirectionDeg + 180.0) * Math.PI / 180.0;
        return new Cartesian3(Math.Sin(rad), Math.Cos(rad), 0.0);
    }
}
