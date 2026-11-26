using System.Collections.ObjectModel;
using System.Globalization;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

/// <summary>One learned source on the Volume memory tab: its level, how long it was heard, and edit buttons.</summary>
public sealed class LearnedVolumeItem : ObservableObject
{
    private double _level;
    private bool _isCurrent;

    public LearnedVolumeItem(LearnedVolume learned)
    {
        Key = learned.Key;
        Update(learned);
    }

    public string Key { get; }

    public string Label { get; private set; } = string.Empty;

    public string KindLabel { get; private set; } = string.Empty;

    public string HeardText { get; private set; } = string.Empty;

    public double Level
    {
        get => _level;
        private set
        {
            if (SetProperty(ref _level, value))
            {
                OnPropertyChanged(nameof(LevelText));
            }
        }
    }

    /// <summary>"42 · −38 dB".</summary>
    public string LevelText
    {
        get
        {
            var rounded = (int)Math.Round(Level);
            return $"{rounded.ToString(CultureInfo.InvariantCulture)} · {DenonVolumeMath.FormatDecibels(rounded)}";
        }
    }

    /// <summary>True for the source that plays now.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public void Update(LearnedVolume learned)
    {
        Label = learned.Label;
        KindLabel = learned.Kind switch
        {
            "tv_channel" => "LIVE TV",
            "tv_app" => "TV APP",
            "cast" => "CHROMECAST",
            "appletv" => "APPLE TV",
            "input" => "INPUT",
            _ => learned.Kind.ToUpperInvariant(),
        };
        HeardText = $"heard {WatchTimeReport.FormatDuration((int)learned.Seconds)}";
        Level = learned.Level;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(HeardText));
    }
}

/// <summary>
/// Scenes &amp; Acoustics → Volume memory: the switch, "only turn down", and the learned level of each source.
/// </summary>
public sealed partial class MainViewModel
{
    private VolumeMemoryService? _volumeMemory;
    private string _volumeMemoryNowText = "Nothing plays through the receiver.";

    public ObservableCollection<LearnedVolumeItem> LearnedVolumes { get; } = [];

    public RelayCommand StepLearnedVolumeCommand { get; private set; } = new(_ => { });

    public RelayCommand ForgetLearnedVolumeCommand { get; private set; } = new(_ => { });

    public bool VolumeMemoryEnabled
    {
        get => _settingsStore.Current.VolumeMemory.Enabled;
        set
        {
            if (value != VolumeMemoryEnabled)
            {
                _ = _settingsStore.UpdateAsync(s => s.VolumeMemory.Enabled = value);
                OnPropertyChanged();
            }
        }
    }

    public bool VolumeMemoryOnlyLower
    {
        get => _settingsStore.Current.VolumeMemory.OnlyLower;
        set
        {
            if (value != VolumeMemoryOnlyLower)
            {
                _ = _settingsStore.UpdateAsync(s => s.VolumeMemory.OnlyLower = value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>"Now: DR1 · learned 42" or why nothing is set.</summary>
    public string VolumeMemoryNowText
    {
        get => _volumeMemoryNowText;
        private set => SetProperty(ref _volumeMemoryNowText, value);
    }

    public bool HasLearnedVolumes => LearnedVolumes.Count > 0;

    /// <summary>The app calls this once at start-up.</summary>
    public void AttachVolumeMemory(VolumeMemoryService service)
    {
        _volumeMemory = service;
        StepLearnedVolumeCommand = new RelayCommand(StepLearnedVolume);
        ForgetLearnedVolumeCommand = new RelayCommand(ForgetLearnedVolume);
        OnPropertyChanged(nameof(StepLearnedVolumeCommand));
        OnPropertyChanged(nameof(ForgetLearnedVolumeCommand));

        service.Applied += message => _dispatcher.BeginInvoke(() => StatusMessage = message);
        service.Store.Changed += () => _dispatcher.BeginInvoke(RefreshLearnedVolumes);
        service.SourceChanged += () => _dispatcher.BeginInvoke(RefreshLearnedVolumes);
        RefreshLearnedVolumes();
    }

    private void RefreshLearnedVolumes()
    {
        if (_volumeMemory is null)
        {
            return;
        }

        var current = _volumeMemory.CurrentSource;
        var levels = _volumeMemory.Store.Snapshot();
        var byKey = LearnedVolumes.ToDictionary(static i => i.Key, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < levels.Count; i++)
        {
            var learned = levels[i];
            if (byKey.Remove(learned.Key, out var item))
            {
                item.Update(learned);
                var at = LearnedVolumes.IndexOf(item);
                if (at != i)
                {
                    LearnedVolumes.Move(at, i);
                }
            }
            else
            {
                item = new LearnedVolumeItem(learned);
                LearnedVolumes.Insert(i, item);
            }

            item.IsCurrent = string.Equals(learned.Key, current?.Key, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var gone in byKey.Values)
        {
            LearnedVolumes.Remove(gone);
        }

        var now = current is null ? null : levels.FirstOrDefault(l => string.Equals(l.Key, current.Key, StringComparison.OrdinalIgnoreCase));
        VolumeMemoryNowText = current is null
            ? "Nothing plays through the receiver."
            : now is null
                ? $"Now: {current.Label} · nothing learned yet (it learns while you listen)"
                : $"Now: {current.Label} · learned {Math.Round(now.Level).ToString(CultureInfo.InvariantCulture)}";
        OnPropertyChanged(nameof(HasLearnedVolumes));
    }

    /// <summary>Screenshots: made-up sources (never the user's own).</summary>
    internal void LoadShowcaseVolumeMemory()
    {
        LearnedVolumes.Clear();
        var now = DateTimeOffset.UtcNow;
        (string Kind, string Label, double Level, int Hours)[] sample =
        [
            ("tv_channel", "News 24", 41.6, 9),
            ("cast", "Netflix", 55.8, 7),
            ("appletv", "Apple TV+", 54.7, 4),
            ("tv_channel", "Sports One", 47.2, 3),
            ("tv_channel", "Cinema HD", 38.4, 2),
            ("input", "Media Player", 53.1, 1),
        ];
        foreach (var (kind, label, level, hours) in sample)
        {
            var source = ListeningSource.Create(kind, label);
            LearnedVolumes.Add(new LearnedVolumeItem(new LearnedVolume
            {
                Key = source.Key, Kind = kind, Label = label, Level = level, Seconds = hours * 3600, UpdatedUtc = now,
            })
            { IsCurrent = label == "News 24" });
        }

        VolumeMemoryNowText = "Now: News 24 · learned 42";
        OnPropertyChanged(nameof(HasLearnedVolumes));
    }

    /// <summary>CommandParameter "key|step": −1 or +1 on a learned level.</summary>
    private void StepLearnedVolume(object? parameter)
    {
        if (_volumeMemory is null || parameter is not string text || text.LastIndexOf('|') is var bar && bar < 0 ||
            !int.TryParse(text[(bar + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
        {
            return;
        }

        var key = text[..bar];
        if (_volumeMemory.Store.Find(key) is { } learned)
        {
            _volumeMemory.Store.Set(key, Math.Round(learned.Level) + step, DateTimeOffset.UtcNow);
            _volumeMemory.Store.SaveIfChanged();
        }
    }

    private void ForgetLearnedVolume(object? parameter)
    {
        if (_volumeMemory is null || parameter is not string key)
        {
            return;
        }

        _volumeMemory.Store.Forget(key);
        _volumeMemory.Store.SaveIfChanged();
        StatusMessage = "Volume memory forgot that source; it learns it again while you listen.";
    }
}
