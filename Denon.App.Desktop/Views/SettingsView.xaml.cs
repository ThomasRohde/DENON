using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Views;

/// <summary>Settings page: hardware, behavior, relay, lip-sync calibration and shortcuts.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
