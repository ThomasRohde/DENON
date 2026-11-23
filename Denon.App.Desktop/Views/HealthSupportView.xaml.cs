using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Views;

/// <summary>Health & Support page: guided health check, activity history and the power-user console.</summary>
public partial class HealthSupportView : UserControl
{
    public HealthSupportView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SendConsoleCommand.CanExecute(null))
        {
            ViewModel.SendConsoleCommand.Execute(null);
            e.Handled = true;
        }
    }
}
