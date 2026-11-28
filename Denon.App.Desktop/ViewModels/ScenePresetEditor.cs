using System.Collections.ObjectModel;
using System.Windows.Media;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;
using Denon.App.PluginSdk;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Denon.App.Desktop.ViewModels;

public sealed class ScenePresetEditor : ObservableObject
{
    private string _id = string.Empty;
    private string _name = "Custom Scene";
    private string _description = string.Empty;
    private string _badge = "SCN";
    private string _accentHex = "#00D4FF";
    private bool _powerOn = true;
    private bool _unmute = true;
    private string _inputSource = string.Empty;
    private string _soundMode = string.Empty;
    private string _targetVolumeText = string.Empty;
    private bool _smoothVolumeRamp = true;
    private string _ecoMode = string.Empty;
    private string _sleepTimerText = string.Empty;
    private string _multEq = string.Empty;
    private string _dynamicEqChoice = string.Empty; // "", "ON", "OFF"
    private string _dynamicVolume = string.Empty;
    private string _restorer = string.Empty;
    private string _dimmer = string.Empty;
    private string _subwooferTrimText = string.Empty;
    private string _dialogTrimText = string.Empty;
    private string _streamUrl = string.Empty;
    private string _zone2PowerChoice = string.Empty; // "", "ON", "OFF"
    private string _zone2Source = string.Empty;
    private string _zone2VolumeText = string.Empty;
    private string _zone2MuteChoice = string.Empty; // "", "ON", "OFF"
    private string _heosGroupMembersText = string.Empty;
    private bool _heosUngroup;
    private bool _isActivating;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value);
    }

    public string AccentHex
    {
        get => _accentHex;
        set
        {
            if (SetProperty(ref _accentHex, value))
            {
                OnPropertyChanged(nameof(AccentBrush));
                OnPropertyChanged(nameof(BadgeForegroundBrush));
            }
        }
    }

    public Brush AccentBrush
    {
        get
        {
            var brush = new SolidColorBrush(ResolveAccentColor());
            brush.Freeze();
            return brush;
        }
    }

    public Brush BadgeForegroundBrush => BadgeContrastHelper.PickThemeForegroundBrush(ResolveAccentColor());

    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(BadgeForegroundBrush));
    }

    private Color ResolveAccentColor()
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(AccentHex) ? "#00D4FF" : AccentHex);
        }
        catch
        {
            return Color.FromRgb(0, 212, 255);
        }
    }

    public bool PowerOn
    {
        get => _powerOn;
        set => SetProperty(ref _powerOn, value);
    }

    public bool Unmute
    {
        get => _unmute;
        set => SetProperty(ref _unmute, value);
    }

    public string InputSource
    {
        get => _inputSource;
        set
        {
            if (SetProperty(ref _inputSource, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string SoundMode
    {
        get => _soundMode;
        set
        {
            if (SetProperty(ref _soundMode, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string TargetVolumeText
    {
        get => _targetVolumeText;
        set
        {
            if (SetProperty(ref _targetVolumeText, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public bool SmoothVolumeRamp
    {
        get => _smoothVolumeRamp;
        set => SetProperty(ref _smoothVolumeRamp, value);
    }

    public string EcoMode
    {
        get => _ecoMode;
        set => SetProperty(ref _ecoMode, value);
    }

    public string SleepTimerText
    {
        get => _sleepTimerText;
        set
        {
            if (SetProperty(ref _sleepTimerText, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string MultEq
    {
        get => _multEq;
        set => SetProperty(ref _multEq, value);
    }

    public string DynamicEqChoice
    {
        get => _dynamicEqChoice;
        set => SetProperty(ref _dynamicEqChoice, value);
    }

    public string DynamicVolume
    {
        get => _dynamicVolume;
        set
        {
            if (SetProperty(ref _dynamicVolume, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string Restorer
    {
        get => _restorer;
        set => SetProperty(ref _restorer, value);
    }

    public string Dimmer
    {
        get => _dimmer;
        set
        {
            if (SetProperty(ref _dimmer, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string SubwooferTrimText
    {
        get => _subwooferTrimText;
        set
        {
            if (SetProperty(ref _subwooferTrimText, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string DialogTrimText
    {
        get => _dialogTrimText;
        set
        {
            if (SetProperty(ref _dialogTrimText, value))
            {
                OnPropertyChanged(nameof(SummaryLine));
            }
        }
    }

    public string StreamUrl
    {
        get => _streamUrl;
        set => SetProperty(ref _streamUrl, value);
    }

    /// <summary>Zone 2 power: ON, OFF, or empty for no change.</summary>
    public string Zone2PowerChoice
    {
        get => _zone2PowerChoice;
        set => SetProperty(ref _zone2PowerChoice, value);
    }

    /// <summary>Zone 2 source code (TV, NET, SOURCE), or empty for no change.</summary>
    public string Zone2Source
    {
        get => _zone2Source;
        set => SetProperty(ref _zone2Source, value);
    }

    /// <summary>Zone 2 volume 0–98, or empty for no change.</summary>
    public string Zone2VolumeText
    {
        get => _zone2VolumeText;
        set => SetProperty(ref _zone2VolumeText, value);
    }

    /// <summary>Zone 2 mute: ON, OFF, or empty for no change.</summary>
    public string Zone2MuteChoice
    {
        get => _zone2MuteChoice;
        set => SetProperty(ref _zone2MuteChoice, value);
    }

    /// <summary>HEOS players to group with this receiver, comma-separated.</summary>
    public string HeosGroupMembersText
    {
        get => _heosGroupMembersText;
        set => SetProperty(ref _heosGroupMembersText, value);
    }

    public bool HeosUngroup
    {
        get => _heosUngroup;
        set => SetProperty(ref _heosUngroup, value);
    }

    public bool IsActivating
    {
        get => _isActivating;
        set => SetProperty(ref _isActivating, value);
    }

    public ObservableCollection<ScenePluginActionStepEditor> PluginActions { get; } = [];

    public bool HasPluginActions => PluginActions.Count > 0;

    public string PluginActionsBadge => PluginActions.Count switch
    {
        0 => string.Empty,
        1 => "+1 plugin step",
        var n => $"+{n} plugin steps",
    };

    public void NotifyPluginActionsChanged()
    {
        OnPropertyChanged(nameof(HasPluginActions));
        OnPropertyChanged(nameof(PluginActionsBadge));
        OnPropertyChanged(nameof(SummaryLine));
    }

    public string SummaryLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(InputSource))
            {
                parts.Add(DenonCatalog.GetFriendlyInputName(InputSource));
            }

            if (!string.IsNullOrWhiteSpace(SoundMode))
            {
                parts.Add(SoundMode);
            }

            if (TryParseNullableInt(TargetVolumeText) is int vol)
            {
                parts.Add($"Vol {vol}");
            }

            if (!string.IsNullOrWhiteSpace(DynamicVolume) && !string.Equals(DynamicVolume, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"DynVol {DynamicVolume}");
            }

            if (TryParseNullableInt(SubwooferTrimText) is int sw && sw != 0)
            {
                parts.Add($"Sub {DenonAcousticCatalog.FormatDbOffset(sw)}");
            }

            if (TryParseNullableInt(DialogTrimText) is int dil && dil != 0)
            {
                parts.Add($"Dialog {DenonAcousticCatalog.FormatDbOffset(dil)}");
            }

            if (!string.IsNullOrWhiteSpace(Dimmer))
            {
                parts.Add($"Dim {Dimmer}");
            }

            if (TryParseNullableInt(SleepTimerText) is > 0 and var sleep)
            {
                parts.Add($"Sleep {sleep}m");
            }

            if (PluginActions.Count > 0)
            {
                parts.Add(PluginActionsBadge);
            }

            return parts.Count > 0 ? string.Join("  •  ", parts) : "Custom AVR hardware macro";
        }
    }

    public static ScenePresetEditor FromModel(
        ScenePreset model,
        Func<string, string, IReadOnlyList<PluginActionParameter>?>? parameterLookup = null)
    {
        var editor = new ScenePresetEditor
        {
            Id = model.Id,
            Name = model.Name,
            Description = model.Description,
            Badge = model.Badge,
            AccentHex = model.AccentHex,
            PowerOn = model.PowerOn,
            Unmute = model.Unmute,
            InputSource = model.InputSource ?? string.Empty,
            SoundMode = model.SoundMode ?? string.Empty,
            TargetVolumeText = model.TargetVolume?.ToString() ?? string.Empty,
            SmoothVolumeRamp = model.SmoothVolumeRamp,
            EcoMode = model.EcoMode ?? string.Empty,
            SleepTimerText = model.SleepTimerMinutes?.ToString() ?? string.Empty,
            MultEq = model.MultEq ?? string.Empty,
            DynamicEqChoice = model.DynamicEq switch { true => "ON", false => "OFF", _ => string.Empty },
            DynamicVolume = model.DynamicVolume ?? string.Empty,
            Restorer = model.Restorer ?? string.Empty,
            Dimmer = model.Dimmer ?? string.Empty,
            SubwooferTrimText = model.SubwooferTrimDb?.ToString() ?? string.Empty,
            DialogTrimText = model.DialogTrimDb?.ToString() ?? string.Empty,
            StreamUrl = model.StreamUrl ?? string.Empty,
            Zone2PowerChoice = OnOffChoice(model.Zone2On),
            Zone2Source = model.Zone2Source ?? string.Empty,
            Zone2VolumeText = model.Zone2Volume?.ToString() ?? string.Empty,
            Zone2MuteChoice = OnOffChoice(model.Zone2Muted),
            HeosGroupMembersText = string.Join(", ", model.HeosGroupMembers),
            HeosUngroup = model.HeosUngroup,
        };

        if (model.PluginActions is { Count: > 0 })
        {
            foreach (var step in model.PluginActions)
            {
                var descriptors = parameterLookup?.Invoke(step.PluginId, step.ActionId);
                editor.PluginActions.Add(ScenePluginActionStepEditor.FromModel(step, descriptors));
            }
        }

        return editor;
    }

    public ScenePreset ToModel() =>
        new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Badge = Badge,
            AccentHex = AccentHex,
            PowerOn = PowerOn,
            Unmute = Unmute,
            InputSource = NullIfEmpty(InputSource),
            SoundMode = NullIfEmpty(SoundMode),
            TargetVolume = TryParseNullableInt(TargetVolumeText),
            SmoothVolumeRamp = SmoothVolumeRamp,
            EcoMode = NullIfEmpty(EcoMode),
            SleepTimerMinutes = TryParseNullableInt(SleepTimerText),
            MultEq = NullIfEmpty(MultEq),
            DynamicEq = DynamicEqChoice.Trim().ToUpperInvariant() switch
            {
                "ON" => true,
                "OFF" => false,
                _ => null,
            },
            DynamicVolume = NullIfEmpty(DynamicVolume),
            Restorer = NullIfEmpty(Restorer),
            Dimmer = NullIfEmpty(Dimmer),
            SubwooferTrimDb = TryParseNullableInt(SubwooferTrimText),
            DialogTrimDb = TryParseNullableInt(DialogTrimText),
            StreamUrl = NullIfEmpty(StreamUrl),
            Zone2On = ParseOnOff(Zone2PowerChoice),
            Zone2Source = NullIfEmpty(Zone2Source)?.ToUpperInvariant(),
            Zone2Volume = TryParseNullableInt(Zone2VolumeText),
            Zone2Muted = ParseOnOff(Zone2MuteChoice),
            HeosGroupMembers = HeosGroupMembersText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            HeosUngroup = HeosUngroup,
            PluginActions = PluginActions.Select(static s => s.ToModel()).ToList(),
        };

    private static string OnOffChoice(bool? value) => value switch { true => "ON", false => "OFF", _ => string.Empty };

    private static bool? ParseOnOff(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "ON" => true,
        "OFF" => false,
        _ => null,
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryParseNullableInt(string? text) =>
        int.TryParse(text?.Trim(), out var parsed) ? parsed : null;
}
