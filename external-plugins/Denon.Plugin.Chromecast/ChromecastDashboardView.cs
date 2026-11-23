using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Denon.Plugin.Chromecast.Cast;

namespace Denon.Plugin.Chromecast;

/// <summary>
/// Plugin dashboard, built in code (no XAML) to keep the external plugin to one small DLL.
/// Colors follow the built-in Smart TV plugin.
/// </summary>
internal sealed class ChromecastDashboardView : UserControl
{
    private static readonly Brush CardBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x1D, 0x29)));
    private static readonly Brush InsetBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x17, 0x26)));
    private static readonly Brush BorderLineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x44)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)));
    private static readonly Brush TitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));

    private static readonly string[] InputCodes = ["", "TV", "BD", "GAME", "MPLAY", "SAT/CBL", "AUX1", "NET"];
    private static readonly string[] SoundModeCodes = ["", "AUTO", "MOVIE", "MUSIC", "STEREO", "DIRECT", "PURE DIRECT", "MCH STEREO"];

    private readonly ChromecastPlugin _plugin;
    private readonly StackPanel _deviceList = new();
    private readonly TextBlock _status = new() { Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, DeviceCard> _cards = new();

    public ChromecastDashboardView(ChromecastPlugin plugin)
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

        var header = new DockPanel { LastChildFill = true };
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Google Cast devices", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = TitleBrush });
        title.Children.Add(new TextBlock
        {
            Text = _plugin.HasReceiverControl
                ? "Found by mDNS. Each device can drive the Denon when an app starts or stops."
                : "Found by mDNS. Automations are off: this Denon Desktop version has no receiver-control API.",
            Foreground = DimBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(title);
        root.Children.Add(header);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var discover = MakeButton("Discover now", primary: true);
        discover.Click += async (_, _) => await RunAsync("Discovering…", async () =>
        {
            var added = await _plugin.DiscoverNowAsync();
            return $"Discovery finished. {added} new device(s).";
        });
        toolbar.Children.Add(discover);

        var hostBox = MakeTextBox(140, "192.168.1.x");
        toolbar.Children.Add(hostBox);
        var add = MakeButton("Add by IP");
        add.Click += async (_, _) => await RunAsync("Adding…", async () =>
        {
            var config = await _plugin.AddManualDeviceAsync(hostBox.Text, null);
            hostBox.Text = string.Empty;
            return $"Added {config.Host}.";
        });
        toolbar.Children.Add(add);

        var automations = new CheckBox
        {
            Content = "Automations enabled",
            IsChecked = _plugin.AutomationsEnabled,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
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

    private void OnDeviceStateChanged(string deviceId, CastDeviceState state) =>
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
                Text = "No devices yet. Press \"Discover now\", or add a device by IP.",
                Foreground = MutedBrush,
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

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Content = text,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(0, 0, 6, 6),
        Background = primary ? AccentBrush : InsetBrush,
        Foreground = primary ? Brushes.Black : TextBrush,
        BorderBrush = BorderLineBrush,
        Cursor = System.Windows.Input.Cursors.Hand,
    };

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

    // The host has no ComboBox theme, so the default control is white. This dark template is
    // parsed once from a XAML string to keep the plugin free of .xaml files.
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
            return null; // Fall back to the default control.
        }
    });

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class DeviceCard
    {
        private readonly ChromecastDashboardView _owner;
        private readonly CastDeviceConfig _config;
        private readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _connection = new() { Foreground = DimBrush };
        private readonly TextBlock _app = new() { Foreground = AccentBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _title = new() { Foreground = TextBrush, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _subtitle = new() { Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _volume = new() { Foreground = DimBrush, Margin = new Thickness(0, 4, 0, 0) };
        private readonly Image _art = new() { Width = 96, Height = 96, Stretch = Stretch.UniformToFill };
        private readonly Button _playPause;
        private string? _artUrl;

        public DeviceCard(ChromecastDashboardView owner, CastDeviceConfig config, string[] scenes)
        {
            _owner = owner;
            _config = config;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var artFrame = new Border
            {
                Width = 96,
                Height = 96,
                CornerRadius = new CornerRadius(8),
                Background = InsetBrush,
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Grid
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "No artwork",
                            FontSize = 10.5,
                            Foreground = DimBrush,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        _art,
                    },
                },
            };
            grid.Children.Add(artFrame);

            var info = new StackPanel();
            Grid.SetColumn(info, 1);
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(_dot);
            nameRow.Children.Add(new TextBlock { Text = config.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TitleBrush });
            info.Children.Add(nameRow);
            info.Children.Add(new TextBlock
            {
                Text = $"{config.Host}:{config.Port}" + (string.IsNullOrEmpty(config.Model) ? "" : $" · {config.Model}") + $" · {config.Source}",
                Foreground = DimBrush,
            });
            info.Children.Add(_connection);
            info.Children.Add(_app);
            info.Children.Add(_title);
            info.Children.Add(_subtitle);
            info.Children.Add(_volume);

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _playPause = AddCommand(buttons, "Play / Pause", c => c.TogglePlayAsync());
            AddCommand(buttons, "Stop", c => c.StopMediaAsync());
            AddCommand(buttons, "Vol −", c => c.SetVolumeAsync(c.State.VolumeLevel - 0.05));
            AddCommand(buttons, "Vol +", c => c.SetVolumeAsync(c.State.VolumeLevel + 0.05));
            AddCommand(buttons, "Mute", c => c.SetMutedAsync(!c.State.IsMuted));
            AddCommand(buttons, "Quit app", c => c.QuitAppAsync());
            info.Children.Add(buttons);
            info.Children.Add(BuildAutomation(scenes));
            grid.Children.Add(info);

            Root = new Border
            {
                Background = CardBrush,
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Child = grid,
            };
        }

        public Border Root { get; }

        public void Apply(CastDeviceState state)
        {
            _dot.Fill = state.Connection switch
            {
                CastConnectionState.Connected => OkBrush,
                CastConnectionState.Connecting => AccentBrush,
                _ => _config.Enabled ? ErrorBrush : DimBrush,
            };
            _connection.Text = !_config.Enabled
                ? "Disabled"
                : state.Connection switch
                {
                    CastConnectionState.Connected => "Connected",
                    CastConnectionState.Connecting => "Connecting…",
                    _ => "Not reachable" + (string.IsNullOrEmpty(state.Error) ? "" : $": {state.Error}"),
                };

            _app.Text = state.HasActiveApp
                ? $"{state.AppName}" + (string.IsNullOrEmpty(state.PlayerState) ? "" : $" · {state.PlayerState.ToLowerInvariant()}")
                : state.Connection == CastConnectionState.Connected ? "Idle" + (state.IsIdleScreen ? $" ({state.AppName})" : "") : "";
            _title.Text = state.Title ?? (state.HasActiveApp ? state.StatusText ?? "" : "");
            _subtitle.Text = state.Subtitle ?? "";
            _volume.Text = state.Connection == CastConnectionState.Connected
                ? $"Volume {Math.Round(state.VolumeLevel * 100)}" + (state.IsMuted ? " (muted)" : "")
                : "";
            _playPause.Content = state.IsPlaying ? "Pause" : "Play";

            if (state.ImageUrl != _artUrl)
            {
                _artUrl = state.ImageUrl;
                _art.Source = LoadImage(_artUrl);
            }
        }

        private Button AddCommand(Panel panel, string text, Func<CastDeviceClient, Task> command)
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

        private Expander BuildAutomation(string[] scenes)
        {
            var a = _config.Automation;
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var enabled = MakeCheck("Device enabled", _config.Enabled);
            var powerOn = MakeCheck("Power on the Denon when an app starts", a.PowerOnReceiver);
            var pause = MakeCheck("Pause this device when the Denon powers off", a.PauseWhenReceiverPowersOff);
            panel.Children.Add(enabled);
            panel.Children.Add(powerOn);
            panel.Children.Add(pause);

            var fields = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var input = MakeCombo(InputCodes, a.ReceiverInput);
            var sound = MakeCombo(SoundModeCodes, a.SoundMode);
            var scene = MakeCombo(scenes, a.SceneOnStart);
            var idle = MakeTextBox(50, "Minutes. 0 = never.");
            idle.Text = a.PowerOffAfterIdleMinutes.ToString();
            var ignored = MakeTextBox(180, "App names, comma separated");
            ignored.Text = string.Join(", ", a.IgnoredApps);
            fields.Children.Add(Labeled("Denon input", input));
            fields.Children.Add(Labeled("Sound mode", sound));
            fields.Children.Add(Labeled("Smart Scene", scene));
            fields.Children.Add(Labeled("Idle power-off (min)", idle));
            fields.Children.Add(Labeled("Ignore apps", ignored));
            panel.Children.Add(fields);

            var actions = new WrapPanel();
            var save = MakeButton("Save", primary: true);
            save.Click += async (_, _) => await _owner.RunAsync("Saving…", async () =>
            {
                if (!int.TryParse(idle.Text.Trim(), out var minutes) || minutes < 0 || minutes > 720)
                {
                    throw new ArgumentException("Idle power-off must be 0–720 minutes.");
                }

                var automation = new CastAutomationConfig
                {
                    PowerOnReceiver = powerOn.IsChecked == true,
                    PauseWhenReceiverPowersOff = pause.IsChecked == true,
                    ReceiverInput = input.Text.Trim().ToUpperInvariant(),
                    SoundMode = sound.Text.Trim().ToUpperInvariant(),
                    SceneOnStart = scene.Text.Trim(),
                    PowerOffAfterIdleMinutes = minutes,
                    IgnoredApps = ignored.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                };
                await _owner._plugin.SaveDeviceSettingsAsync(_config.Id, enabled.IsChecked == true, automation);
                return $"Saved settings for {_config.Name}.";
            });
            actions.Children.Add(save);
            var remove = MakeButton("Remove device");
            remove.Click += async (_, _) => await _owner.RunAsync("Removing…", async () =>
            {
                await _owner._plugin.RemoveDeviceAsync(_config.Id);
                return $"Removed {_config.Name}. Discovery can add it again.";
            });
            actions.Children.Add(remove);
            panel.Children.Add(actions);

            return new Expander
            {
                Header = new TextBlock { Text = "Denon automation", Foreground = MutedBrush },
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

        private static ImageSource? LoadImage(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = uri;
                image.DecodePixelWidth = 192;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
