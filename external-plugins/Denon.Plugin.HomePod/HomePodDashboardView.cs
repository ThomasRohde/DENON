using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Denon.Plugin.AppleTv;

namespace Denon.Plugin.HomePod;

/// <summary>
/// Plugin dashboard, built in code (no XAML) like the Apple TV plugin's, with the host theme and skin.
/// One card per speaker (a stereo pair is one): link, what plays, play/pause, previous, next, on/off.
/// </summary>
internal sealed class HomePodDashboardView : UserControl
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

    private readonly HomePodPlugin _plugin;
    private readonly StackPanel _speakerList = new();
    private readonly TextBlock _status = new() { Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, SpeakerCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    public HomePodDashboardView(HomePodPlugin plugin)
    {
        _plugin = plugin;
        Content = HostSkin.Follow(BuildLayout());
        Loaded += (_, _) =>
        {
            _plugin.SpeakersChanged += OnSpeakersChanged;
            _plugin.SpeakerStateChanged += OnSpeakerStateChanged;
            RebuildSpeakers();
        };
        Unloaded += (_, _) =>
        {
            _plugin.SpeakersChanged -= OnSpeakersChanged;
            _plugin.SpeakerStateChanged -= OnSpeakerStateChanged;
        };
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(4) };
        root.Children.Add(new TextBlock { Text = "HomePod", FontWeight = FontWeights.SemiBold, Foreground = TitleBrush }.Token(TextElement.FontSizeProperty, "FontSize11"));
        root.Children.Add(new TextBlock
        {
            Text = "Found by DNS-SD on your network. No PIN: the plugin links to each HomePod without pairing and stores " +
                   "nothing. A stereo pair is one speaker. Listening time shows in Insights.",
            Foreground = DimBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var discover = MakeButton("Discover now", primary: true);
        discover.Click += async (_, _) => await RunAsync("Discovering…", async () =>
        {
            var added = await _plugin.DiscoverNowAsync();
            return $"Discovery finished. {added} new speaker(s).";
        });
        toolbar.Children.Add(discover);
        toolbar.Children.Add(MakeCheck("Look for HomePods every 10 minutes", _plugin.AutoDiscover, async on =>
        {
            await _plugin.SetAutoDiscoverAsync(on);
            return on ? "Discovery runs every 10 minutes." : "Discovery runs only when you press Discover now.";
        }));
        toolbar.Children.Add(MakeCheck("Pause the HomePods when the Denon turns on", _plugin.PauseWhenReceiverPowersOn, async on =>
        {
            await _plugin.SetPauseWhenReceiverPowersOnAsync(on);
            return on ? "HomePods that play pause when the receiver turns on." : "The receiver no longer pauses the HomePods.";
        }));
        root.Children.Add(toolbar);
        root.Children.Add(_status);

        _speakerList.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_speakerList);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private CheckBox MakeCheck(string text, bool isChecked, Func<bool, Task<string>> onChange)
    {
        var box = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 6),
        };
        box.Click += async (_, _) => await RunAsync("Saving…", () => onChange(box.IsChecked == true));
        return box;
    }

    private void OnSpeakersChanged() => Dispatcher.BeginInvoke(RebuildSpeakers);

    private void OnSpeakerStateChanged(string speakerId, HomePodState state) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_cards.TryGetValue(speakerId, out var card))
            {
                card.Apply(state);
            }
        });

    private void RebuildSpeakers()
    {
        _speakerList.Children.Clear();
        _cards.Clear();
        var views = _plugin.GetSpeakerViews();
        if (views.Count == 0)
        {
            _speakerList.Children.Add(new TextBlock
            {
                Text = "No HomePod yet. Press \"Discover now\". The HomePods and this PC must be on the same network.",
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var (config, state) in views)
        {
            var card = new SpeakerCard(this, config);
            card.Apply(state);
            _cards[config.Id] = card;
            _speakerList.Children.Add(HostSkin.Follow(card.Root));
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

    private sealed class SpeakerCard
    {
        private readonly HomePodDashboardView _owner;
        private readonly HomePodSpeakerConfig _config;
        private readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _connection = new() { Foreground = DimBrush };
        private readonly TextBlock _app = new() { Foreground = AccentBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _title = new TextBlock { Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis }.Token(TextElement.FontSizeProperty, "FontSize10");
        private readonly TextBlock _subtitle = new() { Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button _playPause;
        private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, 8, 0, 0) };

        public SpeakerCard(HomePodDashboardView owner, HomePodSpeakerConfig config)
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
                       $" · {config.ModelName}" +
                       (string.IsNullOrEmpty(config.OsVersion) ? "" : $" · HomePod software {config.OsVersion}") +
                       (config.IsStereoPair ? $" · {string.Join(" and ", config.Members)}" : ""),
                Foreground = DimBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            info.Children.Add(_connection);
            info.Children.Add(_app);
            info.Children.Add(_title);
            info.Children.Add(_subtitle);

            AddCommand("Previous", "previous");
            _playPause = AddCommand("Play / Pause", "play-pause");
            AddCommand("Next", "next");
            info.Children.Add(_buttons);

            var enabled = new CheckBox
            {
                Content = "Linked by Denon Desktop",
                IsChecked = config.Enabled,
                Foreground = TextBrush,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "Off: the plugin does not connect to this speaker and logs no listening time for it.",
            };
            enabled.Click += async (_, _) => await _owner.RunAsync("Saving…", async () =>
            {
                await _owner._plugin.SetSpeakerEnabledAsync(_config.Id, enabled.IsChecked == true);
                return enabled.IsChecked == true ? $"{_config.Name} is linked." : $"{_config.Name} is no longer linked.";
            });
            info.Children.Add(enabled);

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

        private Button AddCommand(string text, string action)
        {
            var button = MakeButton(text);
            button.Click += async (_, _) => await _owner.RunAsync($"{_config.Name}: {text.ToLowerInvariant()}…", async () =>
            {
                await _owner._plugin.CommandAsync(_config.Id, action, CancellationToken.None);
                return $"{_config.Name}: {text.ToLowerInvariant()} sent.";
            });
            _buttons.Children.Add(button);
            return button;
        }

        public void Apply(HomePodState state)
        {
            _dot.Fill = !_config.Enabled ? DimBrush
                : state.Link switch
                {
                    HomePodLink.Connected => OkBrush,
                    HomePodLink.Connecting => WarnBrush,
                    _ => ErrorBrush,
                };
            _connection.Text = !_config.Enabled
                ? "Not linked (switched off below)"
                : state.Link switch
                {
                    HomePodLink.Connected => "Linked · now playing live",
                    HomePodLink.Connecting => "Linking…",
                    _ => "Not reachable" + (string.IsNullOrEmpty(state.Error) ? "" : $": {state.Error}"),
                };

            var np = state.NowPlaying;
            if (state.HasMedia)
            {
                _app.Text = $"{state.AppName} · {np.State.ToString().ToLowerInvariant()}";
                _title.Text = np.Title ?? "";
                _subtitle.Text = string.Join(" · ", new[] { np.Artist, np.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            else
            {
                _app.Text = state.Link == HomePodLink.Connected ? "Nothing plays" : "";
                _title.Text = "";
                _subtitle.Text = "";
            }

            _playPause.Content = state.IsPlaying ? "Pause" : "Play";
            _buttons.IsEnabled = _config.Enabled && state.Link == HomePodLink.Connected;
        }
    }
}
