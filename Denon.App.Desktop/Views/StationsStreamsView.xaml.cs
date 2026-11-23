using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Views;

/// <summary>Stations & Streams page: presets, HEOS favorites and the global radio explorer.</summary>
public partial class StationsStreamsView : UserControl
{
    public StationsStreamsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void RadioBrowserSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SearchRadioBrowserCommand.CanExecute(null))
        {
            ViewModel.SearchRadioBrowserCommand.Execute(null);
            e.Handled = true;
        }
    }
}
