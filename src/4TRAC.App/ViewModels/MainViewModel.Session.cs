using System.IO;
using System.Text.Json;

namespace FourTrac.App.ViewModels;

/// <summary>Saves every user input on close and restores it on request at the next start.</summary>
public partial class MainViewModel
{
    private static readonly string[] SessionTextFields =
    {
        nameof(ImpactLat), nameof(ImpactLon), nameof(ImpactElevation), nameof(OriginHeight),
        nameof(IncidenceAngle), nameof(IncidenceAngleSigma), nameof(Azimuth), nameof(AzimuthSigma),
        nameof(BallisticCoefficient), nameof(BcSigmaPercent), nameof(ReferenceStandard),
        nameof(DiameterIn), nameof(MassGr), nameof(TwistIn), nameof(LengthIn),
        nameof(MvMinFps), nameof(MvMaxFps),
        nameof(TemperatureF), nameof(PressureInHg), nameof(RelativeHumidity),
        nameof(WindSpeedMph), nameof(WindFromDeg), nameof(WindSigma),
        nameof(Hypotheses), nameof(Seed), nameof(DemPath),
    };

    private static readonly string[] SessionBoolFields =
    {
        nameof(SpinDriftOn), nameof(RightHandTwist), nameof(CoriolisOn), nameof(UseSrtm),
    };

    public static string SessionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "4TRAC", "session.json");

    public static bool SessionExists => File.Exists(SessionFilePath);

    public void SaveSession()
    {
        try
        {
            var data = new Dictionary<string, object?>();
            var t = typeof(MainViewModel);
            foreach (var n in SessionTextFields) data[n] = (string?)t.GetProperty(n)!.GetValue(this);
            foreach (var n in SessionBoolFields) data[n] = (bool)t.GetProperty(n)!.GetValue(this)!;
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
            foreach (var n in SessionTextFields)
                if (doc.RootElement.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String)
                    t.GetProperty(n)!.SetValue(this, e.GetString());
            foreach (var n in SessionBoolFields)
                if (doc.RootElement.TryGetProperty(n, out var e) &&
                    (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
                    t.GetProperty(n)!.SetValue(this, e.GetBoolean());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
