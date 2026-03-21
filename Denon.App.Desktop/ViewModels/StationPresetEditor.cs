using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed class StationPresetEditor : ObservableObject
{
    private string _alias = string.Empty;
    private string _displayName = string.Empty;
    private string _url = string.Empty;

    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public StationPreset ToModel() => new(Alias, DisplayName, Url);

    public static StationPresetEditor FromModel(StationPreset preset) =>
        new()
        {
            Alias = preset.Alias,
            DisplayName = preset.DisplayName,
            Url = preset.Url,
        };
}
