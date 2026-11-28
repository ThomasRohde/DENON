using Denon.App.Desktop.CommandPalette;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private RelayCommand? _openCommandPaletteCommand;

    /// <summary>
    /// Opens the command palette. MainWindow binds Ctrl+K to it. It does nothing while the shortcut recorder or the
    /// update panel owns the keyboard.
    /// </summary>
    public RelayCommand OpenCommandPaletteCommand => _openCommandPaletteCommand ??= new RelayCommand(
        _ => OpenCommandPalette(),
        _ => !IsRecordingAnyShortcut && !IsUpdatePanelOpen);

    private void OpenCommandPalette()
    {
        if (System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault() is { } window)
        {
            CommandPaletteWindow.Open(window, this);
        }
    }
}
