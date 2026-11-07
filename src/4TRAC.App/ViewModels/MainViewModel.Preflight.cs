using System.ComponentModel;

namespace FourTrac.App.ViewModels;

/// <summary>
/// Warnings shown above the Solve button, from the inputs alone, before anything is solved.
/// They mirror the warnings the analysis adds to its result, so the user sees them in time.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Impact angle (deg) from which the steep-impact warnings apply (matches FlatAnalysis).</summary>
    private const double SteepAngleDeg = 50.0;

    private IReadOnlyList<string> _preSolveWarnings = Array.Empty<string>();
    public IReadOnlyList<string> PreSolveWarnings
    {
        get => _preSolveWarnings;
        private set
        {
            if (SetProperty(ref _preSolveWarnings, value))
                OnPropertyChanged(nameof(HasPreSolveWarnings));
        }
    }

    public bool HasPreSolveWarnings => _preSolveWarnings.Count > 0;

    private void InitializePreflight()
    {
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(IncidenceAngle):
                case nameof(SpinDriftOn):
                case nameof(OriginHeight):
                case nameof(LaunchBranchIndex):
                case nameof(DemPath):
                case nameof(UseSrtm):
                case nameof(WindSpeedMph):
                case nameof(Crosswind):
                case nameof(WindSigma):
                case nameof(WindModeIndex):
                    RefreshPreSolveWarnings();
                    break;
            }
        };
        RefreshPreSolveWarnings();
    }

    private void RefreshPreSolveWarnings()
    {
        var lines = new List<string>();

        bool haveAngle = TryParseNumber(IncidenceAngle, out double angle);
        bool steep = haveAngle && angle >= SteepAngleDeg;
        if (steep)
        {
            lines.Add($"Steep impact ({angle:F0}°): near the end of a bullet's range the origin range along the bearing is poorly constrained; a few degrees of angle error or a different BC moves it by hundreds of metres. The bearing stays reliable. Read the result as a line along the bearing.");
            if (SpinDriftOn)
                lines.Add("Spin drift is on at a steep angle: the spin-drift formula is for flat fire and is extrapolated here; it can shift the bearing by several degrees. Consider switching spin drift off and comparing both results.");
        }

        bool haveHeight = TryParseNumber(OriginHeight, out double height);
        bool terrain = !string.IsNullOrWhiteSpace(DemPath) || UseSrtm;
        if (haveHeight && !terrain)
        {
            if (height > 0 && LaunchBranchIndex == 0)
                lines.Add("The origin plane is above the impact: only a climbing (lofted) launch will be searched. Set Launch search to Both to also look for a shooter firing down from that height.");
            else if (height <= 0 && LaunchBranchIndex is 1 or 2)
                lines.Add("The descending search needs the origin height above 0 (a shooter higher than the impact).");
        }

        bool haveSigma = TryParseNumber(WindSigma, out double windSigma);
        bool haveWind = WindIsCrosswind ? TryParseNumber(Crosswind, out double wv0) && wv0 == 0 : TryParseNumber(WindSpeedMph, out double wv1) && wv1 == 0;
        if (haveWind && haveSigma && windSigma <= 1.0)
            lines.Add("Wind is set to zero with a small sigma. If the wind on the day is not known, widen the wind sigma (for example 3 to 5 m/s) or enter a crosswind: an unknown wind otherwise makes the zone look more certain than it is.");

        PreSolveWarnings = lines;
    }
}
