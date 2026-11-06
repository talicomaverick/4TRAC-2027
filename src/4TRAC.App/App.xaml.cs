using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace FourTrac.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Software rendering: hardware WPF rendering failed silently (blank window)
        // on the development machine. The map is raster tiles, so the cost is small.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // WPF binds XAML using the default xml:lang (en-US). On some Windows
        // locales that lookup fails ("Cannot find non-neutral culture related to
        // 'en-us'"), so the framework language is set from the current culture.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        base.OnStartup(e);

        // The startup prompt is the first window, so keep the app alive until the main window exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool restore = false;
        if (ViewModels.MainViewModel.SessionExists)
        {
            var choice = new StartupChoiceWindow();
            choice.ShowDialog();
            restore = choice.UsePreviousSession;
        }

        var main = new MainWindow();
        if (restore) main.ViewModel.LoadSession();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
