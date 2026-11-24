using System.Collections.ObjectModel;
using System.Windows.Input;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Infrastructure.Theming;

namespace Denon.App.Desktop.ViewModels;

/// <summary>A theme or skin choice in Settings → Appearance.</summary>
public sealed class ThemeOption : ObservableObject
{
    private bool _isActive;

    public ThemeOption(string id, string label, string hint)
    {
        Id = id;
        Label = label;
        Hint = hint;
    }

    public string Id { get; }

    public string Label { get; }

    public string Hint { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>Settings → Appearance: the theme and the skin apply live and are saved at once (docs/theming.md).</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ThemeOption> ThemeOptions { get; } =
    [
        new(ThemeManager.SystemThemeId, "Follow Windows", "Light or dark, as Windows apps are set (Settings → Personalization → Colors)."),
        new("dark", "Dark", "Dark surfaces. The default look."),
        new("light", "Light", "Light surfaces for bright rooms."),
    ];

    /// <summary>The skins, from <see cref="ThemeManager.Skins"/>.</summary>
    public ObservableCollection<ThemeOption> SkinOptions { get; } =
        new(ThemeManager.Skins.Select(s => new ThemeOption(s.Id, s.Name, s.Hint)));

    private ICommand? _setThemeCommand;
    private ICommand? _setSkinCommand;

    public ICommand SetThemeCommand => _setThemeCommand ??= new RelayCommand(parameter =>
    {
        if (parameter is not ThemeOption option)
        {
            return;
        }

        ThemeManager.ApplyTheme(option.Id);
        MarkActiveTheme(option.Id);
        _suppressNextSettingsReload = true;
        _ = _settingsStore.UpdateAsync(s => s.Ui.Theme = option.Id);
        StatusMessage = option.Id == ThemeManager.SystemThemeId
            ? $"Theme follows Windows (now {ThemeManager.Current.Name})."
            : $"{option.Label} theme.";
    });

    public ICommand SetSkinCommand => _setSkinCommand ??= new RelayCommand(parameter =>
    {
        if (parameter is not ThemeOption option)
        {
            return;
        }

        ThemeManager.ApplySkin(option.Id);
        MarkActiveSkin(option.Id);
        _suppressNextSettingsReload = true;
        _ = _settingsStore.UpdateAsync(s => s.Ui.Skin = option.Id);
        StatusMessage = $"{option.Label} skin.";
    });

    /// <summary>
    /// On the first start the window picked a skin that fits the screen (App.PlaceMainWindow): mark it and say so.
    /// The setting is saved by the caller.
    /// </summary>
    public void ReportSkinPickedForScreen(SkinInfo skin)
    {
        MarkActiveSkin(skin.Id);
        StatusMessage = $"{skin.Name} skin chosen to fit this screen. Change it in Settings → Appearance.";
    }

    /// <summary>Shown under the choices: what "Follow Windows" resolves to now.</summary>
    public string ThemeSummaryText => $"Now showing: {ThemeManager.Current.Name}";

    private void MarkActiveTheme(string? themeSetting)
    {
        var id = string.IsNullOrWhiteSpace(themeSetting) ? ThemeManager.SystemThemeId : themeSetting;
        foreach (var option in ThemeOptions)
        {
            option.IsActive = string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(ThemeSummaryText));
    }

    private void MarkActiveSkin(string? skinSetting)
    {
        var id = ThemeManager.ResolveSkin(skinSetting).Id;
        foreach (var option in SkinOptions)
        {
            option.IsActive = string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase);
        }
    }
}
