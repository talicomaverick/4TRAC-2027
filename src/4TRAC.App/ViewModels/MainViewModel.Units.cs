using System.Globalization;

namespace FourTrac.App.ViewModels;

/// <summary>
/// Imperial / metric display units. The form holds text in whichever system is selected;
/// BuildRequest converts to SI for the solver, so results do not depend on the setting.
/// Elevation, origin height, ranges and areas are metres in both systems. Ballistic
/// coefficient is dimensionless, and bullet mass stays in grains in both systems.
/// </summary>
public partial class MainViewModel
{
    internal const double MmPerIn = 25.4;
    internal const double PaPerInHg = 3386.39;

    private bool _isMetric;
    private bool _resultMetric;
    private bool _resultEstimatedBc;
    private string? _estimatedBcText;   // BC text last filled from an estimate row; null if not an estimate

    public bool IsMetric
    {
        get => _isMetric;
        set
        {
            if (_isMetric == value) return;
            _isMetric = value;
            ConvertFormValues(toMetric: value);
            OnPropertyChanged(nameof(IsMetric));
            OnPropertyChanged(nameof(IsImperial));
            RaiseUnitLabels();
            RevalidateAll();
        }
    }

    public bool IsImperial
    {
        get => !_isMetric;
        set { if (value) IsMetric = false; }
    }

    /// <summary>Switches the unit system without converting the typed values (used when restoring a saved session).</summary>
    internal void SetUnitSystemWithoutConversion(bool metric)
    {
        if (_isMetric == metric) return;
        _isMetric = metric;
        OnPropertyChanged(nameof(IsMetric));
        OnPropertyChanged(nameof(IsImperial));
        RaiseUnitLabels();
        RevalidateAll();
    }

    public string MvHeader => $"Muzzle velocity range ({(IsMetric ? "m/s" : "fps")})";
    public string BcLabel => "Ballistic coefficient (dimensionless)  |  sigma (%)";
    public string DiameterMassLabel => $"Diameter ({(IsMetric ? "mm" : "in")})  |  mass (gr)";
    public string TwistLengthLabel => IsMetric
        ? "Barrel twist (mm per turn)  |  bullet length (mm)"
        : "Barrel twist (in per turn)  |  bullet length (in)";
    public string WeatherLabel => IsMetric
        ? "Temperature (deg C)  |  station pressure (hPa)  |  humidity (%)"
        : "Temperature (deg F)  |  station pressure (inHg)  |  humidity (%)";
    public string WindLabel => $"Wind (constant): speed ({(IsMetric ? "m/s" : "mph")})  |  FROM direction (deg true)  |  sigma per axis (m/s)";
    public string CrosswindLabel => $"Crosswind ({(IsMetric ? "m/s" : "mph")}), applied from the LEFT and the RIGHT  |  sigma per axis (m/s)";

    public string WindAloftLabel => $"Wind aloft: speed ({(IsMetric ? "m/s" : "mph")})  |  FROM (deg true)  |  from height above impact (m; 0 = off)";

    private void RaiseUnitLabels()
    {
        foreach (var n in new[] { nameof(MvHeader), nameof(BcLabel), nameof(DiameterMassLabel),
                                  nameof(TwistLengthLabel), nameof(WeatherLabel), nameof(WindLabel), nameof(CrosswindLabel), nameof(WindAloftLabel) })
            OnPropertyChanged(n);
    }

    private void ConvertFormValues(bool toMetric)
    {
        // (imperial -> metric) factors; the inverse is used when switching back.
        string Conv(string s, Func<double, double> toM, Func<double, double> toI, int decimals)
        {
            if (!TryParseNumber(s, out double v)) return s;   // leave invalid text for the user to fix
            double r = toMetric ? toM(v) : toI(v);
            return Math.Round(r, decimals).ToString("0.###############", CultureInfo.InvariantCulture);
        }

        int mvDec = toMetric ? 1 : 0;
        MvMinFps = Conv(MvMinFps, v => v * 0.3048, v => v / 0.3048, mvDec);
        MvMaxFps = Conv(MvMaxFps, v => v * 0.3048, v => v / 0.3048, mvDec);
        TemperatureF = Conv(TemperatureF, v => (v - 32.0) * 5.0 / 9.0, v => v * 9.0 / 5.0 + 32.0, 1);
        PressureInHg = Conv(PressureInHg, v => v * PaPerInHg / 100.0, v => v * 100.0 / PaPerInHg, toMetric ? 1 : 2);
        WindSpeedMph = Conv(WindSpeedMph, v => v * 0.44704, v => v / 0.44704, toMetric ? 2 : 1);
        Crosswind = Conv(Crosswind, v => v * 0.44704, v => v / 0.44704, toMetric ? 2 : 1);
        WindAloftSpeed = Conv(WindAloftSpeed, v => v * 0.44704, v => v / 0.44704, toMetric ? 2 : 1);
        DiameterIn = Conv(DiameterIn, v => v * MmPerIn, v => v / MmPerIn, 3);
        TwistIn = Conv(TwistIn, v => v * MmPerIn, v => v / MmPerIn, toMetric ? 1 : 2);
        LengthIn = Conv(LengthIn, v => v * MmPerIn, v => v / MmPerIn, 3);
    }

    // Values for the solver, always SI / inches (the Core spin model is in inches).
    private double MvToMps(double v) => IsMetric ? v : v * 0.3048;
    private double TempToC(double v) => IsMetric ? v : (v - 32.0) * 5.0 / 9.0;
    private double PressureToPa(double v) => IsMetric ? v * 100.0 : v * PaPerInHg;
    private double WindToMps(double v) => IsMetric ? v : v * 0.44704;
    private double LenToIn(double v) => IsMetric ? v / MmPerIn : v;
}
