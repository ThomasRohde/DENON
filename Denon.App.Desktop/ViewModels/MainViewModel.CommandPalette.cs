using Denon.App.Desktop.CommandPalette;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Opens the Ctrl+K command palette. Creating it also registers the Ctrl+K key in the main window.
    /// TODO(#76): bind Ctrl+K to this command in MainWindow after the desktop shell PR merges.
    /// </summary>
    public RelayCommand OpenCommandPaletteCommand { get; } = CommandPaletteHotkey.CreateOpenCommand();
}
