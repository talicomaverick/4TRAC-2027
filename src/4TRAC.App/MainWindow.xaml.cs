using System.ComponentModel;
using System.Windows;

namespace FourTrac.App;

public partial class MainWindow : Window
{
    private readonly ViewModels.MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ViewModels.MainViewModel(MapControl);
        DataContext = _viewModel;
        Closing += OnClosing;

        // A picked weight fills BC, drag function and length. Only a pick from the open list does this;
        // typing a value, or a programmatic change, must never overwrite what the user entered.
        MassCombo.DropDownOpened += (_, _) => _picking = true;
        MassCombo.DropDownClosed += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => _picking = false), System.Windows.Threading.DispatcherPriority.Background);
        MassCombo.SelectionChanged += (_, _) =>
        {
            if (_picking && MassCombo.SelectedItem is ViewModels.BulletOption b)
                _viewModel.ApplyBullet(b);
        };
    }

    private bool _picking;

    public ViewModels.MainViewModel ViewModel => _viewModel;

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.SaveSession();
}
