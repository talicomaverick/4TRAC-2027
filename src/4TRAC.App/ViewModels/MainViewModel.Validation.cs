using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;

namespace FourTrac.App.ViewModels;

/// <summary>
/// Per-field input validation. Each input property has a rule that returns an error
/// message (or null when valid). Errors are exposed through INotifyDataErrorInfo so
/// WPF outlines the offending box in red and shows the message as a tooltip, and the
/// full list is exposed as <see cref="ErrorLines"/> for the panel above the Solve button.
/// </summary>
public partial class MainViewModel : INotifyDataErrorInfo
{
    private readonly Dictionary<string, string> _errors = new();
    private Dictionary<string, (string Label, Func<string?> Rule)> _rules = null!;

    private IReadOnlyList<string> _errorLines = Array.Empty<string>();
    public IReadOnlyList<string> ErrorLines { get => _errorLines; private set => SetProperty(ref _errorLines, value); }

    public bool HasErrors => _errors.Count > 0;
    public bool HasNoErrors => _errors.Count == 0;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (propertyName is not null && _errors.TryGetValue(propertyName, out var msg))
            return new[] { msg };
        return Array.Empty<string>();
    }

    /// <summary>Parses a number accepting either the current culture or invariant ('.') notation.</summary>
    internal static bool TryParseNumber(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value)) return true;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private void InitializeValidation()
    {
        string? Range(string s, double min, double max, string unit = "", bool minExclusive = false, bool maxExclusive = false)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Required. Enter a number.";
            if (!TryParseNumber(s, out double v)) return $"'{s.Trim()}' is not a number.";
            bool lowOk = minExclusive ? v > min : v >= min;
            bool highOk = maxExclusive ? v < max : v <= max;
            if (lowOk && highOk) return null;
            string lo = minExclusive ? $"more than {min:G}" : $"at least {min:G}";
            string hi = maxExclusive ? $"less than {max:G}" : $"at most {max:G}";
            return $"Must be {lo} and {hi}{(unit.Length > 0 ? " " + unit : "")}.";
        }

        string? SpinRange(string s, double min, double max, string unit) =>
            SpinDriftOn ? Range(s, min, max, unit) : null;

        _rules = new()
        {
            [nameof(ImpactLat)] = ("Impact latitude", () => Range(ImpactLat, -90, 90, "degrees")),
            [nameof(ImpactLon)] = ("Impact longitude", () => Range(ImpactLon, -180, 180, "degrees")),
            [nameof(ImpactElevation)] = ("Impact elevation", () => Range(ImpactElevation, -500, 9000, "m")),
            [nameof(OriginHeight)] = ("Origin plane height", () => Range(OriginHeight, -5000, 5000, "m")),
            [nameof(IncidenceAngle)] = ("Angle of incidence", () => Range(IncidenceAngle, 0, 90, "degrees below horizontal", true, true)),
            [nameof(IncidenceAngleSigma)] = ("Angle of incidence sigma", () => Range(IncidenceAngleSigma, 0, 45, "degrees")),
            [nameof(Azimuth)] = ("Travel azimuth", () => Range(Azimuth, 0, 360, "degrees true")),
            [nameof(AzimuthSigma)] = ("Azimuth sigma", () => Range(AzimuthSigma, 0, 90, "degrees")),
            [nameof(BallisticCoefficient)] = ("Ballistic coefficient", () => Range(BallisticCoefficient, 0, 2, "lb/in2", true)),
            [nameof(BcSigmaPercent)] = ("BC sigma", () => Range(BcSigmaPercent, 0, 50, "%")),
            [nameof(DiameterIn)] = ("Bullet diameter", () => SpinRange(DiameterIn, 0.1, 1.0, "in")),
            [nameof(MassGr)] = ("Bullet mass", () => SpinRange(MassGr, 1, 1000, "grains")),
            [nameof(TwistIn)] = ("Twist rate", () => SpinRange(TwistIn, 1, 50, "in per turn")),
            [nameof(LengthIn)] = ("Bullet length", () => SpinRange(LengthIn, 0.1, 5, "in")),
            [nameof(MvMinFps)] = ("MV minimum", () => Range(MvMinFps, 100, 5000, "fps")),
            [nameof(MvMaxFps)] = ("MV maximum", () =>
            {
                string? r = Range(MvMaxFps, 100, 5000, "fps");
                if (r is not null) return r;
                if (TryParseNumber(MvMinFps, out double lo) && TryParseNumber(MvMaxFps, out double hi) && hi <= lo)
                    return "Must be greater than the MV minimum.";
                return null;
            }),
            [nameof(TemperatureF)] = ("Temperature", () => Range(TemperatureF, -100, 150, "F")),
            [nameof(PressureInHg)] = ("Pressure", () => Range(PressureInHg, 10, 35, "inHg")),
            [nameof(RelativeHumidity)] = ("Relative humidity", () => Range(RelativeHumidity, 0, 100, "%")),
            [nameof(WindSpeedMph)] = ("Wind speed", () => Range(WindSpeedMph, 0, 200, "mph")),
            [nameof(WindFromDeg)] = ("Wind direction", () => Range(WindFromDeg, 0, 360, "degrees true (direction wind blows from)")),
            [nameof(WindSigma)] = ("Wind sigma", () => Range(WindSigma, 0, 20, "m/s")),
            [nameof(Hypotheses)] = ("Hypotheses", () =>
            {
                if (string.IsNullOrWhiteSpace(Hypotheses)) return "Required. Enter a whole number.";
                if (!int.TryParse(Hypotheses.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    return $"'{Hypotheses.Trim()}' is not a whole number.";
                return n is >= 500 and <= 100_000
                    ? null
                    : "Must be between 500 and 100000 (500 is the calibrated minimum).";
            }),
            [nameof(Seed)] = ("RNG seed", () =>
                ulong.TryParse(Seed?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    ? null
                    : "Must be a whole number, 0 or more."),
            [nameof(DemPath)] = ("DEM file", () =>
            {
                if (string.IsNullOrWhiteSpace(DemPath)) return null;
                return File.Exists(DemPath.Trim()) ? null : "File not found.";
            }),
        };

        PropertyChanged += (_, e) =>
        {
            string? name = e.PropertyName;
            if (name is null) return;
            if (_rules.ContainsKey(name))
            {
                Validate(name);
                // Cross-field and conditional dependencies.
                if (name == nameof(MvMinFps)) Validate(nameof(MvMaxFps));
                if (name == nameof(MvMaxFps)) Validate(nameof(MvMinFps));
            }
            else if (name == nameof(SpinDriftOn))
            {
                foreach (var n in new[] { nameof(DiameterIn), nameof(MassGr), nameof(TwistIn), nameof(LengthIn) })
                    Validate(n);
            }
        };

        foreach (var key in _rules.Keys) Validate(key);
    }

    private void Validate(string property)
    {
        if (!_rules.TryGetValue(property, out var rule)) return;
        string? msg = rule.Rule();
        bool had = _errors.ContainsKey(property);

        if (msg is null)
        {
            if (!had) return;
            _errors.Remove(property);
        }
        else
        {
            if (had && _errors[property] == msg) return;
            _errors[property] = msg;
        }

        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
        ErrorLines = _rules
            .Where(r => _errors.ContainsKey(r.Key))
            .Select(r => $"{r.Value.Label}: {_errors[r.Key]}")
            .ToList();
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasNoErrors));
        CommandManager.InvalidateRequerySuggested();
    }
}
