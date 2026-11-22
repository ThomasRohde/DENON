using System.Windows;
using System.Windows.Controls;

namespace Denon.Plugin.HomeAssistant;

public partial class HomeAssistantDashboardView : UserControl
{
    private bool _syncingToken;

    public HomeAssistantDashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SyncTokenFromVm();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => SyncTokenFromVm();

    private void SyncTokenFromVm()
    {
        if (DataContext is HomeAssistantViewModel vm && TokenPasswordBox.Password != vm.AccessToken)
        {
            _syncingToken = true;
            try
            {
                TokenPasswordBox.Password = vm.AccessToken ?? string.Empty;
            }
            finally
            {
                _syncingToken = false;
            }
        }
    }

    private void TokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingToken)
        {
            return;
        }

        if (DataContext is HomeAssistantViewModel vm)
        {
            vm.AccessToken = TokenPasswordBox.Password;
        }
    }
}
