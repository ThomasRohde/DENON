using System.Collections.ObjectModel;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Models;
using Denon.App.PluginSdk;

namespace Denon.App.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly List<HelpGuideTopic> _allHelpTopics = [];
    private HelpGuideTopic? _selectedHelpTopic;
    private string _helpSearchQuery = string.Empty;
    private string _helpCategoryFilter = "All";
    private string _helpReturnSection = string.Empty;
    private bool _isSetupChecklistExpanded = true;
    private int _setupChecklistCompletedCount;
    private int _setupChecklistTotalCount = 6;

    public ObservableCollection<HelpGuideTopic> FilteredHelpTopics { get; } = [];
    public ObservableCollection<SetupChecklistItem> SetupChecklistItems { get; } = [];
    public ObservableCollection<HelpShortcutRow> HelpShortcutsReference { get; } = [];
    public ObservableCollection<HelpPortRow> HelpPortsReference { get; } = [];

    public RelayCommand OpenContextHelpCommand { get; private set; } = null!;
    public RelayCommand ReturnFromHelpCommand { get; private set; } = null!;
    public RelayCommand SelectHelpTopicCommand { get; private set; } = null!;
    public RelayCommand SetHelpCategoryCommand { get; private set; } = null!;
    public RelayCommand ExecuteHelpJumpCommand { get; private set; } = null!;
    public RelayCommand ToggleSetupChecklistCommand { get; private set; } = null!;
    public RelayCommand ClearHelpSearchCommand { get; private set; } = null!;

    public HelpGuideTopic? SelectedHelpTopic
    {
        get => _selectedHelpTopic;
        set
        {
            if (SetProperty(ref _selectedHelpTopic, value))
            {
                foreach (var topic in _allHelpTopics)
                {
                    topic.IsSelected = ReferenceEquals(topic, value);
                }
            }
        }
    }

    public string HelpSearchQuery
    {
        get => _helpSearchQuery;
        set
        {
            if (SetProperty(ref _helpSearchQuery, value))
            {
                RebuildFilteredHelpTopics();
                OnPropertyChanged(nameof(HasHelpSearchQuery));
            }
        }
    }

    public bool HasHelpSearchQuery => !string.IsNullOrWhiteSpace(_helpSearchQuery);

    public string HelpCategoryFilter
    {
        get => _helpCategoryFilter;
        set
        {
            if (SetProperty(ref _helpCategoryFilter, value))
            {
                RebuildFilteredHelpTopics();
            }
        }
    }

    public bool HasHelpReturnSection => !string.IsNullOrWhiteSpace(_helpReturnSection) &&
                                        !string.Equals(_helpReturnSection, "Help", StringComparison.OrdinalIgnoreCase);

    public string HelpReturnLabel => _helpReturnSection switch
    {
        "Control" => "← Back to Control Deck",
        "YouTube" => "← Back to YouTube & Music",
        "Acoustics" => "← Back to Scenes & Acoustics",
        "CastRemote" => "← Back to Cast & Remote",
        "Stations" => "← Back to Stations & Streams",
        "Plugins" => SelectedPlugin is not null ? $"← Back to {SelectedPlugin.ShortName}" : "← Back to Plugins",
        "Mcp" => "← Back to MCP & Room Brain",
        "Settings" => "← Back to Settings",
        "Diagnostics" => "← Back to Health & Support",
        _ => "← Back",
    };

    public bool IsSetupChecklistExpanded
    {
        get => _isSetupChecklistExpanded;
        set
        {
            if (SetProperty(ref _isSetupChecklistExpanded, value))
            {
                OnPropertyChanged(nameof(SetupChecklistToggleLabel));
            }
        }
    }

    public string SetupChecklistToggleLabel => IsSetupChecklistExpanded ? "Hide Checklist ▴" : "Show Checklist ▾";

    public int SetupChecklistCompletedCount
    {
        get => _setupChecklistCompletedCount;
        private set
        {
            if (SetProperty(ref _setupChecklistCompletedCount, value))
            {
                OnPropertyChanged(nameof(SetupChecklistProgressText));
                OnPropertyChanged(nameof(SetupChecklistPercent));
            }
        }
    }

    public int SetupChecklistTotalCount
    {
        get => _setupChecklistTotalCount;
        private set
        {
            if (SetProperty(ref _setupChecklistTotalCount, value))
            {
                OnPropertyChanged(nameof(SetupChecklistProgressText));
                OnPropertyChanged(nameof(SetupChecklistPercent));
            }
        }
    }

    public string SetupChecklistProgressText => $"{SetupChecklistCompletedCount} / {SetupChecklistTotalCount} ready";

    public double SetupChecklistPercent => SetupChecklistTotalCount > 0
        ? (SetupChecklistCompletedCount * 100.0) / SetupChecklistTotalCount
        : 0;

    public bool HasFilteredHelpTopics => FilteredHelpTopics.Count > 0;

    private void InitializeHelpGuide()
    {
        OpenContextHelpCommand = new RelayCommand(_ => OpenContextualHelp());
        ReturnFromHelpCommand = new RelayCommand(_ =>
        {
            if (HasHelpReturnSection)
            {
                ActiveSection = _helpReturnSection;
            }
        });
        SelectHelpTopicCommand = new RelayCommand(param =>
        {
            if (param is HelpGuideTopic topic)
            {
                SelectedHelpTopic = topic;
            }
        });
        SetHelpCategoryCommand = new RelayCommand(param =>
        {
            if (param is string cat)
            {
                HelpCategoryFilter = cat;
            }
        });
        ExecuteHelpJumpCommand = new RelayCommand(param =>
        {
            if (param is HelpQuickJump jump)
            {
                JumpFromHelp(jump);
            }
        });
        ToggleSetupChecklistCommand = new RelayCommand(_ => IsSetupChecklistExpanded = !IsSetupChecklistExpanded);
        ClearHelpSearchCommand = new RelayCommand(_ =>
        {
            HelpSearchQuery = string.Empty;
            HelpCategoryFilter = "All";
        });

        SeedHelpShortcutsReference();
        SeedHelpPortsReference();
        SeedSetupChecklist();
        SeedHelpTopics();

        RebuildFilteredHelpTopics();
        SelectedHelpTopic = _allHelpTopics.FirstOrDefault();

        _stateStore.StateChanged += snap => _ = _dispatcher.InvokeAsync(RefreshHelpChecklist);
        if (_pluginHost is not null)
        {
            _pluginHost.PluginsChanged += () => { _ = _dispatcher.InvokeAsync(RefreshHelpChecklist); };
        }

        RefreshHelpChecklist();
    }

    public void OpenContextualHelp()
    {
        if (!string.Equals(ActiveSection, "Help", StringComparison.OrdinalIgnoreCase))
        {
            _helpReturnSection = ActiveSection;
            OnPropertyChanged(nameof(HasHelpReturnSection));
            OnPropertyChanged(nameof(HelpReturnLabel));
        }

        var section = _helpReturnSection;
        var pluginId = string.Equals(section, "Plugins", StringComparison.OrdinalIgnoreCase)
            ? SelectedPlugin?.Id
            : null;

        HelpGuideTopic? matched = null;
        if (!string.IsNullOrWhiteSpace(pluginId))
        {
            matched = _allHelpTopics.FirstOrDefault(t =>
                string.Equals(t.ContextPluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        matched ??= _allHelpTopics.FirstOrDefault(t =>
            string.Equals(t.ContextSection, section, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(t.ContextPluginId));

        HelpSearchQuery = string.Empty;
        HelpCategoryFilter = "All";
        ActiveSection = "Help";
        RefreshHelpChecklist();

        if (matched is not null)
        {
            SelectedHelpTopic = matched;
        }
    }

    private void JumpFromHelp(HelpQuickJump jump)
    {
        if (!string.IsNullOrWhiteSpace(jump.TargetPluginId))
        {
            var card = InstalledPlugins.FirstOrDefault(p =>
                string.Equals(p.Id, jump.TargetPluginId, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                SelectedPlugin = card;
            }

            ActiveSection = "Plugins";
            return;
        }

        if (!string.IsNullOrWhiteSpace(jump.TargetSection))
        {
            ActiveSection = jump.TargetSection;
        }
    }

    private void RebuildFilteredHelpTopics()
    {
        var query = (_helpSearchQuery ?? string.Empty).Trim().ToLowerInvariant();
        var cat = (_helpCategoryFilter ?? "All").Trim();

        var matches = _allHelpTopics.Where(t =>
            (cat.Equals("All", StringComparison.OrdinalIgnoreCase) ||
             t.Category.Equals(cat, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query) ||
             t.SearchIndex.Contains(query, StringComparison.Ordinal))).ToList();

        FilteredHelpTopics.Clear();
        foreach (var item in matches)
        {
            FilteredHelpTopics.Add(item);
        }

        OnPropertyChanged(nameof(HasFilteredHelpTopics));

        if (SelectedHelpTopic is null || !FilteredHelpTopics.Contains(SelectedHelpTopic))
        {
            SelectedHelpTopic = FilteredHelpTopics.FirstOrDefault();
        }
    }

    private void RefreshHelpChecklist()
    {
        if (SetupChecklistItems.Count == 0)
        {
            return;
        }

        var snap = _stateStore.Snapshot;
        var completed = 0;

        foreach (var item in SetupChecklistItems)
        {
            switch (item.Id)
            {
                case "avr":
                    item.IsComplete = snap.IsConnected;
                    item.StatusSummary = snap.IsConnected
                        ? $"{ReceiverTitle}{(IsLivePushConnected ? " • LIVE PUSH" : string.Empty)}"
                        : "Connect your Denon or Marantz receiver in Settings";
                    break;

                case "relay":
                    item.IsComplete = !string.IsNullOrWhiteSpace(RemoteWebUrl) && RemoteWebUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                    item.StatusSummary = item.IsComplete
                        ? $"Active on {RemoteWebUrl}"
                        : "Audio relay and Phone Remote (TCP 18925 by default)";
                    break;

                case "hue":
                {
                    var p = InstalledPlugins.FirstOrDefault(x => x.Id.Equals("philips-hue", StringComparison.OrdinalIgnoreCase));
                    item.IsComplete = p is not null && p.IsEnabled && p.Health == PluginHealthState.Connected;
                    item.StatusSummary = p?.StatusText ?? "Pair your Philips Hue Bridge for rooms, scenes and Music Sync";
                    break;
                }

                case "tv":
                {
                    var p = InstalledPlugins.FirstOrDefault(x => x.Id.Equals("smart-tv", StringComparison.OrdinalIgnoreCase));
                    item.IsComplete = p is not null && p.IsEnabled &&
                                      (p.Health == PluginHealthState.Connected || p.StatusText.Contains("Paired", StringComparison.OrdinalIgnoreCase));
                    item.StatusSummary = p?.StatusText ?? "Pair your LG webOS TV for inputs, apps, Screen Off and eARC";
                    break;
                }

                case "ha":
                {
                    var p = InstalledPlugins.FirstOrDefault(x => x.Id.Equals("home-assistant", StringComparison.OrdinalIgnoreCase));
                    item.IsComplete = p is not null && p.IsEnabled && p.Health == PluginHealthState.Connected;
                    item.StatusSummary = p?.StatusText ?? "Optional: connect Home Assistant scenes and the receiver sensor";
                    break;
                }

                case "scenes":
                    item.IsComplete = ScenePresets.Count > 0;
                    item.StatusSummary = $"{ScenePresets.Count} Smart Scenes and {AutomationRules.Count} automation rules";
                    break;
            }

            if (item.IsComplete)
            {
                completed++;
            }
        }

        SetupChecklistTotalCount = SetupChecklistItems.Count;
        SetupChecklistCompletedCount = completed;
    }

    private void SeedSetupChecklist()
    {
        SetupChecklistItems.Clear();
        SetupChecklistItems.Add(new SetupChecklistItem("avr", "Denon / Marantz receiver", "Settings →", "Settings"));
        SetupChecklistItems.Add(new SetupChecklistItem("relay", "Phone Remote & audio relay", "Phone QR →", "CastRemote"));
        SetupChecklistItems.Add(new SetupChecklistItem("hue", "Philips Hue lighting", "Open Hue →", "Plugins", "philips-hue"));
        SetupChecklistItems.Add(new SetupChecklistItem("tv", "LG webOS TV", "Open TV →", "Plugins", "smart-tv"));
        SetupChecklistItems.Add(new SetupChecklistItem("ha", "Home Assistant", "Open HA →", "Plugins", "home-assistant"));
        SetupChecklistItems.Add(new SetupChecklistItem("scenes", "Smart Scenes & automations", "Scenes →", "Acoustics"));
    }

    private void SeedHelpShortcutsReference()
    {
        // Keep in step with MainWindow.Window_PreviewKeyDown, ShortcutSettings.CreateDefaults and YouTubeCinemaWindow.
        HelpShortcutsReference.Clear();
        HelpShortcutsReference.Add(new("F1", "Main window", "Open Help for the page or plugin you are on"));
        HelpShortcutsReference.Add(new("Ctrl + 1 .. 9", "Main window", "Go to a page: 1 Deck, 2 Music, 3 Scenes, 4 Cast, 5 Stations, 6 Plugins, 7 Settings, 8 Health, 9 Help (Brain has no number)"));
        HelpShortcutsReference.Add(new("Ctrl + R", "Main window", "Refresh the receiver state (fixed key)"));
        HelpShortcutsReference.Add(new("F5", "Main window", "Refresh the receiver state (you can change this key)"));
        HelpShortcutsReference.Add(new("Space", "Main window", "Play / pause (not while a text box, button or list has focus)"));
        HelpShortcutsReference.Add(new("M", "Main window", "Mute on / off"));
        HelpShortcutsReference.Add(new("Ctrl + Up / Down", "Main window", "Master volume up / down 2 steps"));
        HelpShortcutsReference.Add(new("Mouse wheel", "Master Volume card", "Master volume up / down 2 steps"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + D", "Global (Windows)", "Show or hide the main window, also from the tray"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + P", "Global (Windows)", "Play / pause"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + M", "Global (Windows)", "Mute on / off"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + Up / Down", "Global (Windows)", "Master volume up / down 2 steps"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + Left / Right", "Global (Windows)", "Previous / next track"));
        HelpShortcutsReference.Add(new("F11", "Cinema window", "Fullscreen on / off"));
        HelpShortcutsReference.Add(new("H", "Cinema window", "Hide / show the bars"));
        HelpShortcutsReference.Add(new("Esc", "Cinema window", "Step back: show the bars, then leave fullscreen, then close"));
        HelpShortcutsReference.Add(new("Esc / Backspace", "Shortcut recorder", "Cancel the recording / clear the shortcut"));
        HelpShortcutsReference.Add(new("Esc", "Tray flyout", "Close the flyout"));
        HelpShortcutsReference.Add(new("Enter", "LG TV page text boxes", "Send: cast the YouTube link, type to the TV, show the toast, or open the URL"));
    }

    private void SeedHelpPortsReference()
    {
        HelpPortsReference.Clear();
        HelpPortsReference.Add(new("TCP 23", "Denon / Marantz AVR control (Telnet)", "Power, volume, inputs, sound modes, Audyssey, ECO, Zone 2 and dimmer. The receiver accepts one client at a time."));
        HelpPortsReference.Add(new("TCP 1255", "HEOS CLI and LIVE PUSH", "Playback, volume, track data, artwork, favorites and queue, plus push events from the receiver"));
        HelpPortsReference.Add(new("TCP 18925", "Audio relay and Phone Remote", "Streams YouTube, PC Audio Cast and jukebox files to HEOS and serves the Phone Remote. Inbound: use Allow Firewall. Change it in Settings → Relay Port."));
        HelpPortsReference.Add(new("TCP 18926", "MCP server (HTTP)", "POST /mcp and SSE with a Bearer token. Localhost only by default. Change it in Brain → HTTP PORT."));
        HelpPortsReference.Add(new("TCP (free port)", "Insights page (HTTP)", "127.0.0.1 only, a new port and key each app start. Settings → Open Insights opens it in the browser."));
        HelpPortsReference.Add(new("UDP 1900", "SSDP discovery", "Finds Denon / Marantz receivers, Philips Hue Bridges and LG webOS TVs on the LAN"));
        HelpPortsReference.Add(new("HTTPS 443 / HTTP 80", "Philips Hue Bridge API", "Rooms, lights, Hue scenes and Entertainment Areas on the LAN"));
        HelpPortsReference.Add(new("UDP 2100", "Hue Entertainment (DTLS)", "50 Hz light stream for Music Sync when an Entertainment Area exists"));
        HelpPortsReference.Add(new("WSS 3001 / WS 3000", "LG webOS SSAP WebSocket", "TV control, live state, apps, inputs, Screen Off and Magic Remote. Secure 3001 first, then 3000."));
        HelpPortsReference.Add(new("UDP 9 and 7", "Wake-on-LAN", "Broadcast wake packet that turns an LG webOS TV on from standby"));
        HelpPortsReference.Add(new("HTTP / HTTPS 8123", "Home Assistant REST and webhooks", "Entity sync, scene / script / switch triggers and the sensor.denon_desktop_receiver state"));
        HelpPortsReference.Add(new("TCP 8009 (TLS)", "Chromecast plugin: Cast v2", "Status and control of Chromecast and Google Cast devices"));
        HelpPortsReference.Add(new("UDP 5353", "mDNS / DNS-SD", "The Chromecast and Apple TV plugins find devices. Windows Firewall must let the answers in."));
        HelpPortsReference.Add(new("TCP (port from DNS-SD)", "Apple TV plugin: Companion and AirPlay", "Pairing, power, keys, apps and now playing. The Apple TV announces the ports (for example Companion 49153, AirPlay 7000)."));
        HelpPortsReference.Add(new("HTTPS 443 (internet)", "Online services", "YouTube, the Radio Browser directory, LRCLIB lyrics, Hue cloud discovery, the daily update check and the TV channel logos (GitHub). Device control does not need the internet."));
    }

    private void SeedHelpTopics()
    {
        _allHelpTopics.Clear();

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "quick-start",
            badge: "START",
            accentHex: "#00D4FF",
            category: "Getting Started",
            title: "Quick Start & First-Time Setup",
            subtitle: "Connect your Denon or Marantz receiver, allow the firewall rule, and add the devices you use.",
            overview: "Denon Desktop controls your Denon or Marantz AV receiver over your local network. It needs no cloud account. It uses the AVR control port (TCP 23) for power, volume, inputs and sound modes, and the HEOS port (TCP 1255) for playback and live push events. A built-in audio relay (TCP 18925) streams YouTube, PC audio and local files to the receiver and serves the Phone Remote. Plugins add Philips Hue, LG webOS TV, Home Assistant and, as separate downloads, Chromecast and Apple TV.",
            steps:
            [
                new("01", "Let the app find your receiver", "Start Denon Desktop. On the first start it scans the LAN and connects when it finds one receiver."),
                new("02", "Choose a receiver yourself if needed", "In Settings → NETWORK AUTO-DISCOVERY, click Scan LAN and then click your receiver in the list."),
                new("03", "Or type the address", "In Settings → RECEIVER CONNECTION PROFILE, type the IP in Receiver IP / Host and click Save & Sync."),
                new("04", "Check the connection", "Look at the title bar. ONLINE and the green LIVE PUSH badge show that the app gets live updates from the receiver."),
                new("05", "Allow the firewall rule", "To use YouTube, PC Audio Cast, the jukebox or the Phone Remote, click Allow Firewall in Music or Cast once. Windows asks for permission. Click Yes."),
                new("06", "Add your other devices (optional)", "Open Plugins and set up Philips Hue, LG webOS TV or Home Assistant. Their badges then show at the bottom of the left rail."),
                new("07", "Choose a look (optional)", "In Settings → APPEARANCE, choose a theme and a skin. See the Appearance topic."),
            ],
            proTips:
            [
                "Press F1 on any page, or click Help in the left rail, to open the topic for the page or plugin you are on.",
                "Click a device badge at the bottom of the left rail to open that plugin. The dot on the badge shows its health.",
                "Close and Minimize send the window to the Windows tray by default. Change this in Settings → DESKTOP & TRAY BEHAVIOR. Right-click the tray icon to exit.",
                "The tray icon changes to show playback, mute and standby.",
            ],
            troubleshooting:
            [
                new("The receiver shows OFFLINE but it is plugged in?", "On the receiver, set Setup → Network → Network Control to Always On. Make sure the PC and the receiver are on the same subnet."),
                new("More than one Denon or Marantz receiver on the network?", "The app connects by itself only when it finds one. Open Settings, click Scan LAN and click the receiver you want. The app remembers its IP and HEOS player."),
                new("Only HEOS controls work?", "Some HEOS speakers have no AVR control port (TCP 23). The app then uses HEOS for status, volume, mute and playback. Input, sound mode and acoustics need port 23."),
            ],
            quickJumps:
            [
                new("Open Settings →", "Settings"),
                new("Open Control Deck →", "Control"),
                new("Run Health Check →", "Diagnostics"),
            ],
            contextSection: "Settings"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "appearance",
            badge: "LOOK",
            accentHex: "#EC4899",
            category: "Getting Started",
            title: "Appearance: Themes & Skins",
            subtitle: "Choose Follow Windows, Dark or Light, and the Default, Hi-Fi, Compact or Large skin.",
            overview: "The theme sets the colors. The skin sets type, spacing, corners and the look of the meters. Any skin works with any theme. Both apply at once, with no restart, and the app saves your choice. The plugin pages follow the theme and the skin too.",
            steps:
            [
                new("01", "Open the Appearance card", "Open Settings. The APPEARANCE card is at the top."),
                new("02", "Choose a theme", "Click Follow Windows, Dark or Light. Follow Windows is the default. It uses light or dark as set in Windows (Settings → Personalization → Colors). The line under the buttons shows the theme in use now."),
                new("03", "Choose a skin", "Under SKIN, click Default, Hi-Fi, Compact or Large. Default is the standard look. Hi-Fi is a rack-receiver look with square corners, display type and backlit meters. Compact has smaller type and spacing, so more fits on the screen. Large has larger type, icons and spacing, for the sofa or a touch screen."),
                new("04", "Look at the meters", "In a dark theme the VU dials and the spectrum have dark faces. In a light theme the VU dials have cream faces with black print and a black needle, and the spectrum has a light screen with deep blue to red segments. With Hi-Fi in a light theme, the dials are ivory with an amber lamp and the segments are deep teal."),
                new("05", "Let the window fit the skin", "When you change the skin, the main window fits itself again to the new skin and to the monitor it is on."),
            ],
            proTips:
            [
                "Use Large on a TV or a touch screen. Use Compact on a small laptop screen.",
                "Follow Windows switches with Windows. You do not have to restart the app.",
            ],
            troubleshooting:
            [
                new("The Chromecast or Apple TV page stays dark in a light theme?", "Update the plugin. Chromecast 0.5.0 or later and Apple TV 0.4.0 or later follow the theme and the skin. Older versions keep the Dark colors and the Default sizes."),
                new("Follow Windows shows the wrong theme?", "Follow Windows uses the Windows app mode, not the Windows mode for the taskbar. Check 'Choose your default app mode' in Windows Settings → Personalization → Colors."),
            ],
            quickJumps:
            [
                new("Open Settings → Appearance →", "Settings"),
            ]));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "window-layout",
            badge: "WIN",
            accentHex: "#14B8A6",
            category: "Getting Started",
            title: "Window, Navigation & Status Line",
            subtitle: "Splash screen, window size and position, the left rail, the title bar and the status line.",
            overview: "The main window has a title bar at the top, a rail with the pages on the left, the page in the middle and a status line at the bottom. The window sizes itself to your screen and remembers where you put it.",
            steps:
            [
                new("01", "First start after an install", "A splash screen shows on the first start after you install, upgrade or reinstall Denon Desktop. Later starts do not show it."),
                new("02", "Window size on start", "The main window opens on the monitor under the mouse pointer. It sizes itself to the skin, so the Control Deck shows without scrolling where the screen is large enough. Long pages such as Settings and Help still scroll."),
                new("03", "Skin for your screen", "On the first start with the Default skin, the app picks the Compact skin when Default would scroll on your screen, and the Large skin on a very large screen at low scaling. The status line tells you. Change it in Settings → APPEARANCE."),
                new("04", "Move or resize the window", "Until you move or resize the window, it grows when the Control Deck grows (for example when the receiver connects). After you move or resize it, it keeps your size. The app remembers the size, the position and whether the window was maximized, for the next start."),
                new("05", "Change the page", "Click a page in the left rail. The top group is for listening: Deck, Music, Stations, Cast. The middle group is for the room: Scenes, Plugins. The bottom group is for the system: Brain, Settings, Health, Help."),
                new("06", "Open a device", "Click a device badge at the bottom of the rail. There is one badge for each plugin. The dot on the badge shows its health."),
                new("07", "Use the title bar", "The title bar shows the receiver model, its name, ONLINE / STANDBY / OFFLINE and LIVE PUSH. At the right are the MCP badge (opens Brain), Phone QR (opens Cast), Refresh and the power button."),
                new("08", "Read the status line", "The status line at the bottom shows the receiver, the last action and the last error. At the right it shows the time of the last update and the app version."),
                new("09", "Update the app", "When a newer version is out, an Update button shows next to the version. Click it to see what changed, then click Download and install. The app checks the download, closes, updates and starts again, and keeps your settings. The portable app opens the release page instead. The app checks GitHub once a day; turn this off, or check now, in Settings → DESKTOP & TRAY BEHAVIOR."),
            ],
            proTips:
            [
                "When you change the skin in Settings → APPEARANCE, the window fits itself again.",
                "Give the app version from the status line when you report a problem. Add the text from Health → Copy Report.",
                "Left-click the tray icon to open a small flyout with inputs, volume and stations. Double-click the tray icon to open the main window.",
            ],
            troubleshooting:
            [
                new("The window does not show after I close it?", "Close sends the window to the tray by default. Double-click the tray icon or press Ctrl + Alt + D. Turn off 'Close hides to tray' in Settings → DESKTOP & TRAY BEHAVIOR to change this."),
            ],
            quickJumps:
            [
                new("Open Settings →", "Settings"),
            ]));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "control-deck",
            badge: "DECK",
            accentHex: "#38BDF8",
            category: "Audio & Media",
            title: "Control Deck, Meters & Live Lyrics",
            subtitle: "Master volume, now playing, VU meters and 31-band spectrum, synced lyrics, inputs, sound modes and zones.",
            overview: "The Control Deck is the main page. The left column shows what plays, with the meters, quick scenes and stations. The right column has the controls that change the receiver: volume, input, sound mode, and power and zones. All values update live from the receiver.",
            steps:
            [
                new("01", "Set the volume", "In MASTER VOLUME, drag the slider, click -5, -1, +1 or +5, or turn the mouse wheel over the card (2 steps for each notch)."),
                new("02", "Change the volume scale", "Click the volume readout to switch between the 0–98 front-panel scale and dB. To make dB the default, tick Primary dB readout in Settings."),
                new("03", "Choose the meters", "On the now-playing card, click the meter button to step through VU Meter, RTA 31 and VU + RTA. Click VU Meters: ON / OFF to show or hide them."),
                new("04", "Read the meter badge", "DENON shows the real audio that the app streams to the receiver (PC Audio Cast, YouTube and the jukebox). SIM shows a simulation from the receiver volume for sources the app cannot hear, such as TV, HDMI or Spotify. PC shows the PC's own output while the receiver is off or idle. MUTED and IDLE show those states."),
                new("05", "Show synced lyrics", "Click Lyrics on the now-playing card while a track plays. The app gets the lyrics from LRCLIB and marks the current line."),
                new("06", "Change input and sound mode", "Use the INPUT SOURCE and SOUND MODE & DSP cards in the right column."),
                new("07", "Set sleep, ECO and Zone 2", "Use the POWER & ZONES card for the SLEEP TIMER, ECO On / Auto / Off and Zone 2."),
            ],
            proTips:
            [
                "QUICK SCENES & STATIONS runs a scene or a station in one click. Low Vol starts a station at the Safety Low Volume from Settings.",
                "The ROOM BRAIN strip shows only when the Room Doctor finds a problem. Click its fix, or Undo to go back.",
                "During a Chromecast or Apple TV program, the now-playing card shows that program, and Play / Pause (and Space) control that device.",
                "Click Phone QR in the title bar to show the Phone Remote QR code.",
            ],
            troubleshooting:
            [
                new("The meters show SIM?", "The receiver plays a source that the app cannot hear (TV, HDMI, Bluetooth or another app). The meters then show a simulation from the receiver volume."),
                new("YouTube or jukebox meters show SIM, not DENON?", "Real meters for YouTube and the jukebox need FFmpeg. They also wait until the analysis reaches the playing position."),
                new("The meters move before or after the sound?", "The meters use the Cinema Offset from Settings. Run Auto-Calibrate Mic in Settings, or change Cinema Offset (ms) by hand."),
                new("Lyrics say that no synced lyrics were found?", "Radio streams and YouTube titles with extra words (for example '[Official Video]') often do not match LRCLIB. Plain 'Artist – Title' tracks match best."),
            ],
            quickJumps:
            [
                new("Go to Control Deck →", "Control"),
            ],
            contextSection: "Control"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "youtube-cinema",
            badge: "YT",
            accentHex: "#EF4444",
            category: "Audio & Media",
            title: "YouTube, Cinema Window & Lip-Sync",
            subtitle: "Play YouTube and YouTube Music on the receiver, watch the video in sync, and measure the delay with the PC microphone.",
            overview: "The Music page finds YouTube songs, videos and playlists, or opens a YouTube link. The app sends the audio over the LAN relay (TCP 18925) to HEOS. With FFmpeg it sends 320 kbps MP3; without FFmpeg it sends the M4A audio directly. The Cinema window can show the video without sound, in sync with the receiver.",
            steps:
            [
                new("01", "Search", "In Music, click Music, Videos or Playlists. Type in the search box and press Enter (or click Search / Load)."),
                new("02", "Open a link", "Copy a YouTube or YouTube Music link and click Paste & Go."),
                new("03", "Play or queue", "In RESULTS & TRACKS, click ▶ Play to play the audio, Video to play and open the Cinema window, or + Q to add the track to UP NEXT QUEUE. For a playlist, click Open Tracks →."),
                new("04", "Open the Cinema window later", "Click Cinema Window in UP NEXT QUEUE, or Watch Video on the Control Deck."),
                new("05", "Use the Cinema window", "Press F11 for fullscreen. Press H to hide the bars. Press Esc to step back: show the bars, then leave fullscreen, then close."),
                new("06", "Measure the delay", "HEOS buffers network audio for a few seconds. With the speakers on, click Auto-Calibrate Mic in Settings → ACOUSTIC LIP-SYNC AUTO-CALIBRATION. The app plays a short 1,750 Hz chirp through the receiver, listens with the PC microphone and saves the delay as Cinema Offset (ms)."),
                new("07", "Fine-tune the sync", "In the Cinema window, click -500ms, -100ms, +100ms or +500ms. After a manual seek, click Re-Sync to Denon."),
            ],
            proTips:
            [
                "The same Cinema Offset also times the lyrics and the meters.",
                "To play YouTube on the LG TV screen, open Plugins → LG webOS TV and paste the link in CAST YOUTUBE URL / VIDEO ID TO TV.",
                "When a track ends, the app closes its stream, so HEOS does not play the same track again.",
            ],
            troubleshooting:
            [
                new("HEOS starts to load a track and then stops with no sound?", "Windows Firewall blocks TCP 18925 from the receiver. Click Allow Firewall in Music or Cast, then try again."),
                new("The video is ahead of or behind the speakers?", "Run Auto-Calibrate Mic in Settings, or click -500ms / +500ms in the Cinema window. The app remembers the offset."),
                new("Do I need FFmpeg?", "No. Without FFmpeg the app sends M4A audio. With FFmpeg (for example 'winget install Gyan.FFmpeg') it sends 320 kbps MP3, and the meters can show the real YouTube audio."),
            ],
            quickJumps:
            [
                new("Open YouTube & Music →", "YouTube"),
                new("Open Settings (Lip-Sync) →", "Settings"),
            ],
            contextSection: "YouTube"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "scenes-acoustics",
            badge: "SCENES",
            accentHex: "#8B5CF6",
            category: "Scenes & Acoustics",
            title: "Smart Scenes, Automations & Acoustics",
            subtitle: "Scenes that set several devices, rules that run on an app start or a schedule, and live Audyssey and tone settings.",
            overview: "A Smart Scene sets the receiver (power, volume, input, sound mode) and can run plugin actions in order, for example on Philips Hue, the LG TV or Home Assistant. You can run a scene from the app, a hotkey, the Phone Remote, an automation rule or an AI agent.",
            steps:
            [
                new("01", "Run a scene", "In Scenes, click ▶ Activate on a scene. The app has Movie Night, Late Night, Pure Hi-Fi, Morning Jazz and Party Multi-Ch."),
                new("02", "Save the current receiver state", "Click Capture Live. The app saves the input, sound mode, volume and acoustics as a new scene."),
                new("03", "Edit a scene", "Click Edit on a scene and change the fields in SCENE EDITOR."),
                new("04", "Add plugin actions", "In PLUGIN ACTION CHAIN, click + Step for each action. Set a delay in milliseconds for each step."),
                new("05", "Save the scenes", "Click Save Scenes."),
                new("06", "Add an automation rule", "Click Automation Rules. Click + App Trigger to run a scene when a program starts, or + Schedule to run it at a time on Daily, Weekdays or Weekends."),
                new("07", "Test and save the rules", "Click Test to run a rule now. Click Save Automations to keep your changes. AUTOMATION ACTIVITY LOG shows what ran."),
                new("08", "Tune the acoustics", "In Smart Scenes & Acoustics → LIVE ACOUSTIC TUNING & AUDYSSEY, set MultEQ, Dynamic EQ, Dynamic Volume, Audio Restorer, the display dimmer, Subwoofer and Dialog levels, and Bass and Treble. Click Sync from AVR to read the values from the receiver."),
            ],
            proTips:
            [
                "Give a scene a global hotkey: Settings → SHORTCUT EDITOR & COLLISION CHECKER → + Scene Key.",
                "Tick 'Show on-screen TV toast on Smart Scenes' in the LG TV plugin to see a message on the TV when a scene runs.",
                "In an App Trigger, type process names without .exe, separated by commas (for example vlc, mpv, Plex).",
            ],
            troubleshooting:
            [
                new("An app trigger did not run a second time?", "A rule runs once when the program starts and then waits for its Cooldown (seconds). Click Test to check a rule at once."),
                new("A plugin step fails with 'Missing … parameter'?", "Fill in the required action parameters shown under PLUGIN ACTION CHAIN (for example entityId for Home Assistant or roomId for Philips Hue) and click Save Scenes."),
                new("Bass and Treble do not change?", "The receiver turns off tone control while Dynamic EQ is ON."),
            ],
            quickJumps:
            [
                new("Open Scenes & Acoustics →", "Acoustics"),
                new("Bind Scene Hotkeys →", "Settings"),
            ],
            contextSection: "Acoustics"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "cast-phone-remote",
            badge: "CAST",
            accentHex: "#06B6D4",
            category: "Audio & Media",
            title: "Phone Remote, PC Audio Cast & Jukebox",
            subtitle: "Control the room from a phone on your Wi-Fi, send the PC's audio to the receiver, and play local files.",
            overview: "The app serves a web remote from this PC (TCP 18925). A phone on the same Wi-Fi opens it from a QR code. It needs no app install. The Cast page also sends live Windows audio to HEOS and plays local music files.",
            steps:
            [
                new("01", "Open the Phone Remote", "Click Phone QR in the title bar, or open Cast."),
                new("02", "Choose the access", "Click Owner QR for full control, or Guest QR for a guest who can only search and queue YouTube tracks."),
                new("03", "Scan the code", "Scan the QR code with the phone camera. The phone remote has the tabs Remote, Lights, Jukebox and Stations."),
                new("04", "Remove old access", "Click Revoke & Rotate to make new codes. Phones with the old code lose access. Active streams continue."),
                new("05", "Cast PC audio", "In CAST PC AUDIO TO DENON, choose the Windows output device, click MP3 320 kbps or PCM 16-bit WAV, and click ▶ Start PC Audio Cast."),
                new("06", "Play local files", "Drag files or folders onto LOCAL HI-RES / FLAC JUKEBOX, or click + Add Files or + Add Folder. Then click ▶ Play All."),
            ],
            proTips:
            [
                "Click Remote: ON / OFF to turn phone control off. Streams to the receiver keep working.",
                "Add the Phone Remote page to the phone home screen for quick access.",
                "PC Audio Cast sends silence while nothing plays, so HEOS keeps the stream open.",
                "MP3 casting needs FFmpeg. PCM 16-bit WAV works without it.",
                "The jukebox plays .flac, .wav, .mp3, .m4a, .aac, .ogg and .opus files.",
            ],
            troubleshooting:
            [
                new("The phone shows 'This site can't be reached'?", "Put the phone on the same Wi-Fi as the PC (not a guest network or mobile data). Click Allow Firewall once. If the PC has VPN or WSL adapters, check that the URL uses the PC's LAN IP."),
                new("The phone shows 'Remote Disabled'?", "Phone control is off. Click Remote: OFF on the Cast page to turn it on."),
            ],
            quickJumps:
            [
                new("Open Cast & Remote →", "CastRemote"),
            ],
            contextSection: "CastRemote"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "stations-radio",
            badge: "RADIO",
            accentHex: "#F59E0B",
            category: "Audio & Media",
            title: "Radio Stations, Presets & HEOS Favorites",
            subtitle: "Search the Radio Browser directory, save presets, play any stream URL, and load the receiver's HEOS favorites.",
            overview: "The Stations page has two tabs. Saved Presets & Direct Stream holds your own stations, a stream launcher and the receiver's HEOS favorites. Global Radio Explorer searches the open Radio Browser directory of internet radio.",
            steps:
            [
                new("01", "Search the directory", "Click Global Radio Explorer (40,000+). Type a station name, a genre or a country, and click Search Directory. Or click a genre chip."),
                new("02", "Find high-quality streams", "Tick Hi-Res (256k+ / FLAC) only before you search."),
                new("03", "Play or save a station", "Click ▶ Play to play a station, or + Save to add it to your presets."),
                new("04", "Edit your presets", "Click Saved Presets & Direct Stream. Change names or URLs, use ↑ and ↓ to sort, and click Save Changes. Click + Add Station for a new one."),
                new("05", "Play a stream URL", "In DIRECT STREAM LAUNCHER, paste the URL and click ▶  Stream Now. Click Test URL to check it first."),
                new("06", "Load the HEOS favorites", "In RECEIVER HEOS FAVORITES, click Load from Receiver."),
            ],
            proTips:
            [
                "Tick 'Apply safety low volume on stream launch' so late-night radio does not start loud. Set the level in Settings → Safety Low Volume.",
                "On the Control Deck, Low Vol starts a preset at the safety low volume.",
            ],
            troubleshooting:
            [
                new("A station does not play on HEOS?", "Click Test on the preset. It checks the HTTP status and the stream type. Some stations use HTTPS redirects or HLS playlists that older HEOS firmware cannot play. Try a direct MP3, AAC or FLAC stream."),
            ],
            quickJumps:
            [
                new("Open Stations & Streams →", "Stations"),
            ],
            contextSection: "Stations"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "plugin-hue",
            badge: "HUE",
            accentHex: "#F59E0B",
            category: "Plugins",
            title: "Philips Hue Lighting & Music Sync",
            subtitle: "Pair the bridge, control rooms and lights, run Music Sync, and link the lights to the receiver.",
            overview: "The built-in Philips Hue plugin talks to your Hue Bridge on the LAN. It controls rooms, lights and Hue scenes. Music Sync makes the lights follow the music. With a Hue Entertainment Area it streams at 50 Hz over DTLS (UDP 2100); without one it uses the normal LAN API.",
            steps:
            [
                new("01", "Open the plugin", "Open Plugins → Philips Hue. If the setup row is hidden, click Bridge Setup ▾."),
                new("02", "Find the bridge", "Click Auto-Discover, or type the BRIDGE IP."),
                new("03", "Press the bridge button", "Press the round link button on top of the Hue Bridge."),
                new("04", "Pair", "Within 30 seconds, click Press Bridge Button & Pair."),
                new("05", "Control the lights", "In MASTER & ROOMS, the All Lights chip shows every light. Click a room chip to filter. Drag the sliders, or click an AMBIENCE PRESETS or HUE BRIDGE SCENES button."),
                new("06", "Start Music Sync", "Choose Bass Pulse, Spectrum Split, Album-Art Ambient or Cinema Subtle, and click Start Music Sync."),
                new("07", "Link the lights to the receiver", "In DENON AVR LIGHTING AUTOMATIONS, choose the TARGET ROOM. Tick the actions you want: lights on with the receiver, lights off in standby, Sync with Denon Smart Scenes, and Cinema Pause Lights."),
            ],
            proTips:
            [
                "Click Simulate Beat to test the meters and Music Sync when no music plays.",
                "Click Try Demo Mode to try the plugin without a bridge.",
                "Music Sync follows the audio that plays on this PC.",
                "After pairing, click Hide Setup ▴. All On, All Off and Sync stay in the header.",
            ],
            troubleshooting:
            [
                new("Music Sync uses the LAN API, not the 50 Hz stream?", "Create an Entertainment Area in the Philips Hue phone app. If the bridge was paired with an old version, pair it again."),
                new("Only the lights of one room show?", "Click the All Lights chip in MASTER & ROOMS, or click Show All Lights."),
            ],
            quickJumps:
            [
                new("Open Philips Hue Plugin →", "Plugins", "philips-hue"),
                new("Bind Hue Global Hotkey →", "Settings"),
            ],
            contextSection: "Plugins",
            contextPluginId: "philips-hue"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "plugin-smart-tv",
            badge: "LG TV",
            accentHex: "#38BDF8",
            category: "Plugins",
            title: "LG webOS TV & Virtual Magic Remote",
            subtitle: "Pair the TV, switch inputs and apps, cast YouTube links, turn the screen off, and link the TV to the receiver.",
            overview: "The built-in Smart TV plugin keeps a WebSocket connection to your LG webOS TV (secure port 3001, then 3000). It shows the real inputs and installed apps, has a virtual Magic Remote, a live screen preview, PC-to-TV typing and Screen Off, and can follow the receiver.",
            steps:
            [
                new("01", "Prepare the TV", "On the TV, turn on 'Turn on via Wi-Fi' (often in Settings → General → External Devices → TV On With Mobile) and LG Connect Apps. The menu names change with the webOS version."),
                new("02", "Find the TV", "Open Plugins → LG webOS TV. Click Setup ▾ and then Auto-Discover TV, or type the TV LAN IP ADDRESS."),
                new("03", "Get the MAC address", "With the TV on, click Auto-MAC. Wake-on-LAN needs the MAC address."),
                new("04", "Pair", "Click Pair with LG TV. On the TV, accept the 'Denon Desktop' prompt."),
                new("05", "Use inputs and apps", "When the badge shows LIVE • WSS :3001, the HDMI & EXTERNAL INPUTS and INSTALLED TV APPS load. A green dot shows an input with a signal."),
                new("06", "Cast a YouTube link", "Paste a YouTube link or video ID in CAST YOUTUBE URL / VIDEO ID TO TV and press Enter."),
                new("07", "Turn the screen off", "Click Screen Off to turn off the picture while the sound plays. Click Wake Screen to turn it on."),
                new("08", "Choose the TV audio output", "In AUDIO OUTPUT, click HDMI ARC / eARC, TV Speakers, Optical Out or Bluetooth."),
                new("09", "Link the TV to the receiver", "In DENON AVR COORDINATION, tick the actions you want, for example 'Wake TV (WoL) when Denon powers ON'. Changes save automatically."),
                new("10", "See the program and channel logo", "On Live TV, the now-playing hero shows the program from the TV's guide as the title, the channel and the program times below it, and the channel logo. Insights counts Live TV per channel and per program."),
            ],
            proTips:
            [
                "Channel logos come from the open tv-logos collection on GitHub. The plugin fetches the logos of your whole channel list once, so GitHub never sees which channel you watch. Turn it off with 'Show channel logos' in DENON AVR COORDINATION.",
                "In LIVE TV SCREEN & VIRTUAL TRACKPAD, click Snap Now or Auto-Refresh to see the TV screen. Click on the image to click on the TV.",
                "Press Enter in the YouTube, keyboard, toast or URL box to send without a click.",
                "If remote buttons or apps fail with '401 insufficient permissions', open Setup ▾ and click Re-Pair (Full Permissions).",
            ],
            troubleshooting:
            [
                new("Wake TV (WoL) does not turn the TV on?", "Check that the MAC ADDRESS is filled in (click Auto-MAC while the TV is on). Check that 'Turn on via Wi-Fi' is on in the TV."),
                new("The badge shows PAIRED • STANDBY after the TV turns off?", "This is normal. The TV closes the connection in standby. Click Wake TV (WoL). The plugin connects again when webOS has started."),
                new("Pairing fails with 'Could not connect'?", "Turn the TV on (not standby). Check that it is on the same subnet and that LG Connect Apps is on."),
                new("A channel shows letters instead of its logo?", "The collection has no logo with that channel's name. To pin one, add the channel name and an https address or image file to ChannelLogoOverrides in the plugin's config.json (\"none\" shows no logo), then reconnect the TV."),
                new("The hero shows the channel but no program?", "The TV has no guide data for this channel. The plugin asks again every 2 minutes and after each program."),
            ],
            quickJumps:
            [
                new("Open LG webOS TV →", "Plugins", "smart-tv"),
            ],
            contextSection: "Plugins",
            contextPluginId: "smart-tv"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "plugin-home-assistant",
            badge: "HA",
            accentHex: "#10B981",
            category: "Plugins",
            title: "Home Assistant Bridge",
            subtitle: "Connect with a long-lived access token, trigger scenes, scripts and switches, and publish the receiver state.",
            overview: "The built-in Home Assistant plugin connects to your Home Assistant server over its REST API. You can trigger scenes, scripts, switches and lights from Denon Desktop, Smart Scenes and the Phone Remote. The plugin can also publish the receiver state to Home Assistant as sensor.denon_desktop_receiver.",
            steps:
            [
                new("01", "Make a token in Home Assistant", "In Home Assistant, click your profile (bottom left) → Security → Long-Lived Access Tokens → Create Token. Name it 'Denon Desktop' and copy it."),
                new("02", "Connect", "In Plugins → Home Assistant, type the HOME ASSISTANT URL (for example http://homeassistant.local:8123). Paste the token in LONG-LIVED ACCESS TOKEN and click Save & Sync."),
                new("03", "Find entities", "Use the All, Scenes, Scripts, Switches and Lights chips or the filter box. Switches and lights show their ON / OFF state."),
                new("04", "Publish the receiver state", "Tick 'Automatically push Denon AVR Power, Volume, Input & Smart Scene events to Home Assistant'. The sensor state is playing, paused or standby. Its attributes are model, volume, muted, input_source, sound_mode, now_playing and last_scene."),
            ],
            proTips:
            [
                "No Home Assistant server yet? Click Try Demo to try virtual blinds, smart plugs and scenes.",
                "Click Push Status to send the receiver state now.",
                "Add a WEBHOOK ID (optional) to also send receiver events to a Home Assistant webhook.",
            ],
            troubleshooting:
            [
                new("Save & Sync returns 401 Unauthorized?", "Copy the whole token again, with no spaces or quotes. Check that the URL has the right http:// or https:// and the port (default 8123)."),
            ],
            quickJumps:
            [
                new("Open Home Assistant Bridge →", "Plugins", "home-assistant"),
            ],
            contextSection: "Plugins",
            contextPluginId: "home-assistant"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "plugin-chromecast",
            badge: "GCAST",
            accentHex: "#F59E0B",
            category: "Plugins",
            title: "Chromecast & Google Cast Plugin",
            subtitle: "Separate download. Find Cast devices, see what they play, control them, and let them drive the receiver.",
            overview: "The Chromecast plugin is not part of the app. You install it separately. It finds Chromecast and Google Cast devices on the LAN (mDNS) at start and every 10 minutes. It shows the app, title, artist, artwork, player state and volume of each device, and controls them over Cast v2 (TLS port 8009). For each device it can drive the receiver when an app starts or stops. It also writes watch sessions to the habit log.",
            steps:
            [
                new("01", "Download the plugin", "Download Denon.Plugin.Chromecast-<version>.zip from the chromecast-v* release that matches your Denon Desktop release."),
                new("02", "Install it", "Close Denon Desktop. Unzip the file to %LocalAppData%\\DenonDesktop\\plugins\\external\\Chromecast\\. Start Denon Desktop."),
                new("03", "Open the plugin", "Open Plugins → Chromecast & Google Cast. The devices that the plugin found show as cards."),
                new("04", "Find a device", "Click Discover now. If a device does not show, type its IP address and click Add by IP."),
                new("05", "Control a device", "On a device card, click Play / Pause, Stop, Vol −, Vol +, Mute or Quit app."),
                new("06", "Let the device drive the receiver", "Open Denon automation on the card. Choose what to do: power on the Denon when an app starts, a Denon input, a sound mode, a Smart Scene, Idle power-off (min), and pause the device when the Denon powers off. Click Save."),
                new("07", "Turn automations on or off", "Use Automations enabled at the top of the page. All device automations are off by default."),
            ],
            proTips:
            [
                "When the receiver is on TV Audio and the TV shows the HDMI input of the Chromecast, the Control Deck shows the cast program and its artwork. Play / Pause and Space then control the Chromecast.",
                "Idle power-off turns the receiver off only if it is still on the input of that device. 0 minutes means never.",
                "Each action is also an MCP tool, for example plugin_chromecast_now_playing. Use the actions in Smart Scenes and hotkeys too.",
                "To update the plugin, close Denon Desktop first. The loaded plugin file is in use.",
            ],
            troubleshooting:
            [
                new("No Cast devices show?", "Put the PC and the devices on the same LAN. Windows Firewall must allow inbound UDP for Denon Desktop, or the mDNS answers do not arrive. Set the network to Private. Add by IP works without mDNS."),
                new("A speaker group does not show?", "The plugin skips Google Cast speaker groups."),
                new("A Netflix session has no program title?", "Netflix shares only its own name over Cast. The play state is correct."),
                new("The plugin does not load?", "Use the plugin release that matches your Denon Desktop release. They must use the same Plugin SDK."),
            ],
            quickJumps:
            [
                new("Open Chromecast Plugin →", "Plugins", "chromecast"),
            ],
            contextSection: "Plugins",
            contextPluginId: "chromecast"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "plugin-apple-tv",
            badge: "ATV",
            accentHex: "#64748B",
            category: "Plugins",
            title: "Apple TV Plugin",
            subtitle: "Separate download. Pair once with a PIN, see what plays, control the Apple TV, and let it drive the receiver.",
            overview: "The Apple TV plugin is not part of the app. You install it separately. It finds Apple TVs on the LAN, pairs once with the PIN that the TV shows, and shows what plays: the app, the title, series and episode, and the play state. It can play and pause, send remote buttons, open apps, and wake or sleep the Apple TV. It can drive the receiver when the Apple TV wakes or sleeps. It does not open the now-playing channel while the Apple TV sleeps, so it does not wake it.",
            steps:
            [
                new("01", "Download the plugin", "Download Denon.Plugin.AppleTv-<version>.zip from the appletv-v* release that matches your Denon Desktop release."),
                new("02", "Install it", "Close Denon Desktop. Unzip the file to %LocalAppData%\\DenonDesktop\\plugins\\external\\AppleTv\\. Start Denon Desktop."),
                new("03", "Find the Apple TV", "Open Plugins → Apple TV and click Discover now."),
                new("04", "Start pairing", "On the Apple TV card, click Pair. The TV shows a 4-digit PIN."),
                new("05", "Send the PIN", "Type the PIN and click Send PIN. One pairing gives both the control link and the now-playing channel."),
                new("06", "See what plays", "The card shows the app, the title and the play state. The Control Deck now-playing card shows the program too, with artwork when the app has it."),
                new("07", "Control the Apple TV", "On the card, click Play / Pause, Menu, Home, Wake or Sleep."),
                new("08", "Let the Apple TV drive the receiver", "Open Settings and Denon automation on the card. Choose what to do: power on the Denon when the Apple TV wakes, a Denon input, a sound mode, a Smart Scene on wake, Power off after sleep (min), and pause the Apple TV when the Denon powers off. Click Save."),
            ],
            proTips:
            [
                "All automations are off by default. Automations enabled at the top of the page turns them all on or off.",
                "The now-playing card shows the Apple TV when it is on an HDMI input of the TV (receiver on TV Audio) or plugged into the receiver. Play / Pause and Space then control the Apple TV.",
                "Power off after sleep turns the receiver off only while it is still on that input. 0 minutes means never.",
                "To remove the pairing on the Apple TV too, open Settings › Remotes and Devices › Remote App and Devices on the Apple TV.",
                "The actions (now-playing, play-pause, key, launch-app, power-on, power-off and more) also work in Smart Scenes, hotkeys and MCP. power-off needs the Full Orchestration tier.",
            ],
            troubleshooting:
            [
                new("No Apple TV shows?", "Put the PC and the Apple TV on the same network and click Discover now again. A firewall that drops multicast answers can hide the Apple TV."),
                new("The card shows no duration or artwork?", "Some apps do not send them. DRTV, for example, sends no duration or position."),
                new("Pause does nothing?", "Some apps do not accept the command, for example live channels in DRTV."),
                new("The page keeps the Dark colors in a light theme?", "Update to Apple TV plugin 0.4.0 or later."),
            ],
            quickJumps:
            [
                new("Open Apple TV Plugin →", "Plugins", "apple-tv"),
            ],
            contextSection: "Plugins",
            contextPluginId: "apple-tv"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "mcp-room-brain",
            badge: "MCP",
            accentHex: "#10B981",
            category: "Plugins",
            title: "MCP Server & Room Brain",
            subtitle: "Connect AI agents over Streamable HTTP (/mcp), set safety limits, and use the Room Doctor and Undo.",
            overview: "Denon Desktop has a built-in Model Context Protocol (MCP) server (POST /mcp and SSE) on TCP 18926. AI clients such as Claude Desktop, Cursor, VS Code or Antigravity can read the room state and control the receiver, the plugins and the scenes. Safety limits and an Undo snapshot protect the room. The Room Brain also checks the devices for problems and learns your volume habits on this PC only.",
            steps:
            [
                new("01", "Open the page", "Click Brain in the left rail, or the MCP :18926 badge in the title bar."),
                new("02", "Copy the client config", "Click Copy MCP Config JSON and paste it into your MCP client."),
                new("03", "Choose a permission tier", "Click Observer (Read-Only), Safe Control (No Power Off) or Full Orchestration. The default is Full Orchestration."),
                new("04", "Set the volume ceiling", "Set HARD MAX SAFE VOLUME CEILING (default 65). An AI agent cannot set the volume above it."),
                new("05", "Apply the settings", "Click Save & Apply."),
                new("06", "Check the room", "Click Run Room Doctor. It finds problems such as a muted receiver while music plays, loud volume late at night, or a TV screen on during music. Click a fix to apply it."),
                new("07", "Undo a change", "Click Restore Previous State to go back to the state before the last AI or atmosphere change."),
            ],
            proTips:
            [
                "By default the server listens on 127.0.0.1 only and needs the Bearer token. Tick 'Allow LAN connections' only if an agent on another PC must connect.",
                "LIVE MCP ACTIVITY FEED shows each tool call and resource read.",
                "Each enabled plugin action is also an MCP tool (plugin_<id>_<action>). Read-only tools work in the Observer tier. Tools that a plugin marks destructive need Full Orchestration.",
                "Learned preferences stay on this PC in room-habits.json (denon://memory/habits).",
            ],
            troubleshooting:
            [
                new("The MCP client gets 401 Unauthorized?", "The Authorization: Bearer header must match BEARER AUTH TOKEN. Use Copy MCP Config JSON to avoid typing errors. Click Regenerate only if you want a new token; then update every client."),
                new("Another PC on the LAN cannot connect?", "Tick 'Allow LAN connections' and click Save & Apply. Windows needs a URL reservation (URLACL) or one run as administrator to listen on the LAN. Without it, the server stays on localhost."),
            ],
            quickJumps:
            [
                new("Open MCP & Room Brain →", "Mcp"),
            ],
            contextSection: "Mcp"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "insights",
            badge: "STATS",
            accentHex: "#22D3EE",
            category: "Reference",
            title: "Insights: Your Watching & Listening",
            subtitle: "Watch and listen time, your weekly rhythm, volume habits and who controls the room, in your browser.",
            overview: "Insights shows the habit event log as charts and plain observations. It opens in your browser from a small web server inside the app. Only this PC can open it: the server listens on 127.0.0.1 and needs a key that the app gives the browser. Everything stays on this PC. The page uses the app's theme and skin, and changes with them.",
            steps:
            [
                new("01", "Open Insights", "Open Settings. In SMART EVENT & HABIT LOGGING, click Open Insights. The page opens in your default browser."),
                new("02", "Choose a period", "Click Today, 7 days, 30 days or 90 days at the top. The display shows the watch and listen time and the change against the period before."),
                new("03", "Read the observations", "The cards under the display tell you your prime time, the top app, the longest sitting, volume differences and who made most changes."),
                new("04", "Find your rhythm", "In WEEKLY RHYTHM, each light is one hour of one weekday. The brighter it glows, the more played then. The outlined light is now."),
                new("05", "See what played", "TIME PER DAY, BY SOURCE, TOP APPS & INPUTS and MOST WATCHED show where the time went. LIVE TV CHANNELS shows the time per channel with its logo. Point at a bar or a slice for the numbers."),
                new("06", "Check volume and control", "VOLUME shows each volume change and the average for each time of day. WHO'S IN CONTROL splits the changes between remotes and devices, Denon Desktop, the phone remote, automations and an AI agent."),
                new("07", "Look back at the activity", "ACTIVITY lists the events, newest first. Filter by Media, Receiver, TV, Lighting or Control. Tick Connection events to also see connects and disconnects."),
            ],
            proTips:
            [
                "Time is counted once: when a cast plays through the TV and the receiver at the same time, only the cast counts.",
                "RIGHT NOW updates every 5 seconds; the charts every minute.",
                "An AI agent reads the same data over MCP: get_watch_time, get_listening_habits and get_activity_history.",
            ],
            troubleshooting:
            [
                new("The page says 'Open Insights from the app'?", "The page needs this run's key. The app makes a new key each time it starts. In the app, click Open Insights again."),
                new("The charts are empty?", "Insights reads the habit event log. Make sure 'Record smart device, media & external remote events locally' is on in Settings. Sessions shorter than the media dwell filter are not recorded."),
                new("The browser does not open?", "The line above the button then shows the address. Copy it into your browser."),
            ],
            quickJumps:
            [
                new("Open Settings →", "Settings"),
            ],
            contextSection: "Settings"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "shortcuts-hotkeys",
            badge: "KEYS",
            accentHex: "#A855F7",
            category: "Reference",
            title: "Keyboard Shortcuts, Global Hotkeys & Tray",
            subtitle: "All shortcuts, the shortcut recorder, collision checks, scene and plugin hotkeys, and the tray flyout.",
            overview: "In-app shortcuts work while the main window has focus. Global hotkeys work from any Windows app, also while Denon Desktop is in the tray. You can change both in Settings.",
            steps:
            [
                new("01", "Record a shortcut", "In Settings → SHORTCUT EDITOR & COLLISION CHECKER, click Record on a row and press the new key combination. Press Esc to cancel or Backspace to clear."),
                new("02", "Clear a shortcut", "Click ✕ on the row."),
                new("03", "Add a scene or plugin hotkey", "Click + Scene Key or + Plugin Key to give a global hotkey to a Smart Scene or a plugin action."),
                new("04", "Check for collisions", "Click Check Collisions. CONFLICT means a duplicate in the app. OS TAKEN means another app has the hotkey. RESERVED means Windows or the app uses it. OVERLAP means a global and an in-app key are the same (the global key wins)."),
                new("05", "Save", "Click Save & Sync to apply the shortcuts. Click Reset to load the defaults into the editor."),
                new("06", "Use the tray", "Left-click the tray icon to open the flyout with inputs, volume, stations and play / stop. Double-click it to open the main window. Right-click it for the menu."),
            ],
            proTips:
            [
                "Ctrl + 1 .. 9, Ctrl + R and F1 are fixed. They are not in the editor.",
                "Global hotkeys keep working while the window is in the tray.",
            ],
            troubleshooting:
            [
                new("A global hotkey shows OS TAKEN?", "Another app (for example an overlay, PowerToys, Discord or Spotify) already uses it. Record a different combination, for example with Ctrl + Alt + Shift."),
            ],
            quickJumps:
            [
                new("Open Shortcut Editor →", "Settings"),
            ],
            showShortcutsTable: true));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "ports-troubleshooting",
            badge: "NET",
            accentHex: "#F43F5E",
            category: "Troubleshooting",
            title: "Network Ports, Firewall & Troubleshooting",
            subtitle: "The ports that Denon Desktop uses, the Health Check, the AVR / HEOS console and common fixes.",
            overview: "Device control runs on your local network. Some features use the internet: YouTube, the Radio Browser directory, LRCLIB lyrics, Hue cloud discovery and the daily update check on GitHub (you can turn it off in Settings). The table below lists the ports. The Health page tests the receiver and has tools for common problems.",
            steps:
            [
                new("01", "Run the Health Check", "In Health, click Run Health Check. It tests the network connection, the AVR control port (23), the HEOS engine (1255), the audio output and the radio stream."),
                new("02", "Fix silent audio", "If the receiver is on but silent, click Fix Silent Audio. It wakes the receiver, selects HEOS, unmutes it and sets an audible volume."),
                new("03", "Reconnect", "Click Reset Links to close and open the connections to the receiver again."),
                new("04", "Send a raw command", "In ADVANCED TOOLS, click AVR (:23) or HEOS (:1255), type a command such as SI? or MV?, and click Send. The Check buttons send common queries."),
                new("05", "Read the activity history", "In ACTIVITY HISTORY, click All, Info, Warnings or Errors, or search. Click Open Folder to open the log files."),
                new("06", "Copy a report", "Click Copy Report to copy a health summary and the recent activity to the clipboard."),
            ],
            proTips:
            [
                "Give the receiver, the Hue Bridge and the TV fixed IP addresses (DHCP reservations) in your router.",
                "The log files are in %LocalAppData%\\DenonDesktop\\logs.",
            ],
            troubleshooting:
            [
                new("YouTube, PC Audio Cast or the Phone Remote do not connect?", "Click Allow Firewall in Music or Cast (it opens TCP 18925 on private networks). Set the Windows network profile to Private, not Public."),
                new("AVR commands are slow or fail while HEOS works?", "The receiver accepts one Telnet (port 23) client at a time. Close other Denon remote apps, or click Reset Links in Health."),
                new("Chromecast or Apple TV devices do not show?", "These plugins find devices with mDNS (UDP 5353). Windows Firewall must let the answers in, and the network should be Private."),
                new("A plugin does not show on the Phone Remote?", "Check that the plugin is enabled in Plugins. The Lights tab on the phone shows the actions of enabled plugins only."),
            ],
            quickJumps:
            [
                new("Open Health & Support →", "Diagnostics"),
                new("Open Firewall Helper →", "CastRemote"),
            ],
            contextSection: "Diagnostics",
            showPortsTable: true));
    }
}
