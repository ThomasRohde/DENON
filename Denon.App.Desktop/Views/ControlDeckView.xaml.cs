using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
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

    private bool _equalizerRunning;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>
    /// Starts or stops the small 5-bar equalizer next to the title. It runs only while it shows: an endless
    /// storyboard ticks (and lays the bars out again) every frame even while its element is hidden.
    /// </summary>
    public void SetEqualizerRunning(bool run)
    {
        if (run == _equalizerRunning || EqBars.Resources["EqBarsStoryboard"] is not Storyboard storyboard)
        {
            return;
        }

        _equalizerRunning = run;
        if (run)
        {
            storyboard.Begin(EqBars, isControllable: true);
        }
        else
        {
            storyboard.Stop(EqBars);
        }
    }

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
