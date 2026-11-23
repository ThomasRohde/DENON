using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Denon.App.Desktop.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Denon.App.Desktop.Views;

/// <summary>Control Deck page: now-playing hero, meters, inputs, sound modes, volume and quick presets.</summary>
public partial class ControlDeckView : UserControl
{
    public ControlDeckView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        ViewModel.BeginVolumeDrag();

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e) =>
        ViewModel.EndVolumeDrag();

    private void VolumeCard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var delta = e.Delta > 0 ? 2 : -2;
        ViewModel.AdjustVolumeByWheel(delta);
        e.Handled = true;
    }
}
