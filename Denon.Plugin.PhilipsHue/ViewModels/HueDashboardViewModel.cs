using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Denon.App.PluginSdk;
using Denon.Plugin.PhilipsHue.Models;
using Denon.Plugin.PhilipsHue.Services;

namespace Denon.Plugin.PhilipsHue.ViewModels;

public abstract class HueObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class HueCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public HueCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class HueAsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Action<Exception>? _onError;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    /// <param name="onError">
    /// Where a failed run goes (the dashboard shows it). Execute is async void, so an exception it let out would
    /// close the app; with no handler it is dropped.
    /// </param>
    public HueAsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public static HueAsyncCommand WithErrors(Action<Exception>? onError, Func<object?, Task> execute, Func<object?, bool>? canExecute = null) =>
        new(execute, canExecute, onError);

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class HueLightViewModel : HueObservableObject
{
    private readonly Func<HueLightViewModel, bool, Task> _onToggle;
    private readonly Func<HueLightViewModel, int, Task> _onBrightnessChanged;
    private readonly Func<HueLightViewModel, int, Task> _onColorTempChanged;
    private readonly Func<HueLightViewModel, HueColorPreset, Task> _onPresetApplied;

    private bool _suppressUpdates;
    private bool _isOn;
    private bool _isReachable;
    private int _brightnessPercent;
    private int _colorTempMirek;
    private string _previewHex = "#FDE68A";
    private Brush _previewBrush = Brushes.Goldenrod;
    private CancellationTokenSource? _briDebounce;
    private CancellationTokenSource? _ctDebounce;

    public HueLightViewModel(
        HueLightState state,
        Func<HueLightViewModel, bool, Task> onToggle,
        Func<HueLightViewModel, int, Task> onBrightnessChanged,
        Func<HueLightViewModel, int, Task> onColorTempChanged,
        Func<HueLightViewModel, HueColorPreset, Task> onPresetApplied,
        Action<Exception>? onError = null)
    {
        Id = state.Id;
        Name = state.Name;
        TypeName = state.TypeName;
        ProductName = state.ProductName;
        SupportsColor = state.SupportsColor;
        SupportsColorTemp = state.SupportsColorTemp;

        _onToggle = onToggle;
        _onBrightnessChanged = onBrightnessChanged;
        _onColorTempChanged = onColorTempChanged;
        _onPresetApplied = onPresetApplied;

        ToggleCommand = HueAsyncCommand.WithErrors(onError, _ => _onToggle(this, !IsOn));
        ApplySwatchCommand = HueAsyncCommand.WithErrors(onError, param =>
            param is HueColorPreset preset ? _onPresetApplied(this, preset) : Task.CompletedTask);

        ApplyState(state);
    }

    public string Id { get; }

    public string Name { get; }

    public string TypeName { get; }

    public string ProductName { get; }

    public bool SupportsColor { get; }

    public bool SupportsColorTemp { get; }

    public IReadOnlyList<HueColorPreset> QuickSwatches => HueColorPreset.Defaults;

    public HueAsyncCommand ToggleCommand { get; }

    public HueAsyncCommand ApplySwatchCommand { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
            {
                RaisePropertyChanged(nameof(PowerLabel));
                RaisePropertyChanged(nameof(StateSummary));
            }
        }
    }

    public bool IsReachable
    {
        get => _isReachable;
        private set
        {
            if (SetProperty(ref _isReachable, value))
            {
                RaisePropertyChanged(nameof(ReachabilityLabel));
            }
        }
    }

    public int BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _brightnessPercent, clamped))
            {
                RaisePropertyChanged(nameof(StateSummary));
                if (!_suppressUpdates)
                {
                    DebounceBrightness(clamped);
                }
            }
        }
    }

    public int ColorTempMirek
    {
        get => _colorTempMirek;
        set
        {
            var clamped = Math.Clamp(value, 153, 500);
            if (SetProperty(ref _colorTempMirek, clamped))
            {
                RaisePropertyChanged(nameof(ColorTempKelvinLabel));
                if (!_suppressUpdates)
                {
                    DebounceColorTemp(clamped);
                }
            }
        }
    }

    public string PreviewHex
    {
        get => _previewHex;
        private set
        {
            if (SetProperty(ref _previewHex, value))
            {
                PreviewBrush = CreateFrozenBrush(value);
            }
        }
    }

    public Brush PreviewBrush
    {
        get => _previewBrush;
        private set => SetProperty(ref _previewBrush, value);
    }

    public string PowerLabel => IsOn ? "ON" : "OFF";

    public string ReachabilityLabel => IsReachable ? "ONLINE" : "UNREACHABLE";

    public string ColorTempKelvinLabel
    {
        get
        {
            var kelvin = (int)Math.Round(1_000_000.0 / Math.Clamp(ColorTempMirek, 153, 500));
            return $"{kelvin}K";
        }
    }

    public string StateSummary => IsOn ? $"{BrightnessPercent}% • {(SupportsColor ? "Color" : SupportsColorTemp ? ColorTempKelvinLabel : "White")}" : "Off";

    public void ApplyState(HueLightState state)
    {
        _suppressUpdates = true;
        try
        {
            IsOn = state.IsOn;
            IsReachable = state.IsReachable;
            BrightnessPercent = state.BrightnessPercent;
            ColorTempMirek = state.ColorTempMirek ?? 366;
            PreviewHex = state.PreviewHex;
        }
        finally
        {
            _suppressUpdates = false;
        }
    }

    private void DebounceBrightness(int percent)
    {
        _briDebounce?.Cancel();
        _briDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _briDebounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, cts.Token);
                await _onBrightnessChanged(this, percent);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void DebounceColorTemp(int mirek)
    {
        _ctDebounce?.Cancel();
        _ctDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _ctDebounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, cts.Token);
                await _onColorTempChanged(this, mirek);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Goldenrod;
        }
    }
}

public sealed class HueRoomChipViewModel : HueObservableObject
{
    private bool _isSelected;
    private bool _anyOn;
    private int _brightnessPercent;

    public HueRoomChipViewModel(HueRoomGroup group)
    {
        Id = group.Id;
        Name = group.Name;
        GroupType = group.GroupType;
        LightIds = group.LightIds;
        AnyOn = group.AnyOn;
        BrightnessPercent = group.BrightnessPercent;
    }

    public string Id { get; }

    public string Name { get; }

    public string GroupType { get; }

    public IReadOnlyList<string> LightIds { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool AnyOn
    {
        get => _anyOn;
        set
        {
            if (SetProperty(ref _anyOn, value))
            {
                RaisePropertyChanged(nameof(Subtitle));
            }
        }
    }

    public int BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            if (SetProperty(ref _brightnessPercent, value))
            {
                RaisePropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string Subtitle
    {
        get
        {
            var unit = LightIds.Count == 1 ? "light" : "lights";
            return AnyOn
                ? $"{LightIds.Count} {unit} • {BrightnessPercent}%"
                : $"{LightIds.Count} {unit} • Off";
        }
    }
}

/// <summary>One Hue Entertainment area in the Music Sync picker.</summary>
public sealed class HueEntertainmentAreaOption
{
    public HueEntertainmentAreaOption(HueEntertainmentArea area)
    {
        Id = area.Id;
        Name = area.Name;
        ChannelCount = area.ChannelCount;
    }

    public string Id { get; }

    public string Name { get; }

    public int ChannelCount { get; }

    public string Label => $"{Name} • {ChannelCount} {(ChannelCount == 1 ? "channel" : "channels")}";
}

public sealed class HueDashboardViewModel : HueObservableObject
{
    private readonly PhilipsHuePlugin _plugin;
    private bool _suppressSliderPush;
    private bool _suppressRuleSave;
    private CancellationTokenSource? _roomBriDebounce;
    private CancellationTokenSource? _pairingCts;

    private string _bridgeIpInput = string.Empty;
    private bool _useHttps = true;
    private bool _isDemoMode;
    private bool _isPaired;
    private bool _isPairing;
    private string _connectionBadgeText = "NOT CONNECTED";
    private string _connectionBadgeTone = "AccentAmber"; // a token: a theme switch rebuilds the brush
    private string _statusMessage = "Discover your Philips Hue Bridge on LAN, enter its IP, or try Demo Mode.";
    private string _pairingHintText = string.Empty;
    private bool _isBusy;

    private HueRoomChipViewModel? _selectedRoom;
    private int _selectedRoomBrightness = 65;

    // AVR Automation rule bindings
    private bool _syncWithSmartScenes = true;
    private bool _turnOnWithReceiverPower;
    private bool _turnOffWithReceiverStandby;
    private bool _cinemaPauseLights;
    private string _automationTargetRoomId = string.Empty;
    private string _lastAutomationEventText = "Waiting for Denon AVR events (Power, Smart Scenes, Playback)...";

    // Real-time Music Sync & Album-Art Ambient bindings
    private bool _isMusicSyncEnabled;
    private bool _isDemoBeatActive;
    private bool _isDtlsStreaming;
    private HueMusicSyncMode _musicSyncMode = HueMusicSyncMode.BassPulse;
    private double _musicSyncSensitivity = 1.25;
    private int _musicSyncMinBrightness = 16;
    private int _musicSyncMaxBrightness = 95;
    private double _liveBassPercent;
    private double _liveMidPercent;
    private double _liveTreblePercent;
    private bool _liveBeatFlash;
    private bool _hasLiveAudioSignal;
    private string _albumArtDominantHex = "#00D4FF";
    private string _albumArtSecondaryHex = "#F59E0B";
    private string _albumArtTrackLabel = "Waiting for artwork...";
    private string _entertainmentAreaSummary = "LAN Fast-Sync Ready";
    private HueEntertainmentAreaOption? _selectedEntertainmentArea;
    private string _entertainmentAreaHint = string.Empty;

    /// <summary>
    /// True while the area list is filled from the plugin. The ComboBox then sets its selection by itself; that
    /// must not save an area or restart the stream.
    /// </summary>
    private bool _suppressAreaSave;
    private long _lastMeterUiTickMs;
    private readonly LiveSignalHold _liveSignalHold = new();

    /// <summary>A command that fails (the bridge went offline on "Exit Demo", a storage error) shows why here.</summary>
    private void ShowCommandError(Exception ex) => RunOnUi(() => StatusMessage = $"⚠ {ex.Message}");

    public HueDashboardViewModel(PhilipsHuePlugin plugin)
    {
        _plugin = plugin;

        DiscoverBridgeCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => DiscoverBridgeAsync());
        StartPairingCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => StartPairingAsync(), _ => !IsPairing && !string.IsNullOrWhiteSpace(BridgeIpInput));
        CancelPairingCommand = new HueCommand(_ => CancelPairing(), _ => IsPairing);
        ToggleDemoModeCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => ToggleDemoModeAsync());
        DisconnectBridgeCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => DisconnectBridgeAsync());
        RefreshStateCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => RefreshStateAsync(), _ => IsConnectedOrDemo);

        AllLightsOnCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => ExecuteQuickGroupAsync("0", isOn: true), _ => IsConnectedOrDemo);
        AllLightsOffCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => ExecuteQuickGroupAsync("0", isOn: false), _ => IsConnectedOrDemo);
        SetAllBrightnessCommand = HueAsyncCommand.WithErrors(ShowCommandError, param =>
        {
            if (param is string s && int.TryParse(s, out var pct))
            {
                return ExecuteQuickGroupAsync("0", isOn: true, brightnessPercent: pct);
            }

            return Task.CompletedTask;
        }, _ => IsConnectedOrDemo);

        SelectRoomCommand = new HueCommand(param =>
        {
            if (param is HueRoomChipViewModel room)
            {
                SelectRoom(room);
            }
        });
        ToggleSelectedRoomCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => ToggleSelectedRoomAsync(), _ => IsConnectedOrDemo && SelectedRoom is not null);
        ApplyMoodPresetCommand = HueAsyncCommand.WithErrors(ShowCommandError, param =>
            param is HueColorPreset preset ? ApplyPresetToSelectedRoomAsync(preset) : Task.CompletedTask, _ => IsConnectedOrDemo);
        ActivateHueSceneCommand = HueAsyncCommand.WithErrors(ShowCommandError, param =>
            param is HueSceneItem scene ? ActivateHueSceneAsync(scene) : Task.CompletedTask, _ => IsConnectedOrDemo);
        SelectDiscoveredBridgeCommand = new HueCommand(param =>
        {
            if (param is HueDiscoveredBridge bridge)
            {
                BridgeIpInput = bridge.IpAddress;
                StatusMessage = $"Selected Hue Bridge at {bridge.IpAddress}. Click 'Press Bridge Button & Pair' to connect.";
            }
        });
        UseSelectedRoomForAutomationCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => SetAutomationTargetToSelectedRoomAsync());
        ShowAllLightsCommand = new HueCommand(_ =>
        {
            var allChip = Rooms.FirstOrDefault(r => r.Id == "0");
            if (allChip is not null)
            {
                SelectRoom(allChip);
            }
        });
        ToggleMusicSyncCommand = HueAsyncCommand.WithErrors(ShowCommandError, _ => ToggleMusicSyncAsync(), _ => IsConnectedOrDemo);
        SetMusicSyncModeCommand = HueAsyncCommand.WithErrors(ShowCommandError, param =>
        {
            if (param is string modeStr && Enum.TryParse<HueMusicSyncMode>(modeStr, ignoreCase: true, out var parsed))
            {
                return SetMusicSyncModeAsync(parsed);
            }

            return Task.CompletedTask;
        }, _ => IsConnectedOrDemo);
        ToggleDemoBeatCommand = new HueCommand(_ => _plugin.ToggleDemoBeatSimulator(), _ => IsConnectedOrDemo);
        ToggleSetupCommand = new HueCommand(_ => IsSetupExpanded = !IsSetupExpanded);
    }

    public ObservableCollection<HueDiscoveredBridge> DiscoveredBridges { get; } = [];
    public ObservableCollection<HueRoomChipViewModel> Rooms { get; } = [];
    public ObservableCollection<HueLightViewModel> VisibleLights { get; } = [];
    public ObservableCollection<HueSceneItem> VisibleScenes { get; } = [];
    public ObservableCollection<HueEntertainmentAreaOption> EntertainmentAreas { get; } = [];

    public IReadOnlyList<HueColorPreset> MoodPresets => HueColorPreset.Defaults;

    public HueAsyncCommand DiscoverBridgeCommand { get; }
    public HueAsyncCommand StartPairingCommand { get; }
    public HueCommand CancelPairingCommand { get; }
    public HueAsyncCommand ToggleDemoModeCommand { get; }
    public HueAsyncCommand DisconnectBridgeCommand { get; }
    public HueAsyncCommand RefreshStateCommand { get; }
    public HueAsyncCommand AllLightsOnCommand { get; }
    public HueAsyncCommand AllLightsOffCommand { get; }
    public HueAsyncCommand SetAllBrightnessCommand { get; }
    public HueCommand SelectRoomCommand { get; }
    public HueAsyncCommand ToggleSelectedRoomCommand { get; }
    public HueAsyncCommand ApplyMoodPresetCommand { get; }
    public HueAsyncCommand ActivateHueSceneCommand { get; }
    public HueCommand SelectDiscoveredBridgeCommand { get; }
    public HueAsyncCommand UseSelectedRoomForAutomationCommand { get; }
    public HueCommand ShowAllLightsCommand { get; }
    public HueAsyncCommand ToggleMusicSyncCommand { get; }
    public HueAsyncCommand SetMusicSyncModeCommand { get; }
    public HueCommand ToggleDemoBeatCommand { get; }
    public HueCommand ToggleSetupCommand { get; }

    private bool _isSetupExpanded;

    public bool IsSetupExpanded
    {
        get => _isSetupExpanded;
        set
        {
            if (SetProperty(ref _isSetupExpanded, value))
            {
                RaisePropertyChanged(nameof(ShowSetupControls));
                RaisePropertyChanged(nameof(SetupToggleLabel));
            }
        }
    }

    public bool ShowSetupControls => IsSetupExpanded || !IsConnectedOrDemo || IsPairing;

    public string SetupToggleLabel => ShowSetupControls ? "Hide Setup ▴" : "Bridge Setup ▾";

    public string BridgeIpInput
    {
        get => _bridgeIpInput;
        set
        {
            if (SetProperty(ref _bridgeIpInput, value))
            {
                StartPairingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool UseHttps
    {
        get => _useHttps;
        set => SetProperty(ref _useHttps, value);
    }

    public bool IsDemoMode
    {
        get => _isDemoMode;
        private set
        {
            if (SetProperty(ref _isDemoMode, value))
            {
                RaisePropertyChanged(nameof(DemoModeButtonLabel));
                RaisePropertyChanged(nameof(IsConnectedOrDemo));
                RaisePropertyChanged(nameof(ShowSetupControls));
                RaisePropertyChanged(nameof(SetupToggleLabel));
                RefreshCommandStates();
            }
        }
    }

    public string DemoModeButtonLabel => IsDemoMode ? "Exit Demo Mode" : "Try Demo Mode";

    public bool IsPaired
    {
        get => _isPaired;
        private set
        {
            if (SetProperty(ref _isPaired, value))
            {
                RaisePropertyChanged(nameof(IsConnectedOrDemo));
                RaisePropertyChanged(nameof(ShowSetupControls));
                RaisePropertyChanged(nameof(SetupToggleLabel));
                RefreshCommandStates();
            }
        }
    }

    public bool IsConnectedOrDemo => IsPaired || IsDemoMode;

    public bool IsPairing
    {
        get => _isPairing;
        private set
        {
            if (SetProperty(ref _isPairing, value))
            {
                StartPairingCommand.RaiseCanExecuteChanged();
                CancelPairingCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ShowSetupControls));
                RaisePropertyChanged(nameof(SetupToggleLabel));
            }
        }
    }

    public string ConnectionBadgeText
    {
        get => _connectionBadgeText;
        private set => SetProperty(ref _connectionBadgeText, value);
    }

    public Brush ConnectionBadgeBrush => HostTheme.Brush(_connectionBadgeTone);

    private string ConnectionBadgeTone
    {
        set
        {
            if (_connectionBadgeTone != value)
            {
                _connectionBadgeTone = value;
                RaisePropertyChanged(nameof(ConnectionBadgeBrush));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string PairingHintText
    {
        get => _pairingHintText;
        private set => SetProperty(ref _pairingHintText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public HueRoomChipViewModel? SelectedRoom
    {
        get => _selectedRoom;
        private set
        {
            if (SetProperty(ref _selectedRoom, value))
            {
                RaisePropertyChanged(nameof(SelectedRoomHeader));
                RaisePropertyChanged(nameof(SelectedRoomToggleLabel));
                ToggleSelectedRoomCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedRoomHeader
    {
        get
        {
            if (SelectedRoom is null)
            {
                return $"All Lights ({_allLightsCache.Count})";
            }

            var unit = SelectedRoom.LightIds.Count == 1 ? "light" : "lights";
            return $"{SelectedRoom.Name} ({SelectedRoom.LightIds.Count} {unit})";
        }
    }

    public string IndividualLightsHeader => SelectedRoom is null || SelectedRoom.Id == "0"
        ? $"INDIVIDUAL LIGHTS ({VisibleLights.Count} TOTAL)"
        : $"INDIVIDUAL LIGHTS • {SelectedRoom.Name.ToUpperInvariant()} ({VisibleLights.Count} OF {_allLightsCache.Count})";

    public bool IsFilteredToSingleRoom => SelectedRoom is not null && SelectedRoom.Id != "0";

    public string SelectedRoomToggleLabel => SelectedRoom?.AnyOn == true ? "Turn Room Off" : "Turn Room On";

    public int SelectedRoomBrightness
    {
        get => _selectedRoomBrightness;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _selectedRoomBrightness, clamped) && !_suppressSliderPush && SelectedRoom is not null)
            {
                DebounceRoomBrightness(SelectedRoom.Id, clamped);
            }
        }
    }

    public bool SyncWithSmartScenes
    {
        get => _syncWithSmartScenes;
        set
        {
            if (SetProperty(ref _syncWithSmartScenes, value) && !_suppressRuleSave)
            {
                _ = SaveAutomationRulesAsync();
            }
        }
    }

    public bool TurnOnWithReceiverPower
    {
        get => _turnOnWithReceiverPower;
        set
        {
            if (SetProperty(ref _turnOnWithReceiverPower, value) && !_suppressRuleSave)
            {
                _ = SaveAutomationRulesAsync();
            }
        }
    }

    public bool TurnOffWithReceiverStandby
    {
        get => _turnOffWithReceiverStandby;
        set
        {
            if (SetProperty(ref _turnOffWithReceiverStandby, value) && !_suppressRuleSave)
            {
                _ = SaveAutomationRulesAsync();
            }
        }
    }

    public bool CinemaPauseLights
    {
        get => _cinemaPauseLights;
        set
        {
            if (SetProperty(ref _cinemaPauseLights, value) && !_suppressRuleSave)
            {
                _ = SaveAutomationRulesAsync();
            }
        }
    }

    public string AutomationTargetRoomLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_automationTargetRoomId) || _automationTargetRoomId == "0")
            {
                return "All Hue Lights";
            }

            return Rooms.FirstOrDefault(r => r.Id == _automationTargetRoomId)?.Name ?? $"Room #{_automationTargetRoomId}";
        }
    }

    public string LastAutomationEventText
    {
        get => _lastAutomationEventText;
        private set => SetProperty(ref _lastAutomationEventText, value);
    }

    public bool HasDiscoveredBridges => DiscoveredBridges.Count > 0;

    public bool HasVisibleScenes => VisibleScenes.Count > 0;

    public bool IsMusicSyncEnabled
    {
        get => _isMusicSyncEnabled;
        private set
        {
            if (SetProperty(ref _isMusicSyncEnabled, value))
            {
                RaisePropertyChanged(nameof(MusicSyncToggleLabel));
                RaisePropertyChanged(nameof(MusicSyncStatusBadge));
            }
        }
    }

    public string MusicSyncToggleLabel => IsMusicSyncEnabled ? "Stop Music Sync" : "Start Music Sync";

    public string MusicSyncStatusBadge => IsDemoBeatActive
        ? "DEMO BEAT LIVE"
        : IsMusicSyncEnabled
            ? (_isDtlsStreaming
                ? "50 Hz DTLS LIVE"
                : (HasLiveAudioSignal ? "LIVE FFT SYNC" : "WAITING FOR AUDIO"))
            : "STANDBY";

    public void SetDtlsStreamingState(bool active) => RunOnUi(() =>
    {
        if (SetProperty(ref _isDtlsStreaming, active, nameof(MusicSyncStatusBadge)))
        {
            RaisePropertyChanged(nameof(MusicSyncStatusBadge));
        }
    });

    public bool IsDemoBeatActive
    {
        get => _isDemoBeatActive;
        private set
        {
            if (SetProperty(ref _isDemoBeatActive, value))
            {
                RaisePropertyChanged(nameof(DemoBeatButtonLabel));
                RaisePropertyChanged(nameof(MusicSyncStatusBadge));
            }
        }
    }

    public string DemoBeatButtonLabel => IsDemoBeatActive ? "Stop Test Beat" : "Simulate Beat";

    public HueMusicSyncMode MusicSyncMode
    {
        get => _musicSyncMode;
        private set
        {
            if (SetProperty(ref _musicSyncMode, value))
            {
                RaisePropertyChanged(nameof(MusicSyncModeName));
            }
        }
    }

    public string MusicSyncModeName => MusicSyncMode.ToString();

    public double MusicSyncSensitivity
    {
        get => _musicSyncSensitivity;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0.5, 2.5), 2);
            if (SetProperty(ref _musicSyncSensitivity, clamped))
            {
                RaisePropertyChanged(nameof(MusicSyncSensitivityLabel));
                if (!_suppressRuleSave)
                {
                    _ = SaveMusicSyncConfigAsync();
                }
            }
        }
    }

    public string MusicSyncSensitivityLabel => $"{MusicSyncSensitivity:0.0}×";

    public int MusicSyncMinBrightness
    {
        get => _musicSyncMinBrightness;
        set
        {
            var clamped = Math.Clamp(value, 5, 85);
            if (SetProperty(ref _musicSyncMinBrightness, clamped) && !_suppressRuleSave)
            {
                _ = SaveMusicSyncConfigAsync();
            }
        }
    }

    public int MusicSyncMaxBrightness
    {
        get => _musicSyncMaxBrightness;
        set
        {
            var clamped = Math.Clamp(value, 20, 100);
            if (SetProperty(ref _musicSyncMaxBrightness, clamped) && !_suppressRuleSave)
            {
                _ = SaveMusicSyncConfigAsync();
            }
        }
    }

    public double LiveBassPercent
    {
        get => _liveBassPercent;
        private set => SetProperty(ref _liveBassPercent, value);
    }

    public double LiveMidPercent
    {
        get => _liveMidPercent;
        private set => SetProperty(ref _liveMidPercent, value);
    }

    public double LiveTreblePercent
    {
        get => _liveTreblePercent;
        private set => SetProperty(ref _liveTreblePercent, value);
    }

    public bool LiveBeatFlash
    {
        get => _liveBeatFlash;
        private set => SetProperty(ref _liveBeatFlash, value);
    }

    public bool HasLiveAudioSignal
    {
        get => _hasLiveAudioSignal;
        private set
        {
            if (SetProperty(ref _hasLiveAudioSignal, value))
            {
                RaisePropertyChanged(nameof(MusicSyncStatusBadge));
            }
        }
    }

    public string AlbumArtDominantHex
    {
        get => _albumArtDominantHex;
        private set => SetProperty(ref _albumArtDominantHex, value);
    }

    public string AlbumArtSecondaryHex
    {
        get => _albumArtSecondaryHex;
        private set => SetProperty(ref _albumArtSecondaryHex, value);
    }

    public string AlbumArtTrackLabel
    {
        get => _albumArtTrackLabel;
        private set => SetProperty(ref _albumArtTrackLabel, value);
    }

    public string EntertainmentAreaSummary
    {
        get => _entertainmentAreaSummary;
        private set => SetProperty(ref _entertainmentAreaSummary, value);
    }

    public bool HasEntertainmentAreas => EntertainmentAreas.Count > 0;

    /// <summary>
    /// The area Music Sync uses. When the user picks one, the plugin saves it (and moves a running stream to it).
    /// When the saved area is gone, this shows the area the plugin uses instead, and the saved id stays.
    /// </summary>
    public HueEntertainmentAreaOption? SelectedEntertainmentArea
    {
        get => _selectedEntertainmentArea;
        set
        {
            if (!SetProperty(ref _selectedEntertainmentArea, value) || _suppressAreaSave || value is null)
            {
                return;
            }

            UpdateEntertainmentAreaText(value.Id);
            _ = SelectEntertainmentAreaAsync(value);
        }
    }

    public string EntertainmentAreaHint
    {
        get => _entertainmentAreaHint;
        private set
        {
            if (SetProperty(ref _entertainmentAreaHint, value))
            {
                RaisePropertyChanged(nameof(HasEntertainmentAreaHint));
            }
        }
    }

    public bool HasEntertainmentAreaHint => !string.IsNullOrEmpty(EntertainmentAreaHint);

    public void SyncFromPluginConfig(HuePluginConfig config)
    {
        RunOnUi(() =>
        {
            _suppressRuleSave = true;
            try
            {
                BridgeIpInput = config.BridgeIp;
                UseHttps = config.UseHttps;
                IsDemoMode = config.DemoMode;
                IsPaired = !string.IsNullOrWhiteSpace(config.BridgeIp) && !string.IsNullOrWhiteSpace(config.ApplicationKey);
                SyncWithSmartScenes = config.Automation.SyncWithSmartScenes;
                TurnOnWithReceiverPower = config.Automation.TurnOnWithReceiverPower;
                TurnOffWithReceiverStandby = config.Automation.TurnOffWithReceiverStandby;
                CinemaPauseLights = config.Automation.CinemaPauseLights;
                _automationTargetRoomId = config.Automation.TargetRoomId;

                var sync = config.MusicSync ?? new HueMusicSyncConfig();
                IsMusicSyncEnabled = sync.Enabled;
                MusicSyncMode = sync.Mode;
                MusicSyncSensitivity = sync.Sensitivity;
                MusicSyncMinBrightness = sync.MinBrightness;
                MusicSyncMaxBrightness = sync.MaxBrightness;

                RaisePropertyChanged(nameof(AutomationTargetRoomLabel));
                UpdateConnectionBadge();
            }
            finally
            {
                _suppressRuleSave = false;
            }
        });
    }

    public void ApplyLiveLightPreviews(IReadOnlyList<HueLightState> lights)
    {
        RunOnUi(() =>
        {
            _allLightsCache = lights;
            foreach (var vm in VisibleLights)
            {
                var match = lights.FirstOrDefault(l => l.Id == vm.Id);
                if (match is not null)
                {
                    vm.ApplyState(match);
                }
            }
        });
    }

    public void UpdateLiveAudioMeters(double bass, double mid, double treble, bool isBeat, bool isLiveSignal)
    {
        var now = Environment.TickCount64;
        if (!isBeat && now - _lastMeterUiTickMs < 33)
        {
            return;
        }

        _lastMeterUiTickMs = now;
        RunOnUi(() =>
        {
            LiveBassPercent = Math.Round(Math.Clamp(bass * 100.0, 0.0, 100.0), 1);
            LiveMidPercent = Math.Round(Math.Clamp(mid * 100.0, 0.0, 100.0), 1);
            LiveTreblePercent = Math.Round(Math.Clamp(treble * 100.0, 0.0, 100.0), 1);
            LiveBeatFlash = isBeat;
            HasLiveAudioSignal = _liveSignalHold.Update(isLiveSignal, now);
        });
    }

    public void ApplyAlbumArtPalette(string dominantHex, string secondaryHex, string trackTitle)
    {
        RunOnUi(() =>
        {
            AlbumArtDominantHex = dominantHex;
            AlbumArtSecondaryHex = secondaryHex;
            AlbumArtTrackLabel = string.IsNullOrWhiteSpace(trackTitle)
                ? $"Palette {dominantHex} / {secondaryHex}"
                : $"{trackTitle} ({dominantHex})";
        });
    }

    public void ApplyEntertainmentAreas(IReadOnlyList<HueEntertainmentArea> areas)
    {
        RunOnUi(() =>
        {
            // Show the area the plugin streams to. A saved id that is not in the list stays saved until the user
            // picks an area.
            var savedId = _plugin.Config.MusicSync.SelectedEntertainmentAreaId;
            var inUse = PhilipsHuePlugin.ResolveEntertainmentArea(areas, savedId);

            _suppressAreaSave = true;
            try
            {
                EntertainmentAreas.Clear();
                foreach (var area in areas)
                {
                    EntertainmentAreas.Add(new HueEntertainmentAreaOption(area));
                }

                SelectedEntertainmentArea = inUse is null
                    ? null
                    : EntertainmentAreas.FirstOrDefault(o => string.Equals(o.Id, inUse.Id, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressAreaSave = false;
            }

            RaisePropertyChanged(nameof(HasEntertainmentAreas));
            UpdateEntertainmentAreaText(savedId);
        });
    }

    /// <summary>Sets the header badge and the hint under the picker from the selected area.</summary>
    private void UpdateEntertainmentAreaText(string savedId)
    {
        var area = SelectedEntertainmentArea;
        EntertainmentAreaSummary = area is not null
            ? $"Hue Entertainment Ready: {area.Name} ({area.ChannelCount} ch)"
            : "LAN Fast-Sync Engine Ready";

        if (area is null)
        {
            EntertainmentAreaHint = "No Entertainment areas — create one in the Hue app. Music Sync uses LAN sync.";
        }
        else if (!string.IsNullOrWhiteSpace(savedId) && !string.Equals(savedId, area.Id, StringComparison.OrdinalIgnoreCase))
        {
            EntertainmentAreaHint = $"The saved area is gone. Music Sync uses '{area.Name}' until you pick an area.";
        }
        else
        {
            EntertainmentAreaHint = string.Empty;
        }
    }

    private async Task SelectEntertainmentAreaAsync(HueEntertainmentAreaOption area)
    {
        try
        {
            await _plugin.SelectEntertainmentAreaAsync(area.Id);
            RunOnUi(() => StatusMessage = $"Music Sync uses the Entertainment area '{area.Name}'.");
        }
        catch (Exception ex)
        {
            ShowCommandError(ex);
        }
    }

    public void SetDemoBeatState(bool active) => RunOnUi(() => IsDemoBeatActive = active);

    public void ApplyBridgeState(
        IReadOnlyList<HueRoomGroup> rooms,
        IReadOnlyList<HueLightState> lights,
        IReadOnlyList<HueSceneItem> scenes,
        string? preferredRoomId = null)
    {
        RunOnUi(() =>
        {
            _allLightsCache = lights;
            _allScenesCache = scenes;

            var activeRoomId = !string.IsNullOrWhiteSpace(preferredRoomId)
                ? preferredRoomId
                : !string.IsNullOrWhiteSpace(SelectedRoom?.Id)
                    ? SelectedRoom.Id
                    : !string.IsNullOrWhiteSpace(_plugin.Config.SelectedRoomId)
                        ? _plugin.Config.SelectedRoomId
                        : "0";

            Rooms.Clear();

            // Always include an "All Lights" chip first so every light on the Bridge is accessible in 1 click
            var allAnyOn = lights.Any(l => l.IsOn);
            var allAllOn = lights.Count > 0 && lights.All(l => l.IsOn);
            var allAvgBri = lights.Count > 0 ? (int)Math.Round(lights.Average(l => l.BrightnessPercent)) : 0;
            Rooms.Add(new HueRoomChipViewModel(new HueRoomGroup(
                "0",
                "All Lights",
                "All",
                "All",
                allAnyOn,
                allAllOn,
                allAvgBri,
                lights.Select(static l => l.Id).ToList())));

            foreach (var r in rooms)
            {
                Rooms.Add(new HueRoomChipViewModel(r));
            }

            var toSelect = Rooms.FirstOrDefault(r => r.Id == activeRoomId) ?? Rooms.FirstOrDefault();
            SelectRoomCore(toSelect, saveSelection: false);
            RaisePropertyChanged(nameof(AutomationTargetRoomLabel));
            UpdateConnectionBadge();
        });
    }

    public void NotifyAutomationEvent(string message)
    {
        RunOnUi(() => LastAutomationEventText = $"{DateTime.Now:HH:mm:ss} • {message}");
    }

    private IReadOnlyList<HueLightState> _allLightsCache = [];
    private IReadOnlyList<HueSceneItem> _allScenesCache = [];

    private void SelectRoom(HueRoomChipViewModel room)
    {
        SelectRoomCore(room, saveSelection: true);
    }

    private void SelectRoomCore(HueRoomChipViewModel? room, bool saveSelection)
    {
        foreach (var r in Rooms)
        {
            r.IsSelected = ReferenceEquals(r, room);
        }

        SelectedRoom = room;
        _suppressSliderPush = true;
        try
        {
            SelectedRoomBrightness = room?.BrightnessPercent ?? 65;
        }
        finally
        {
            _suppressSliderPush = false;
        }

        VisibleLights.Clear();
        var filteredLights = room is null || room.Id == "0"
            ? _allLightsCache
            : _allLightsCache.Where(l => room.LightIds.Contains(l.Id)).ToList();

        foreach (var light in filteredLights)
        {
            VisibleLights.Add(new HueLightViewModel(
                light,
                onToggle: HandleLightToggleAsync,
                onBrightnessChanged: HandleLightBrightnessAsync,
                onColorTempChanged: HandleLightColorTempAsync,
                onPresetApplied: HandleLightPresetAsync,
                onError: ShowCommandError));
        }

        VisibleScenes.Clear();
        var filteredScenes = room is null || room.Id == "0"
            ? _allScenesCache.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(12) // The same scene name in several rooms: once.
            : _allScenesCache.Where(s => string.Equals(s.GroupId, room.Id, StringComparison.OrdinalIgnoreCase)).Take(12);

        foreach (var scene in filteredScenes)
        {
            VisibleScenes.Add(scene);
        }

        RaisePropertyChanged(nameof(HasVisibleScenes));
        RaisePropertyChanged(nameof(SelectedRoomHeader));
        RaisePropertyChanged(nameof(SelectedRoomToggleLabel));
        RaisePropertyChanged(nameof(IndividualLightsHeader));
        RaisePropertyChanged(nameof(IsFilteredToSingleRoom));

        if (saveSelection && room is not null)
        {
            _ = _plugin.SaveSelectedRoomAsync(room.Id);
        }
    }

    private async Task DiscoverBridgeAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning local network & Hue discovery service for Bridges...";
        try
        {
            var bridges = await _plugin.DiscoverBridgesAsync();
            RunOnUi(() =>
            {
                DiscoveredBridges.Clear();
                foreach (var b in bridges)
                {
                    DiscoveredBridges.Add(b);
                }

                RaisePropertyChanged(nameof(HasDiscoveredBridges));
                if (bridges.Count > 0)
                {
                    BridgeIpInput = bridges[0].IpAddress;
                    StatusMessage = $"Found {bridges.Count} Hue Bridge(s) ({bridges[0].IpAddress} via {bridges[0].Source}). Click 'Press Bridge Button & Pair'.";
                }
                else
                {
                    StatusMessage = "No Hue Bridge found automatically. Enter your Bridge IP manually or try Demo Mode.";
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartPairingAsync()
    {
        var ip = BridgeIpInput.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            StatusMessage = "Enter a Philips Hue Bridge IP address first.";
            return;
        }

        CancelPairing();
        _pairingCts = new CancellationTokenSource();
        var token = _pairingCts.Token;

        IsPairing = true;
        StatusMessage = $"Connecting to Hue Bridge at {ip}...";

        try
        {
            for (var remaining = 30; remaining > 0 && !token.IsCancellationRequested; remaining -= 2)
            {
                PairingHintText = $"Press the round blue link button on your Hue Bridge now ({remaining}s remaining)...";
                var result = await _plugin.TryPairBridgeAsync(ip, UseHttps, token);
                if (result.Success)
                {
                    PairingHintText = string.Empty;
                    StatusMessage = "Paired! Loading rooms, lights, and scenes...";
                    await RefreshStateAsync();
                    return;
                }

                if (!result.LinkButtonNotPressed)
                {
                    StatusMessage = result.Message;
                    PairingHintText = string.Empty;
                    return;
                }

                await Task.Delay(2000, token);
            }

            if (!token.IsCancellationRequested)
            {
                StatusMessage = "Pairing timed out — press the link button on your Hue Bridge and try again.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Pairing canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Pairing failed: {ex.Message}";
        }
        finally
        {
            IsPairing = false;
            PairingHintText = string.Empty;
        }
    }

    private void CancelPairing()
    {
        _pairingCts?.Cancel();
        _pairingCts?.Dispose();
        _pairingCts = null;
        IsPairing = false;
        PairingHintText = string.Empty;
    }

    private async Task ToggleDemoModeAsync()
    {
        var nextDemo = !IsDemoMode;
        await _plugin.SetDemoModeAsync(nextDemo);
        StatusMessage = nextDemo
            ? "Demo Bridge active — test rooms, color swatches, brightness, and Denon Smart Scene sync live!"
            : "Exited Demo Mode. Discover or enter your real Hue Bridge IP to pair.";
    }

    private async Task DisconnectBridgeAsync()
    {
        CancelPairing();
        await _plugin.DisconnectBridgeAsync();
        RunOnUi(() =>
        {
            Rooms.Clear();
            VisibleLights.Clear();
            VisibleScenes.Clear();
            SelectedRoom = null;
            RaisePropertyChanged(nameof(HasVisibleScenes));
            StatusMessage = "Disconnected from Hue Bridge. Ready to pair or enable Demo Mode.";
        });
    }

    private async Task RefreshStateAsync()
    {
        IsBusy = true;
        try
        {
            await _plugin.RefreshBridgeStateAsync();
            StatusMessage = IsDemoMode
                ? $"Demo Hue Bridge active • {Rooms.Count} rooms, {_allLightsCache.Count} virtual lights."
                : $"Synced with Hue Bridge ({BridgeIpInput}) at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteQuickGroupAsync(string groupId, bool? isOn = null, int? brightnessPercent = null)
    {
        try
        {
            await _plugin.SetGroupStateAsync(groupId, isOn: isOn, brightnessPercent: brightnessPercent);
            StatusMessage = isOn == false
                ? "All Hue lights turned off."
                : brightnessPercent.HasValue
                    ? $"All Hue lights set to {brightnessPercent.Value}%."
                    : "All Hue lights turned on.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hue command failed: {ex.Message}";
        }
    }

    private async Task ToggleSelectedRoomAsync()
    {
        if (SelectedRoom is null)
        {
            return;
        }

        var nextOn = !SelectedRoom.AnyOn;
        try
        {
            await _plugin.SetGroupStateAsync(SelectedRoom.Id, isOn: nextOn);
            StatusMessage = $"{SelectedRoom.Name} turned {(nextOn ? "on" : "off")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Room toggle failed: {ex.Message}";
        }
    }

    private async Task ApplyPresetToSelectedRoomAsync(HueColorPreset preset)
    {
        var groupId = SelectedRoom?.Id ?? "0";
        var targetName = SelectedRoom?.Name ?? "All Lights";
        try
        {
            await _plugin.ApplyColorPresetAsync(groupId, preset);
            StatusMessage = $"Applied '{preset.Name}' ({preset.BrightnessPercent}%) to {targetName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preset failed: {ex.Message}";
        }
    }

    private async Task ActivateHueSceneAsync(HueSceneItem scene)
    {
        var groupId = !string.IsNullOrWhiteSpace(scene.GroupId) ? scene.GroupId : (SelectedRoom?.Id ?? "0");
        try
        {
            await _plugin.ActivateNativeSceneAsync(groupId, scene.Id);
            StatusMessage = $"Activated Hue scene '{scene.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scene failed: {ex.Message}";
        }
    }

    private async Task HandleLightToggleAsync(HueLightViewModel light, bool nextOn)
    {
        try
        {
            await _plugin.SetLightStateAsync(light.Id, isOn: nextOn);
            StatusMessage = $"{light.Name} turned {(nextOn ? "on" : "off")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not toggle {light.Name}: {ex.Message}";
        }
    }

    private async Task HandleLightBrightnessAsync(HueLightViewModel light, int percent)
    {
        try
        {
            await _plugin.SetLightStateAsync(light.Id, isOn: percent > 0, brightnessPercent: percent);
        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusMessage = $"Brightness update failed: {ex.Message}");
        }
    }

    private async Task HandleLightColorTempAsync(HueLightViewModel light, int mirek)
    {
        try
        {
            await _plugin.SetLightStateAsync(light.Id, isOn: true, colorTempMirek: mirek);
        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusMessage = $"Color temperature update failed: {ex.Message}");
        }
    }

    private async Task HandleLightPresetAsync(HueLightViewModel light, HueColorPreset preset)
    {
        try
        {
            await _plugin.SetLightStateAsync(
                light.Id,
                isOn: true,
                brightnessPercent: preset.BrightnessPercent,
                colorTempMirek: light.SupportsColor ? preset.ColorTempMirek : (preset.ColorTempMirek ?? 366),
                x: light.SupportsColor ? preset.X : null,
                y: light.SupportsColor ? preset.Y : null);
            StatusMessage = $"Applied '{preset.Name}' to {light.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Color update failed: {ex.Message}";
        }
    }

    private void DebounceRoomBrightness(string roomId, int percent)
    {
        _roomBriDebounce?.Cancel();
        _roomBriDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _roomBriDebounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, cts.Token);
                await _plugin.SetGroupStateAsync(roomId, isOn: percent > 0, brightnessPercent: percent);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RunOnUi(() => StatusMessage = $"Room dimmer failed: {ex.Message}");
            }
        });
    }

    private async Task SetAutomationTargetToSelectedRoomAsync()
    {
        _automationTargetRoomId = SelectedRoom?.Id ?? string.Empty;
        RaisePropertyChanged(nameof(AutomationTargetRoomLabel));
        await SaveAutomationRulesAsync();
        StatusMessage = $"AVR lighting automations now target: {AutomationTargetRoomLabel}.";
    }

    private Task SaveAutomationRulesAsync() =>
        _plugin.UpdateAutomationRulesAsync(new HueAutomationRules
        {
            SyncWithSmartScenes = SyncWithSmartScenes,
            TurnOnWithReceiverPower = TurnOnWithReceiverPower,
            TurnOffWithReceiverStandby = TurnOffWithReceiverStandby,
            CinemaPauseLights = CinemaPauseLights,
            TargetRoomId = _automationTargetRoomId,
            // No control sets these: keep what the config has.
            PauseBrightenPercent = _plugin.Config.Automation.PauseBrightenPercent,
            ResumeDimPercent = _plugin.Config.Automation.ResumeDimPercent,
        });

    private async Task ToggleMusicSyncAsync()
    {
        var next = !IsMusicSyncEnabled;
        await _plugin.SetMusicSyncEnabledAsync(next);
        StatusMessage = next
            ? $"Hue Music Sync on ({MusicSyncMode}): follows what the Denon plays from the app (radio, YouTube, jukebox, PC Cast), PC audio, or the room mic when it is on."
            : "Hue Music Sync paused.";
    }

    private async Task SetMusicSyncModeAsync(HueMusicSyncMode mode)
    {
        MusicSyncMode = mode;
        await SaveMusicSyncConfigAsync();
        StatusMessage = $"Hue Music Sync mode switched to {mode}.";
    }

    private Task SaveMusicSyncConfigAsync() =>
        _plugin.UpdateMusicSyncConfigAsync(new HueMusicSyncConfig
        {
            Enabled = IsMusicSyncEnabled,
            Mode = MusicSyncMode,
            Sensitivity = MusicSyncSensitivity,
            MinBrightness = MusicSyncMinBrightness,
            MaxBrightness = MusicSyncMaxBrightness,
            // The area picker saves this through the plugin: keep what the config has.
            SelectedEntertainmentAreaId = _plugin.Config.MusicSync.SelectedEntertainmentAreaId,
        });

    private void UpdateConnectionBadge()
    {
        if (IsDemoMode)
        {
            ConnectionBadgeText = "DEMO SIMULATOR";
            ConnectionBadgeTone = "AccentCyanBright";
        }
        else if (IsPaired)
        {
            ConnectionBadgeText = $"CONNECTED • {BridgeIpInput}";
            ConnectionBadgeTone = "AccentEmerald";
        }
        else
        {
            ConnectionBadgeText = "SETUP REQUIRED";
            ConnectionBadgeTone = "AccentAmber";
        }
    }

    private void RefreshCommandStates()
    {
        RefreshStateCommand.RaiseCanExecuteChanged();
        AllLightsOnCommand.RaiseCanExecuteChanged();
        AllLightsOffCommand.RaiseCanExecuteChanged();
        SetAllBrightnessCommand.RaiseCanExecuteChanged();
        ToggleSelectedRoomCommand.RaiseCanExecuteChanged();
        ApplyMoodPresetCommand.RaiseCanExecuteChanged();
        ActivateHueSceneCommand.RaiseCanExecuteChanged();
        ToggleMusicSyncCommand.RaiseCanExecuteChanged();
        SetMusicSyncModeCommand.RaiseCanExecuteChanged();
        ToggleDemoBeatCommand.RaiseCanExecuteChanged();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.InvokeAsync(action);
        }
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
