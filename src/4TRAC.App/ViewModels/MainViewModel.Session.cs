using System.IO;
using System.Text.Json;

namespace FourTrac.App.ViewModels;

/// <summary>Saves every user input on close and restores it on request at the next start.</summary>
public partial class MainViewModel
{
    private static readonly string[] SessionTextFields =
    {
        nameof(ImpactLat), nameof(ImpactLon), nameof(ImpactElevation), nameof(OriginHeight),
        nameof(IncidenceAngle), nameof(IncidenceAngleSigma), nameof(Azimuth), nameof(AzimuthSigma), nameof(Declination),
        nameof(BallisticCoefficient), nameof(BcSigmaPercent), nameof(ReferenceStandard),
        nameof(DiameterIn), nameof(MassGr), nameof(TwistIn), nameof(LengthIn),
        nameof(MvMinFps), nameof(MvMaxFps),
        nameof(TemperatureF), nameof(PressureInHg), nameof(RelativeHumidity),
        nameof(WindSpeedMph), nameof(WindFromDeg), nameof(WindAloftSpeed), nameof(WindAloftFromDeg), nameof(WindAloftHeight), nameof(WindSigma), nameof(Crosswind),
        nameof(Hypotheses), nameof(Seed), nameof(DemPath),
    };

    private static readonly string[] SessionBoolFields =
    {
        nameof(SpinDriftOn), nameof(RightHandTwist), nameof(CoriolisOn), nameof(UseSrtm),
    };

    public static string SessionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "4TRAC", "session.json");

    public static bool SessionExists => File.Exists(SessionFilePath);

    /// <summary>
    /// "Start fresh": blanks every case input. Weather (atmosphere and wind) and the run settings
    /// (hypotheses, seed, option checkboxes) keep their defaults.
    /// </summary>
    public void ClearCaseInputs()
    {
        ImpactLat = ImpactLon = ImpactElevation = OriginHeight = "";
        IncidenceAngle = IncidenceAngleSigma = Azimuth = AzimuthSigma = "";
        BallisticCoefficient = BcSigmaPercent = "";
        DiameterIn = MassGr = TwistIn = LengthIn = "";
        MvMinFps = MvMaxFps = "";
        DemPath = "";
        ClearCalibreSelection();
        ResultsText = "Enter the case data in the fields on the left, then press Solve.\n" +
                      "Model: flat-earth point mass, 10 km validated envelope, gravity 9.80665 m/s^2.";
    }

    public void SaveSession()
    {
        try
        {
            var data = new Dictionary<string, object?>();
            var t = typeof(MainViewModel);
            foreach (var n in SessionTextFields) data[n] = (string?)t.GetProperty(n)!.GetValue(this);
            foreach (var n in SessionBoolFields) data[n] = (bool)t.GetProperty(n)!.GetValue(this)!;
            data["IsMetric"] = IsMetric;
            data["LaunchBranchIndex"] = LaunchBranchIndex;
            data["WindModeIndex"] = WindModeIndex;
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);
            File.WriteAllText(SessionFilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Failing to save the session must never block closing the program.
        }
    }

    /// <summary>Restores saved inputs. Returns false if nothing usable was found; unknown or bad entries are skipped.</summary>
    public bool LoadSession()
    {
        try
        {
            if (!SessionExists) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(SessionFilePath));
            var t = typeof(MainViewModel);
            // Unit system first and without conversion: the saved text is already in that system.
            if (doc.RootElement.TryGetProperty("IsMetric", out var um) &&
                (um.ValueKind == JsonValueKind.True || um.ValueKind == JsonValueKind.False))
                SetUnitSystemWithoutConversion(um.GetBoolean());
            foreach (var n in SessionTextFields)
                if (doc.RootElement.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String)
                    t.GetProperty(n)!.SetValue(this, e.GetString());
            foreach (var n in SessionBoolFields)
                if (doc.RootElement.TryGetProperty(n, out var e) &&
                    (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
                    t.GetProperty(n)!.SetValue(this, e.GetBoolean());
            // Launch search and wind input mode (integers; the setters ignore out-of-range values).
            if (doc.RootElement.TryGetProperty("LaunchBranchIndex", out var lb) && lb.ValueKind == JsonValueKind.Number && lb.TryGetInt32(out int lbi))
                LaunchBranchIndex = lbi;
            if (doc.RootElement.TryGetProperty("WindModeIndex", out var wm) && wm.ValueKind == JsonValueKind.Number && wm.TryGetInt32(out int wmi))
                WindModeIndex = wmi;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
