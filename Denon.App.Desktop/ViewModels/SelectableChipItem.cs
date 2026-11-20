using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

public sealed class InputSourceItem : ObservableObject
{
    private bool _isActive;

    public InputSourceItem(string code, string displayName, string shortLabel)
    {
        Code = code;
        DisplayName = displayName;
        ShortLabel = shortLabel;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public string ShortLabel { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

public sealed class SoundModeItem : ObservableObject
{
    private bool _isActive;

    public SoundModeItem(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

public sealed class VolumePresetItem : ObservableObject
{
    private bool _isActive;

    public VolumePresetItem(int level, string name)
    {
        Level = level;
        Name = name;
        DbLabel = DenonVolumeMath.FormatDecibels(level);
    }

    public int Level { get; }

    public string Name { get; }

    public string DbLabel { get; }

    public string LevelLabel => Level.ToString();

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

public sealed class SleepTimerOption : ObservableObject
{
    private bool _isActive;

    public SleepTimerOption(int? minutes, string label)
    {
        Minutes = minutes;
        Label = label;
    }

    public int? Minutes { get; }

    public string Label { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
