using System.Windows;
using Denon.App.Desktop.ViewModels;

namespace Denon.App.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
