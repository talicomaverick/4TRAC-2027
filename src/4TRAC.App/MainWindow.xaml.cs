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
    }

    public ViewModels.MainViewModel ViewModel => _viewModel;

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.SaveSession();
}
