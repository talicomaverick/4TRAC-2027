using System.Globalization;
using System.IO;
using System.Windows.Input;
using FourTrac.Core.Library;

namespace FourTrac.App.ViewModels;

public partial class MainViewModel
{
    // --- Bullet profile library ---

    private string? _selectedProfile;
    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value is not null)
                ApplyProfile(value);
        }
    }

    public IReadOnlyList<string> ProfileNames { get; private set; } = Array.Empty<string>();

    public ICommand RecallProfileCommand { get; set; } = new RelayCommand(_ => { });

    private void InitializeProfiles()
    {
        var profilesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "profiles");
        var absDir = Path.GetFullPath(profilesDir);
        try
        {
            BulletProfileLibrary.LoadDirectory(absDir);
            ProfileNames = BulletProfileLibrary.Profiles.Select(p => p.Name).ToList();
        }
        catch
        {
            // Profiles are optional; degrade gracefully if the directory is missing.
            ProfileNames = Array.Empty<string>();
        }

        RecallProfileCommand = new RelayCommand(_ =>
        {
            if (SelectedProfile is not null)
                ApplyProfile(SelectedProfile);
        });
    }

    private void ApplyProfile(string name)
    {
        var profile = BulletProfileLibrary.FindByName(name);
        if (profile is null) return;

        BallisticCoefficient = profile.BcLbIn2.ToString("F4", CultureInfo.InvariantCulture);
        BcSigmaPercent = (profile.BcSigmaFraction * 100.0).ToString("F1", CultureInfo.InvariantCulture);
        ReferenceStandard = profile.ReferenceStandard;
        DiameterIn = profile.DiameterIn.ToString("F3", CultureInfo.InvariantCulture);
        MassGr = profile.MassGr.ToString("F0", CultureInfo.InvariantCulture);
        TwistIn = profile.TwistIn.ToString("F1", CultureInfo.InvariantCulture);
        LengthIn = profile.LengthIn.ToString("F3", CultureInfo.InvariantCulture);
        RightHandTwist = profile.RightHandTwist;

        ResultsText += $"\n\nProfile recalled: {profile.Name}\n{profile.Notes ?? string.Empty}";
    }
}
