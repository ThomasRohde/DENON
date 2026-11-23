using System.Windows;
using System.Windows.Controls;
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
    private static readonly Brush CardBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x1D, 0x29)));
    private static readonly Brush InsetBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x17, 0x26)));
    private static readonly Brush BorderLineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x44)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)));
    private static readonly Brush TitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8)));
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));

    private static readonly string[] InputCodes = ["", "TV", "BD", "GAME", "MPLAY", "SAT/CBL", "AUX1", "NET"];
    private static readonly string[] SoundModeCodes = ["", "AUTO", "MOVIE", "MUSIC", "STEREO", "DIRECT", "PURE DIRECT", "MCH STEREO"];

    private readonly AppleTvPlugin _plugin;
    private readonly StackPanel _deviceList = new();
    private readonly TextBlock _status = new() { Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, DeviceCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    public AppleTvDashboardView(AppleTvPlugin plugin)
    {
        _plugin = plugin;
        Content = BuildLayout();
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
        root.Children.Add(new TextBlock { Text = "Apple TV", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = TitleBrush });
        root.Children.Add(new TextBlock
        {
            Text = _plugin.HasReceiverControl
                ? "Found by DNS-SD. Pair once with the PIN the TV shows. Each Apple TV can drive the Denon when it wakes or sleeps."
                : "Found by DNS-SD. Automations are off: this Denon Desktop version has no receiver-control API.",
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
            IsEnabled = _plugin.HasReceiverControl,
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
            _deviceList.Children.Add(card.Root);
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

    private static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Background = primary ? PrimaryBrush : InsetBrush,
            Foreground = primary ? Brushes.White : TextBrush,
            BorderBrush = primary ? PrimaryBrush : BorderLineBrush,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (DarkButtonStyle.Value is { } style)
        {
            button.Style = style;
        }

        return button;
    }

    private static readonly Brush PrimaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

    // The default Windows button template draws its own light chrome (hover, focus, disabled) over these colors,
    // which made the primary button unreadable. This template keeps the plugin's colors in every state.
    private static readonly Lazy<Style?> DarkButtonStyle = new(() =>
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="FocusVisualStyle" Value="{x:Null}" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="BorderBrush" Value="#38BDF8" />
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="Bd" Property="BorderBrush" Value="#38BDF8" />
                      </Trigger>
                      <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Bd" Property="Opacity" Value="0.75" />
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="Bd" Property="Opacity" Value="0.45" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        try
        {
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        catch
        {
            return null; // Fall back to the default control.
        }
    });

    private static TextBox MakeTextBox(double width, string tooltip) => new()
    {
        Width = width,
        Margin = new Thickness(0, 0, 6, 6),
        Padding = new Thickness(4, 3, 4, 3),
        Background = InsetBrush,
        Foreground = TextBrush,
        BorderBrush = BorderLineBrush,
        CaretBrush = TextBrush,
        ToolTip = tooltip,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static ComboBox MakeCombo(IEnumerable<string> items, string value)
    {
        var combo = new ComboBox
        {
            IsEditable = true,
            Width = 140,
            Margin = new Thickness(0, 0, 6, 6),
            Background = InsetBrush,
            Foreground = TextBrush,
            BorderBrush = BorderLineBrush,
            ToolTip = "Empty = no change. You can also type a code.",
        };
        if (DarkComboStyle.Value is { } style)
        {
            combo.Style = style;
        }

        foreach (var item in items)
        {
            combo.Items.Add(item);
        }

        combo.Text = value;
        return combo;
    }

    // The host has no ComboBox theme, so the default control is white (same template as the Chromecast plugin).
    private static readonly Lazy<Style?> DarkComboStyle = new(() =>
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
              <Setter Property="Background" Value="#0E1726" />
              <Setter Property="Foreground" Value="#E2E8F0" />
              <Setter Property="BorderBrush" Value="#2A3244" />
              <Setter Property="ItemContainerStyle">
                <Setter.Value>
                  <Style TargetType="ComboBoxItem">
                    <Setter Property="Foreground" Value="#E2E8F0" />
                    <Setter Property="MinHeight" Value="22" />
                    <Setter Property="Template">
                      <Setter.Value>
                        <ControlTemplate TargetType="ComboBoxItem">
                          <Border x:Name="Bd" Background="Transparent" Padding="8,3">
                            <ContentPresenter />
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                              <Setter TargetName="Bd" Property="Background" Value="#242A3A" />
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </Setter.Value>
                    </Setter>
                  </Style>
                </Setter.Value>
              </Setter>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ComboBox">
                    <Grid>
                      <ToggleButton Focusable="False" ClickMode="Press"
                                    IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                        <ToggleButton.Template>
                          <ControlTemplate TargetType="ToggleButton">
                            <Border x:Name="Bd" Background="#0E1726" BorderBrush="#2A3244" BorderThickness="1" CornerRadius="4">
                              <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,8,0"
                                    Data="M 0 0 L 4 4 L 8 0" Stroke="#94A3B8" StrokeThickness="1.5" />
                            </Border>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="BorderBrush" Value="#38BDF8" />
                              </Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </ToggleButton.Template>
                      </ToggleButton>
                      <ContentPresenter x:Name="ContentSite" IsHitTestVisible="False" Margin="8,3,24,3" VerticalAlignment="Center"
                                        Content="{TemplateBinding SelectionBoxItem}" />
                      <TextBox x:Name="PART_EditableTextBox" Visibility="Hidden" Margin="4,2,22,2" VerticalAlignment="Center"
                               Background="Transparent" BorderThickness="0" Foreground="#E2E8F0" CaretBrush="#E2E8F0"
                               IsReadOnly="{TemplateBinding IsReadOnly}" />
                      <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                             IsOpen="{TemplateBinding IsDropDownOpen}" PopupAnimation="Slide">
                        <Border Background="#181D29" BorderBrush="#2A3244" BorderThickness="1" CornerRadius="4"
                                MinWidth="{TemplateBinding ActualWidth}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                          <ScrollViewer>
                            <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                          </ScrollViewer>
                        </Border>
                      </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsEditable" Value="True">
                        <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible" />
                        <Setter TargetName="ContentSite" Property="Visibility" Value="Hidden" />
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.5" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        try
        {
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        catch
        {
            return null;
        }
    });

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
        private readonly TextBlock _title = new() { Foreground = TextBrush, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _subtitle = new() { Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button? _playPause;

        public DeviceCard(AppleTvDashboardView owner, AppleTvDeviceConfig config, string[] scenes)
        {
            _owner = owner;
            _config = config;

            var info = new StackPanel();
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(_dot);
            nameRow.Children.Add(new TextBlock { Text = config.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TitleBrush });
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
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Child = info,
            };
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
            panel.Children.Add(new TextBlock { Text = label, Foreground = DimBrush, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(control);
            return panel;
        }
    }
}
