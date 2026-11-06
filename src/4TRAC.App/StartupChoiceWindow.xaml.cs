using System.Windows;

namespace FourTrac.App;

/// <summary>Startup prompt over the 4TRAC splash image: restore the last session or start fresh.</summary>
public partial class StartupChoiceWindow : Window
{
    /// <summary>True when the user chose to restore the previous session.</summary>
    public bool UsePreviousSession { get; private set; }

    public StartupChoiceWindow() => InitializeComponent();

    private void UsePrevious_Click(object sender, RoutedEventArgs e) { UsePreviousSession = true; DialogResult = true; }
    private void StartFresh_Click(object sender, RoutedEventArgs e) { UsePreviousSession = false; DialogResult = true; }
}
