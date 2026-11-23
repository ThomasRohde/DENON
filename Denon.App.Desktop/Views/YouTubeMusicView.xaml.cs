using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Views;

/// <summary>YouTube & Music page: search, results, queue and history.</summary>
public partial class YouTubeMusicView : UserControl
{
    public YouTubeMusicView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void YouTubeSearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SearchYouTubeCommand.CanExecute(null))
        {
            ViewModel.SearchYouTubeCommand.Execute(null);
            e.Handled = true;
        }
    }
}
