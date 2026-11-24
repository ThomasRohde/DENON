using System.Windows.Media;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Denon.App.Desktop.ViewModels;

public sealed class StationPresetEditor : ObservableObject
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(14, 165, 233),  // Sky Cyan
        Color.FromRgb(245, 158, 11),  // Warm Amber
        Color.FromRgb(16, 185, 129),  // Emerald
        Color.FromRgb(139, 92, 246),  // Violet
        Color.FromRgb(236, 72, 153),  // Rose
        Color.FromRgb(59, 130, 246),  // Cobalt
        Color.FromRgb(20, 184, 166),  // Teal
    ];

    private string _alias = string.Empty;
    private string _displayName = string.Empty;
    private string _url = string.Empty;
    private bool _isCurrentlyPlaying;
    private bool _isProbing;
    private string _probeStatusText = string.Empty;
    private bool? _probeSucceeded;

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                OnPropertyChanged(nameof(Monogram));
                OnPropertyChanged(nameof(BadgeBrush));
                OnPropertyChanged(nameof(BadgeForegroundBrush));
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                OnPropertyChanged(nameof(Monogram));
                OnPropertyChanged(nameof(BadgeBrush));
                OnPropertyChanged(nameof(BadgeForegroundBrush));
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
            {
                ProbeStatusText = string.Empty;
                ProbeSucceeded = null;
            }
        }
    }

    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set => SetProperty(ref _isCurrentlyPlaying, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        set => SetProperty(ref _isProbing, value);
    }

    public string ProbeStatusText
    {
        get => _probeStatusText;
        set => SetProperty(ref _probeStatusText, value);
    }

    public bool? ProbeSucceeded
    {
        get => _probeSucceeded;
        set => SetProperty(ref _probeSucceeded, value);
    }

    public string Monogram => BuildMonogram(DisplayName, Alias);

    public Brush BadgeBrush
    {
        get
        {
            var brush = new SolidColorBrush(PickBadgeColor(string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias));
            brush.Freeze();
            return brush;
        }
    }

    public Brush BadgeForegroundBrush =>
        BadgeContrastHelper.PickThemeForegroundBrush(PickBadgeColor(string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias));

    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(BadgeBrush));
        OnPropertyChanged(nameof(BadgeForegroundBrush));
    }

    public StationPreset ToModel() => new(Alias, DisplayName, Url);

    public static StationPresetEditor FromModel(StationPreset preset) =>
        new()
        {
            Alias = preset.Alias,
            DisplayName = preset.DisplayName,
            Url = preset.Url,
        };

    private static string BuildMonogram(string displayName, string alias)
    {
        var source = string.IsNullOrWhiteSpace(displayName) ? alias : displayName;
        if (string.IsNullOrWhiteSpace(source))
        {
            return "FM";
        }

        var parts = source.Split([' ', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries);
        // Handle "DR P4 København" -> "P4"
        if (parts.Length >= 2 &&
            string.Equals(parts[0], "DR", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Length <= 3)
        {
            return parts[1].ToUpperInvariant();
        }

        if (parts.Length >= 2)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        }

        var single = parts[0];
        return single.Length >= 2
            ? single[..2].ToUpperInvariant()
            : single.ToUpperInvariant();
    }

    private static Color PickBadgeColor(string seed)
    {
        var hash = 17;
        foreach (var ch in seed)
        {
            hash = unchecked((hash * 31) + char.ToLowerInvariant(ch));
        }

        var index = Math.Abs(hash) % Palette.Length;
        return Palette[index];
    }
}
