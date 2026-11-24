using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Mrp;

namespace Denon.Plugin.AppleTv;

/// <summary>
/// Plugin dashboard, built in code (no XAML) to keep the plugin to one small DLL plus BouncyCastle.
/// Colors and controls follow the Chromecast plugin.
/// </summary>
internal sealed class AppleTvDashboardView : UserControl
{
    // Read at each use, never cached: on a theme switch the host points existing elements to the new brushes.
    private static Brush CardBrush => HostTheme.Brush("CardElevated");
    private static Brush BorderLineBrush => HostTheme.Brush("Ink12");
    private static Brush TextBrush => HostTheme.Brush("TextStrong");
    private static Brush TitleBrush => HostTheme.Brush("TextPrimary");
    private static Brush MutedBrush => HostTheme.Brush("TextMuted");
    private static Brush DimBrush => HostTheme.Brush("TextFaint");
    private static Brush AccentBrush => HostTheme.Brush("TextSoft");
    private static Brush WarnBrush => HostTheme.Brush("AccentAmber");
    private static Brush OkBrush => HostTheme.Brush("AccentEmeraldBright");
    private static Brush ErrorBrush => HostTheme.Brush("AccentCrimsonBright");

    private static readonly string[] InputCodes = ["", "TV", "BD", "GAME", "MPLAY", "SAT/CBL", "AUX1", "NET"];
    private static readonly string[] SoundModeCodes = ["", "AUTO", "MOVIE", "MUSIC", "STEREO", "DIRECT", "PURE DIRECT", "MCH STEREO"];

    private readonly AppleTvPlugin _plugin;
    private readonly StackPanel _deviceList = new();
    private readonly TextBlock _status = new() { Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, DeviceCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    public AppleTvDashboardView(AppleTvPlugin plugin)
    {
        _plugin = plugin;
        Content = HostSkin.Follow(BuildLayout());
        Loaded += (_, _) =>
        {
            _plugin.DevicesChanged += OnDevicesChanged;
            _plugin.DeviceStateChanged += OnDeviceStateChanged;
            RebuildDevices();
        };
        Unloaded += (_, _) =>
        {
            _plugin.DevicesChanged -= OnDevicesChanged;
            _plugin.DeviceStateChanged -= OnDeviceStateChanged;
        };
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(4) };
        root.Children.Add(new TextBlock { Text = "Apple TV", FontWeight = FontWeights.SemiBold, Foreground = TitleBrush }.Token(TextElement.FontSizeProperty, "FontSize11"));
        root.Children.Add(new TextBlock
        {
            Text = "Found by DNS-SD. Pair once with the PIN the TV shows. Each Apple TV can drive the Denon when it wakes or sleeps.",
            Foreground = DimBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var discover = MakeButton("Discover now", primary: true);
        discover.Click += async (_, _) => await RunAsync("Discovering…", async () =>
        {
            var added = await _plugin.DiscoverNowAsync();
            return $"Discovery finished. {added} new Apple TV(s).";
        });
        toolbar.Children.Add(discover);

        var automations = new CheckBox
        {
            Content = "Automations enabled",
            IsChecked = _plugin.AutomationsEnabled,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 6),
        };
        automations.Click += async (_, _) => await RunAsync("Saving…", async () =>
        {
            await _plugin.SetAutomationsEnabledAsync(automations.IsChecked == true);
            return automations.IsChecked == true ? "Automations enabled." : "Automations disabled.";
        });
        toolbar.Children.Add(automations);
        root.Children.Add(toolbar);
        root.Children.Add(_status);

        _deviceList.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_deviceList);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(RebuildDevices);

    private void OnDeviceStateChanged(string deviceId, AppleTvDeviceState state) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_cards.TryGetValue(deviceId, out var card))
            {
                card.Apply(state);
            }
        });

    private void RebuildDevices()
    {
        _deviceList.Children.Clear();
        _cards.Clear();
        var views = _plugin.GetDeviceViews();
        if (views.Count == 0)
        {
            _deviceList.Children.Add(new TextBlock
            {
                Text = "No Apple TV yet. Press \"Discover now\". The Apple TV and this PC must be on the same network.",
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var scenes = _plugin.GetReceiverScenes().Select(s => s.Name).Prepend("").ToArray();
        foreach (var (config, state) in views)
        {
            var card = new DeviceCard(this, config, scenes);
            card.Apply(state);
            _cards[config.Id] = card;
            _deviceList.Children.Add(HostSkin.Follow(card.Root));
        }
    }

    private async Task RunAsync(string busyText, Func<Task<string>> work)
    {
        _status.Foreground = MutedBrush;
        _status.Text = busyText;
        try
        {
            _status.Text = await work();
        }
        catch (Exception ex)
        {
            _status.Foreground = ErrorBrush;
            _status.Text = ex.Message;
        }
    }

    /// <summary>A button in the app's own style: <c>PrimaryButton</c> or <c>SecondaryButton</c> from the host theme.</summary>
    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Content = text,
        Margin = new Thickness(0, 0, 6, 6),
        Style = HostTheme.Style(primary ? "PrimaryButton" : "SecondaryButton"),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    private static TextBox MakeTextBox(double width, string tooltip) => new TextBox
    {
        Width = width,
        Margin = new Thickness(0, 0, 6, 6),
        Padding = new Thickness(8, 6, 8, 6), // same height as the app's drop-down boxes next to it
        HorizontalAlignment = HorizontalAlignment.Left,
        ToolTip = tooltip,
        VerticalContentAlignment = VerticalAlignment.Center,
    }.Token(TextElement.FontSizeProperty, "FontSize06");

    private static ComboBox MakeCombo(IEnumerable<string> items, string value)
    {
        var combo = new ComboBox
        {
            IsEditable = true,
            Width = 140,
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = "Empty = no change. You can also type a code.",
        };
        foreach (var item in items)
        {
            combo.Items.Add(item);
        }

        combo.Text = value;
        return combo;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class DeviceCard
    {
        private readonly AppleTvDashboardView _owner;
        private readonly AppleTvDeviceConfig _config;
        private readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _connection = new() { Foreground = DimBrush };
        private readonly TextBlock _app = new() { Foreground = AccentBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _title = new TextBlock { Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis }.Token(TextElement.FontSizeProperty, "FontSize10");
        private readonly TextBlock _subtitle = new() { Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button? _playPause;

        public DeviceCard(AppleTvDashboardView owner, AppleTvDeviceConfig config, string[] scenes)
        {
            _owner = owner;
            _config = config;

            var info = new StackPanel();
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(_dot);
            nameRow.Children.Add(new TextBlock { Text = config.Name, FontWeight = FontWeights.SemiBold, Foreground = TitleBrush }.Token(TextElement.FontSizeProperty, "FontSize10"));
            info.Children.Add(nameRow);
            info.Children.Add(new TextBlock
            {
                Text = config.Host +
                       (string.IsNullOrEmpty(config.Model) ? "" : $" · {config.Model}") +
                       (string.IsNullOrEmpty(config.OsVersion) ? "" : $" · tvOS {config.OsVersion}"),
                Foreground = DimBrush,
            });
            info.Children.Add(_connection);

            if (!config.IsPaired)
            {
                info.Children.Add(BuildPairing());
            }
            else
            {
                info.Children.Add(_app);
                info.Children.Add(_title);
                info.Children.Add(_subtitle);

                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                _playPause = AddCommand(buttons, "Play / Pause", c => c.State.IsPlaying ? c.PauseAsync(default) : c.PlayAsync(default));
                AddCommand(buttons, "Menu", c => c.PressAsync(HidCommand.Menu, default));
                AddCommand(buttons, "Home", c => c.PressAsync(HidCommand.Home, default));
                AddCommand(buttons, "Wake", c => c.SetPowerAsync(true, default));
                AddCommand(buttons, "Sleep", c => c.SetPowerAsync(false, default));
                info.Children.Add(buttons);
                info.Children.Add(BuildSettings(scenes));
            }

            Root = new Border
            {
                Background = CardBrush,
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Child = info,
            }.Token(Border.CornerRadiusProperty, "Radius04");
        }

        public Border Root { get; }

        public void Apply(AppleTvDeviceState state)
        {
            if (!_config.IsPaired)
            {
                _dot.Fill = DimBrush;
                _connection.Text = _owner._plugin.IsPairing(_config.Id) ? "Pairing: type the PIN the TV shows." : "Not paired.";
                return;
            }

            _dot.Fill = !_config.Enabled ? DimBrush
                : state.Link switch
                {
                    AppleTvLink.Connected => state.IsAwake ? OkBrush : WarnBrush,
                    AppleTvLink.Connecting => WarnBrush,
                    _ => ErrorBrush,
                };
            _connection.Text = !_config.Enabled
                ? "Disabled"
                : state.Link switch
                {
                    AppleTvLink.Connected => $"Connected · {PowerText(state.Power)}" + (state.NowPlayingLinked ? " · now playing linked" : ""),
                    AppleTvLink.Connecting => "Connecting…",
                    _ => "Not reachable" + (string.IsNullOrEmpty(state.Error) ? "" : $": {state.Error}"),
                };

            var np = state.NowPlaying;
            if (state.HasMedia)
            {
                _app.Text = (state.AppName ?? np.AppBundleId ?? "App") + $" · {np.State.ToString().ToLowerInvariant()}";
                _title.Text = np.Title ?? "";
                var series = np.SeriesName is null ? null
                    : np.Season is { } s && np.Episode is { } e ? $"{np.SeriesName} · S{s}E{e}" : np.SeriesName;
                _subtitle.Text = series ?? np.Subtitle ?? np.Artist ?? "";
            }
            else
            {
                _app.Text = state.Link == AppleTvLink.Connected && state.IsAwake ? "Nothing plays" : "";
                _title.Text = "";
                _subtitle.Text = "";
            }

            if (_playPause is not null)
            {
                _playPause.Content = state.IsPlaying ? "Pause" : "Play";
            }
        }

        private static string PowerText(AttentionState power) => power switch
        {
            AttentionState.Awake => "awake",
            AttentionState.Idle => "awake (idle)",
            AttentionState.Screensaver => "screensaver",
            AttentionState.Asleep => "asleep",
            _ => "power unknown",
        };

        private UIElement BuildPairing()
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var start = MakeButton("Pair", primary: true);
            var pin = MakeTextBox(70, "The 4-digit PIN on the TV");
            var finish = MakeButton("Send PIN");
            var cancel = MakeButton("Cancel");
            var pairing = _owner._plugin.IsPairing(_config.Id);
            pin.IsEnabled = finish.IsEnabled = cancel.IsEnabled = pairing;

            start.Click += async (_, _) => await _owner.RunAsync("Asking the Apple TV for a PIN…", async () =>
            {
                await _owner._plugin.StartPairingAsync(_config.Id);
                return $"{_config.Name} shows a PIN on the TV. Type it and press \"Send PIN\".";
            });
            finish.Click += async (_, _) => await _owner.RunAsync("Pairing…", async () =>
            {
                await _owner._plugin.FinishPairingAsync(_config.Id, pin.Text);
                return $"Paired with {_config.Name}.";
            });
            cancel.Click += async (_, _) => await _owner.RunAsync("Cancelling…", async () =>
            {
                await _owner._plugin.CancelPairingAsync(_config.Id);
                return "Pairing cancelled.";
            });

            panel.Children.Add(start);
            panel.Children.Add(pin);
            panel.Children.Add(finish);
            panel.Children.Add(cancel);
            var remove = MakeButton("Remove");
            remove.Click += async (_, _) => await _owner.RunAsync("Removing…", async () =>
            {
                await _owner._plugin.RemoveDeviceAsync(_config.Id);
                return $"Removed {_config.Name}. Discovery can add it again.";
            });
            panel.Children.Add(remove);
            return panel;
        }

        private Button AddCommand(Panel panel, string text, Func<AppleTvDeviceClient, Task> command)
        {
            var button = MakeButton(text);
            button.Click += async (_, _) => await _owner.RunAsync($"{text}…", async () =>
            {
                await _owner._plugin.RunDeviceCommandAsync(_config.Id, command);
                return $"{text}: sent to {_config.Name}.";
            });
            panel.Children.Add(button);
            return button;
        }

        private Expander BuildSettings(string[] scenes)
        {
            var a = _config.Automation;
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var enabled = MakeCheck("Device enabled", _config.Enabled);
            var powerOn = MakeCheck("Power on the Denon when the Apple TV wakes", a.PowerOnReceiver);
            var pause = MakeCheck("Pause the Apple TV when the Denon powers off", a.PauseWhenReceiverPowersOff);
            panel.Children.Add(enabled);
            panel.Children.Add(powerOn);
            panel.Children.Add(pause);

            var fields = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var input = MakeCombo(InputCodes, a.ReceiverInput);
            var sound = MakeCombo(SoundModeCodes, a.SoundMode);
            var scene = MakeCombo(scenes, a.SceneOnWake);
            var sleep = MakeTextBox(50, "Minutes. 0 = never.");
            sleep.Text = a.PowerOffAfterSleepMinutes.ToString();
            fields.Children.Add(Labeled("Denon input", input));
            fields.Children.Add(Labeled("Sound mode", sound));
            fields.Children.Add(Labeled("Smart Scene on wake", scene));
            fields.Children.Add(Labeled("Power off after sleep (min)", sleep));
            panel.Children.Add(fields);

            var actions = new WrapPanel();
            var save = MakeButton("Save", primary: true);
            save.Click += async (_, _) => await _owner.RunAsync("Saving…", async () =>
            {
                if (!int.TryParse(sleep.Text.Trim(), out var minutes) || minutes < 0 || minutes > 720)
                {
                    throw new ArgumentException("Power off after sleep must be 0–720 minutes.");
                }

                var automation = new AppleTvAutomationConfig
                {
                    PowerOnReceiver = powerOn.IsChecked == true,
                    PauseWhenReceiverPowersOff = pause.IsChecked == true,
                    ReceiverInput = input.Text.Trim().ToUpperInvariant(),
                    SoundMode = sound.Text.Trim().ToUpperInvariant(),
                    SceneOnWake = scene.Text.Trim(),
                    PowerOffAfterSleepMinutes = minutes,
                };
                await _owner._plugin.SaveDeviceSettingsAsync(_config.Id, enabled.IsChecked == true, automation);
                return $"Saved settings for {_config.Name}.";
            });
            actions.Children.Add(save);
            var unpair = MakeButton("Unpair");
            unpair.Click += async (_, _) => await _owner.RunAsync("Unpairing…", async () =>
            {
                await _owner._plugin.UnpairAsync(_config.Id);
                return $"Unpaired {_config.Name}. To remove it from the Apple TV too: Settings › Remotes and Devices.";
            });
            actions.Children.Add(unpair);
            panel.Children.Add(actions);

            return new Expander
            {
                Style = HostTheme.Style("DarkExpanderStyle"),
                Header = new TextBlock { Text = "Settings and Denon automation", Foreground = MutedBrush },
                Content = panel,
                Margin = new Thickness(0, 4, 0, 0),
                IsExpanded = false,
            };
        }

        private static CheckBox MakeCheck(string text, bool value) => new()
        {
            Content = text,
            IsChecked = value,
            Foreground = TextBrush,
            Margin = new Thickness(0, 2, 0, 2),
        };

        private static StackPanel Labeled(string label, FrameworkElement control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            panel.Children.Add(new TextBlock { Text = label, Foreground = DimBrush, Margin = new Thickness(0, 0, 0, 2) }.Token(TextElement.FontSizeProperty, "FontSize04"));
            panel.Children.Add(control);
            return panel;
        }
    }
}
