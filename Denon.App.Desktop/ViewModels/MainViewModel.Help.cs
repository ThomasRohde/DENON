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
                        : "Connect your Denon or Marantz AVR via Settings";
                    break;

                case "relay":
                    item.IsComplete = !string.IsNullOrWhiteSpace(RemoteWebUrl) && RemoteWebUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                    item.StatusSummary = item.IsComplete
                        ? $"Active on {RemoteWebUrl}"
                        : "LAN audio relay & Phone Remote (:18925)";
                    break;

                case "hue":
                {
                    var p = InstalledPlugins.FirstOrDefault(x => x.Id.Equals("philips-hue", StringComparison.OrdinalIgnoreCase));
                    item.IsComplete = p is not null && p.IsEnabled && p.Health == PluginHealthState.Connected;
                    item.StatusSummary = p?.StatusText ?? "Pair your Philips Hue Bridge for rooms & 50 Hz Music Sync";
                    break;
                }

                case "tv":
                {
                    var p = InstalledPlugins.FirstOrDefault(x => x.Id.Equals("smart-tv", StringComparison.OrdinalIgnoreCase));
                    item.IsComplete = p is not null && p.IsEnabled &&
                                      (p.Health == PluginHealthState.Connected || p.StatusText.Contains("Paired", StringComparison.OrdinalIgnoreCase));
                    item.StatusSummary = p?.StatusText ?? "Pair your LG webOS TV for apps, OLED Screen Off & eARC";
                    break;
                }

                case "ha":
                {
                    var p = InstalledPlugins.FirstOrDefault(x => x.Id.Equals("home-assistant", StringComparison.OrdinalIgnoreCase));
                    item.IsComplete = p is not null && p.IsEnabled && p.Health == PluginHealthState.Connected;
                    item.StatusSummary = p?.StatusText ?? "Optional: connect Home Assistant scenes & live AVR sensor";
                    break;
                }

                case "scenes":
                    item.IsComplete = ScenePresets.Count > 0;
                    item.StatusSummary = $"{ScenePresets.Count} Smart Scenes & {AutomationRules.Count} Automation Rules configured";
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
        SetupChecklistItems.Add(new SetupChecklistItem("avr", "Denon / Marantz AVR", "Settings →", "Settings"));
        SetupChecklistItems.Add(new SetupChecklistItem("relay", "Phone Remote & Audio Relay", "Phone QR →", "CastRemote"));
        SetupChecklistItems.Add(new SetupChecklistItem("hue", "Philips Hue Smart Lighting", "Open Hue →", "Plugins", "philips-hue"));
        SetupChecklistItems.Add(new SetupChecklistItem("tv", "LG webOS Smart TV", "Open TV →", "Plugins", "smart-tv"));
        SetupChecklistItems.Add(new SetupChecklistItem("ha", "Home Assistant Bridge", "Open HA →", "Plugins", "home-assistant"));
        SetupChecklistItems.Add(new SetupChecklistItem("scenes", "Smart Scenes & Automations", "Scenes →", "Acoustics"));
    }

    private void SeedHelpShortcutsReference()
    {
        HelpShortcutsReference.Clear();
        HelpShortcutsReference.Add(new("F1", "Anywhere in App", "Open Context-Aware Help & Guide for the current screen or plugin"));
        HelpShortcutsReference.Add(new("Ctrl + 1 .. 9", "Anywhere in App", "Switch sections (1 Control, 2 YouTube, 3 Scenes, 4 Cast, 5 Stations, 6 Plugins, 7 Settings, 8 Health, 9 Help)"));
        HelpShortcutsReference.Add(new("Ctrl + R / F5", "In-App", "Force-refresh Denon AVR & HEOS state"));
        HelpShortcutsReference.Add(new("Space", "In-App", "Play / Pause HEOS playback"));
        HelpShortcutsReference.Add(new("M", "In-App", "Toggle Master Mute"));
        HelpShortcutsReference.Add(new("Ctrl + Up / Down", "In-App", "Adjust Master Volume ±2"));
        HelpShortcutsReference.Add(new("Mouse Wheel", "Volume Card", "Scroll smoothly over the Volume Card to nudge Master Volume ±2"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + D", "Global Windows", "Show / Hide Denon Desktop from anywhere (even minimized to tray)"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + P", "Global Windows", "Global Play / Pause"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + M", "Global Windows", "Global Mute toggle"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + Up / Down", "Global Windows", "Global Master Volume ±2"));
        HelpShortcutsReference.Add(new("Ctrl + Alt + Left / Right", "Global Windows", "Global Previous / Next Track"));
        HelpShortcutsReference.Add(new("F11 / H / Esc", "YouTube Cinema", "Toggle monitor fullscreen / Hide chrome bars / Progressive exit"));
        HelpShortcutsReference.Add(new("Enter", "Smart TV Inputs", "Instantly cast YouTube URL, type to TV keyboard, or send on-screen toast"));
    }

    private void SeedHelpPortsReference()
    {
        HelpPortsReference.Clear();
        HelpPortsReference.Add(new("TCP 23", "Denon / Marantz AVR Telnet", "Hardware power, master volume, HDMI inputs, sound modes, Audyssey, ECO, Zone 2 & dimmer"));
        HelpPortsReference.Add(new("TCP 1255", "HEOS CLI & LIVE PUSH", "Zero-lag (<50 ms) real-time playback, volume, album art, favorites & queue events"));
        HelpPortsReference.Add(new("TCP 18925", "LAN Audio Relay & Phone Remote", "Streams YouTube, PC Audio Cast & local FLACs to HEOS + hosts the QR Couch Phone Remote web UI"));
        HelpPortsReference.Add(new("UDP 1900", "SSDP Auto-Discovery", "Zero-config LAN discovery for Denon/Marantz AVRs, Philips Hue Bridges & LG webOS TVs"));
        HelpPortsReference.Add(new("HTTPS 443 / HTTP 80", "Philips Hue Bridge API", "Room/light controls, native Hue scenes & Entertainment Area discovery on local LAN"));
        HelpPortsReference.Add(new("UDP 2100", "Hue Entertainment API v2 (DTLS)", "True 50 Hz zero-lag binary RGB streaming for FFT Music Sync"));
        HelpPortsReference.Add(new("WSS 3001 / WS 3000", "LG webOS SSAP WebSocket", "Persistent LG Smart TV control, live subscriptions, installed apps, OLED Screen Off & Magic Remote"));
        HelpPortsReference.Add(new("UDP 9 & 7", "Wake-on-LAN (Magic Packet)", "Multi-interface broadcast wake packet to power on LG webOS TVs from standby"));
        HelpPortsReference.Add(new("HTTP/HTTPS 8123", "Home Assistant REST & Webhooks", "Entity sync, scene/script/switch triggering & sensor.denon_desktop_receiver telemetry"));
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
            subtitle: "Get your Denon/Marantz AVR, LIVE PUSH, Phone Remote, and smart-home ecosystem online in 2 minutes.",
            overview: "Denon Desktop talks directly to your Denon or Marantz AV receiver over your local Wi-Fi/Ethernet network — no cloud account required. It combines hardware AVR Telnet (:23) with real-time HEOS push events (:1255) for <50 ms response, plus a built-in LAN audio relay (:18925) and modular smart-home plugins.",
            steps:
            [
                new("01", "Connect Your Receiver", "Open Settings and click Auto-Discover Receivers (or type your AVR's LAN IP). Denon Desktop automatically detects your exact model, firmware, and HEOS player."),
                new("02", "Verify LIVE PUSH Status", "Look at the top status pill: a green ONLINE badge plus LIVE PUSH means volume knob turns and physical remote clicks sync to your PC in under 50 ms."),
                new("03", "Allow Windows Firewall for Audio & Phone Remote", "If you plan to play YouTube, cast PC audio, stream FLACs, or use the Couch Phone Remote, click Allow Firewall in Cast & Remote (or Settings) once so port 18925 is reachable on your private LAN."),
                new("04", "Connect Your Ecosystem (Optional)", "Use the LIVE ECOSYSTEM quick-jump strip in the left sidebar to pair Philips Hue, your LG webOS Smart TV, or Home Assistant — all three also work in Smart Scenes & Couch Phone Remote."),
                new("05", "Pin to System Tray", "By default, closing the window minimizes cleanly to the Windows system tray (right-click for the floating mini-flyout or Exit). Toggle Launch at Windows Startup in Settings if desired."),
            ],
            proTips:
            [
                "Press F1 anywhere in the app (or click ? Help in the title bar) to jump straight to the guide for the exact screen or plugin you are currently using.",
                "Click the live ecosystem pills in the left sidebar (HUE, TV, HA) from any screen to jump directly into that plugin in 1 click.",
                "The Windows system tray icon dynamically changes color and shape to show Connected Idle, Active Playback, Muted, or Standby at a glance.",
            ],
            troubleshooting:
            [
                new("Receiver shows OFFLINE even though it's plugged in?", "Check that Network Control is set to 'Always On' in your Denon AVR on-screen menu (Setup → Network → Network Control → Always On), and verify PC and AVR are on the same subnet."),
                new("Multiple Denon/Marantz devices on the network?", "Open Settings → Auto-Discover and click the exact receiver you want; Denon Desktop remembers its IP and HEOS Player ID automatically."),
            ],
            quickJumps:
            [
                new("Open Settings →", "Settings"),
                new("Open Control Deck →", "Control"),
                new("Run Health Check →", "Diagnostics"),
            ],
            contextSection: "Settings"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "control-deck",
            badge: "DECK",
            accentHex: "#38BDF8",
            category: "Audio & Media",
            title: "Control Deck, 31-Band RTA & Live Lyrics",
            subtitle: "Master volume, OLED Now Playing, Analog VU vs 31-Band FFT Spectrum, synced lyrics, inputs & Zone 2.",
            overview: "The Control Deck is your studio-grade daily console. Every hardware control updates live via HEOS LIVE PUSH and AVR Telnet, while the OLED deck visualizes live Windows WASAPI audio at 60–144 Hz VSync.",
            steps:
            [
                new("01", "Master Volume & dB Display", "Drag the cyan volume slider, scroll your mouse wheel anywhere over the Volume Card (±2 steps), use Fine ±0.5 dB buttons, or click the dB readout to toggle between Relative dB (-80.0 to +18.0 dB) and 0–98 scale."),
                new("02", "Switch Audio Visualizers (VU / RTA 31 / Both)", "On the Hero OLED Now Playing card, click the meter switcher pill to cycle between Illuminated Dual Analog VU Meters, the 31-Band ISO Real-Time FFT Spectrum Analyzer (25 Hz–16 kHz with peak hold), or Both side-by-side."),
                new("03", "Toggle Synced Live Lyrics", "Click the Lyrics button on the OLED deck whenever a track is playing — Denon Desktop queries LRCLIB (zero API key) and highlights the current lyric line in sync with playback."),
                new("04", "Inputs, Sound Modes, ECO & Zone 2", "Switch physical HDMI/HEOS inputs, surround modes (Stereo, Pure Direct, Dolby/DTS, Multi-Ch Stereo), ECO power saver, Sleep Timer, and independent Zone 2 power/volume directly from the deck."),
            ],
            proTips:
            [
                "Analog VU & 31-Band RTA automatically tap live Windows PC WASAPI audio even when you aren't actively using PC Audio Cast.",
                "Click Phone QR in the top-right title bar at any time to grab your Couch Phone Remote QR code.",
            ],
            troubleshooting:
            [
                new("VU meters or RTA bars aren't moving?", "Make sure audio is playing on your Windows default playback device; if playing directly on HEOS without PC audio, the meters gently idle until PC audio or Demo Beat is active."),
                new("Lyrics say 'No synced lyrics found'?", "Some radio streams or YouTube titles with extra tags (like '[Official 4K Video]') may not match LRCLIB; clean Artist – Title tracks match best."),
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
            title: "YouTube, Cinema Mode & Lip-Sync Calibration",
            subtitle: "Stream YouTube audio to Denon HEOS, watch pure full-bleed synced video, and auto-calibrate lip-sync via mic.",
            overview: "Denon Desktop resolves YouTube searches, video URLs, and full playlists directly, transcodes high-bitrate audio (320 kbps MP3 via FFmpeg or Direct M4A fallback) over your local LAN relay (:18925) straight to HEOS, and optionally opens a synced Pure-Video Cinema window on your monitor or TV.",
            steps:
            [
                new("01", "Search or Paste Any YouTube Link", "In YouTube & Music, type a search query or paste any YouTube video/playlist URL and press Enter. Click Play Now or + Queue to build an auto-advancing Up-Next queue."),
                new("02", "Pure-Video Cinema Companion", "Click Open Cinema Video (or enable Auto-Open Cinema) to launch the dedicated WebView2 viewer with all YouTube UI chrome stripped. Press F11 for monitor fullscreen, H to hide the sync toolbar, and Esc to exit progressively."),
                new("03", "1-Click Acoustic Lip-Sync Auto-Calibration", "Because HEOS buffers network audio (~1.5–3.5s), click Auto-Calibrate Sync (Mic) while your speakers are audible. Denon Desktop plays a precision 1,750 Hz 3-burst chirp through your AVR, listens via your PC mic, measures exact arrival latency in milliseconds, and saves CinemaSyncOffsetMs automatically."),
                new("04", "Fine-Tune Sync Anytime", "Inside the Cinema toolbar, nudge sync with ±100ms / ±500ms or hit Re-Sync to Denon if you seek manually."),
            ],
            proTips:
            [
                "Want YouTube directly on your LG Smart TV screen instead of your PC monitor? Open Plugins → LG webOS TV and paste any YouTube URL into 'Cast YouTube URL / Video ID to TV'!",
                "Completed relay stream tokens expire automatically at EOF so HEOS firmware never loops a finished song.",
            ],
            troubleshooting:
            [
                new("HEOS starts loading a YouTube track then stops with no sound?", "Windows Firewall is likely blocking inbound TCP port 18925 from your AVR. Open Cast & Remote and click Allow Firewall (1-click UAC rule), then retry."),
                new("Cinema video is slightly ahead or behind the speakers?", "Run the 1-Click Acoustic Lip-Sync Auto-Calibration or tap -500ms / +500ms on the Cinema toolbar; your calibrated offset is remembered permanently."),
            ],
            quickJumps:
            [
                new("Open YouTube & Music →", "YouTube"),
                new("Open Cast & Firewall Helper →", "CastRemote"),
            ],
            contextSection: "YouTube"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "scenes-acoustics",
            badge: "SCENES",
            accentHex: "#8B5CF6",
            category: "Scenes & Acoustics",
            title: "Smart Scenes 2.0, Automations & Acoustics",
            subtitle: "1-click multi-device macros, App Launch Watchers, Night Guard schedules, and live Audyssey tuning.",
            overview: "Smart Scenes 2.0 turn your Denon AVR, Philips Hue lights, LG webOS TV, and Home Assistant into one coordinated home-theater rig — triggered manually, by keyboard shortcut, from your phone, by launching a PC app, or on a schedule.",
            steps:
            [
                new("01", "Activate or Customize Smart Scenes", "Open Scenes & Acoustics to run presets like Movie Night, Late Night, Pure Hi-Fi, Morning Jazz, or Party Multi-Ch. Click Capture Live AVR inside the Scene Editor to snapshot your receiver's current volume, input, and sound mode."),
                new("02", "Chain Plugin Actions With Delays", "Inside the Scene Editor, attach ordered plugin actions with millisecond delays — for example: Movie Night → LG TV HDMI 2 (0 ms) → Philips Hue Cinema Dim (300 ms) → Home Assistant Close Blinds (500 ms)."),
                new("03", "Build Automation Rules (App Watchers & Night Guard)", "Switch to the Automation Rules sub-tab to add App Launch Watchers (e.g., launching vlc, Plex, or a game automatically activates Movie Night) or Time-of-Day / Night Guard Schedules (Daily / Weekdays / Weekends) with Test Run and a live Activity Log."),
                new("04", "Live Acoustic Tuning Console", "Dial in Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Audio Restorer, Front Panel Dimmer, Subwoofer & Dialog Level trims, and Bass/Treble tone directly over Telnet :23."),
            ],
            proTips:
            [
                "Any Smart Scene can be bound to a system-wide Windows Hotkey in Settings → Keyboard Shortcuts (+ Scene Key) or triggered from the Couch Phone Remote.",
                "Enable 'Show on-screen TV toast on Smart Scenes' in the LG TV plugin to get a slick confirmation banner right on your TV screen whenever a scene fires.",
            ],
            troubleshooting:
            [
                new("App Launch Watcher didn't fire twice in a row?", "Watchers are edge-triggered with a cooldown so they don't spam your receiver while an app stays open. Use the Test Run button to verify any rule immediately."),
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
            title: "Couch Phone Remote 2.0, PC Cast & FLAC Jukebox",
            subtitle: "Control Denon + Hue + LG TV from any phone on Wi-Fi, cast live Windows audio, and stream bit-perfect local FLACs.",
            overview: "Denon Desktop hosts an Obsidian mobile web app and audio relay right on your PC (:18925). Anyone on your home Wi-Fi can scan the QR code — no app store install required — to control volume, inputs, scenes, YouTube queue, Philips Hue lights, and Smart TV actions.",
            steps:
            [
                new("01", "Open Couch Phone Remote (QR Code)", "Click Phone QR in the title bar (or go to Cast & Remote) and scan the QR code with your phone camera. Use the Control, Queue, and Lights tabs to run your whole room from the couch."),
                new("02", "Cast Any PC Audio (WASAPI Loopback)", "Pick your Windows audio output device, choose MP3 320 kbps or Lossless WAV, and click Start Casting PC Audio. Everything playing on your PC (Spotify, games, browsers, DAWs) streams wirelessly to HEOS."),
                new("03", "Local Hi-Res FLAC Jukebox", "Drag and drop .flac, .wav, .mp3, or .m4a files (or whole folders) onto the Local Jukebox drop zone to play bit-perfect audio directly on your Denon AVR."),
                new("04", "One-Click Windows Firewall Rule", "If your phone can't load the QR URL or HEOS can't reach the stream, click Allow Firewall once to open private-network inbound TCP port 18925."),
            ],
            proTips:
            [
                "Bookmark the Phone Remote URL on your iPhone/Android home screen for an instant native-like Couch Remote icon.",
                "PC Audio Cast includes automatic silence keepalive so HEOS never drops the stream when you pause a video on your PC.",
            ],
            troubleshooting:
            [
                new("Phone shows 'This site can't be reached' after scanning QR?", "Verify your phone is on the same Wi-Fi LAN (not Guest Wi-Fi or cellular), ensure Windows Firewall allows port 18925, and check that the correct LAN IP is selected if your PC has VPN/WSL adapters."),
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
            title: "40,000+ Global Radio Explorer & Favorites",
            subtitle: "Discover lossless FLAC internet radio stations, manage custom presets, and play HEOS favorites.",
            overview: "Explore over 40,000 live global stations via Radio Browser with instant genre, country, and FLAC/Hi-Res filtering — or play your receiver's built-in HEOS favorites and custom stream URLs.",
            steps:
            [
                new("01", "Search Global Radio Browser", "Open Stations & Streams, type a genre, city, or station name (e.g. 'jazz', 'lo-fi', 'BBC'), optionally tick FLAC / Hi-Res Only, and press Enter."),
                new("02", "Play or Save to Your Presets", "Click Play directly on any discovered station, or click Save Preset to pin it to your custom Stations list with one-click Low-Volume Launch and Stream Health Probe."),
                new("03", "Sync Receiver HEOS Favorites", "Click Load HEOS Favorites to pull the native favorites already saved on your Denon/Marantz receiver."),
            ],
            proTips:
            [
                "Use Play @ Low Vol when starting late-night radio so you never get blasted if the receiver was left loud from a movie.",
            ],
            troubleshooting:
            [
                new("A radio URL fails to play on HEOS?", "Click Test Stream to probe the HTTP status & content-type. Some stations use HTTPS redirects or HLS playlists that older HEOS firmware can't decode directly — try an MP3/AAC/FLAC direct stream."),
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
            title: "Philips Hue Lighting & 50 Hz DTLS Music Sync",
            subtitle: "Bridge pairing, All Lights & Rooms, 4-mode real-time FFT Music Sync (50 Hz Entertainment API v2) & AVR hooks.",
            overview: "The built-in Philips Hue plugin connects directly over your local LAN to your Hue Bridge (HTTPS v1 + CLIP v2 Entertainment API over DTLS/UDP port 2100) for instant room/light control, album-art ambience, and true 50 Hz beat-reactive lighting.",
            steps:
            [
                new("01", "Discover & Pair Your Hue Bridge", "Open Plugins → Philips Hue (click Bridge Setup ▾ if collapsed), click Auto-Discover, press the physical round link button on top of your Hue Bridge, and click Press Bridge Button & Pair within 30 seconds."),
                new("02", "Control All Lights, Rooms & Mood Swatches", "The All Lights chip is selected by default so every bulb on your bridge appears immediately. Click any Room chip to filter, drag brightness/warmth sliders smoothly, or tap Ambience Presets and native Hue Bridge Scenes."),
                new("03", "Enable 50 Hz Real-Time FFT Music Sync", "Create an Entertainment Area in the Philips Hue mobile app, then click Start Music Sync in Denon Desktop. It automatically activates 50 Hz DTLS/UDP (:2100) streaming across 4 modes: Bass Pulse, Spectrum Split, Album-Art Ambient, and Cinema Subtle."),
                new("04", "Turn On Denon AVR Lighting Automations", "Pick your Target Room and toggle Sync with Smart Scenes, Power ON/Standby sync, or Cinema Pause Lights (brightens gently to 55% on Pause, dims back to 18% on Resume)."),
            ],
            proTips:
            [
                "Click Simulate Beat (125 BPM) to preview the FFT spectrum meters and beat-reactive lighting live even when no music is playing.",
                "Once paired, click Bridge Setup ▾ to keep the pairing bar tucked away while All On, All Off, and Sync stay pinned in the header.",
            ],
            troubleshooting:
            [
                new("Music Sync says 'LAN REST fallback' instead of '50 Hz DTLS'?", "Make sure you have at least one Entertainment Area configured in the official Philips Hue phone app and re-pair once if your bridge key predates Entertainment clientkey capture."),
                new("Only seeing one room's lights?", "Click the 'All Lights' chip at the top of Master & Rooms or click 'Show All Lights' in the Individual Lights header."),
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
            title: "LG webOS Smart TV 2.0 & Virtual Magic Remote",
            subtitle: "Persistent wss://:3001 connection, dynamic apps & HDMI inputs, OLED Screen Off, eARC switcher & Magic Remote.",
            overview: "Smart TV Control 2.0 maintains a persistent encrypted SSAP WebSocket (wss://:3001 with ws://:3000 fallback) to your LG webOS TV with live state subscriptions, full Virtual Magic Remote, PC-to-TV keyboard typing, OLED Screen Off (Audio-Only Mode), and Denon AVR eARC coordination.",
            steps:
            [
                new("01", "Enable Network Control on Your LG TV", "On your LG TV, check Settings → General → External Devices → TV On With Mobile (Turn on via Wi-Fi/LAN = ON) and Settings → Network → LG Connect Apps = ON."),
                new("02", "Discover, Auto-MAC & Pair", "Open Plugins → LG webOS TV, click Setup ▾ → Auto-Discover TV (or enter the TV IP and click Auto-MAC so Wake-on-LAN works automatically). Make sure the TV is powered ON, click Pair with LG TV, and press Accept on the 'Denon Desktop' TV prompt."),
                new("03", "Launch Real Installed Apps & Cast YouTube URLs", "Once the badge turns green (LIVE • WSS :3001), your TV's real HDMI ports (with green signal dots) and every installed app load automatically. Paste any YouTube link into 'Cast YouTube URL' and press Enter to play it directly on the TV."),
                new("04", "Use OLED Screen Off, eARC Switcher & Magic Remote", "Click OLED Screen Off to blank your OLED panel while keeping eARC music playing (zero burn-in!), switch TV audio straight to HDMI ARC/eARC, navigate with the D-Pad, or type into TV search fields from your PC keyboard."),
                new("05", "Enable Denon AVR Coordination", "Tick Wake TV on Denon ON, Power off on Standby, Auto-Switch to HDMI ARC/eARC, Mute TV Speakers, Scene Toasts, or OLED Screen Off on Music Scenes — every toggle saves automatically."),
            ],
            proTips:
            [
                "Press Enter directly inside the Cast YouTube URL, PC Keyboard → TV, or On-Screen Toast boxes to send without clicking a button.",
                "If Magic Remote buttons or Installed Apps return '401 insufficient permissions' on an older pairing key, open Setup ▾ and click Re-Pair (Full Permissions) once.",
            ],
            troubleshooting:
            [
                new("Wake TV (WoL) doesn't turn the TV on?", "Verify the MAC address is filled in (click Auto-MAC while the TV is awake) and confirm 'Turn on via Wi-Fi / LAN' is enabled on the LG TV."),
                new("TV shows 'PAIRED • STANDBY' after turning it off?", "That's normal — LG TVs shut down the WebSocket in standby. Click Wake TV (WoL) and Denon Desktop automatically reconnects ~6 seconds after webOS boots."),
            ],
            quickJumps:
            [
                new("Open LG webOS Smart TV →", "Plugins", "smart-tv"),
            ],
            contextSection: "Plugins",
            contextPluginId: "smart-tv"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "plugin-home-assistant",
            badge: "HA",
            accentHex: "#10B981",
            category: "Plugins",
            title: "Home Assistant REST & Webhook Bridge",
            subtitle: "Long-Lived Access Token setup, triggering scenes/scripts/switches, and live sensor.denon_desktop_receiver telemetry.",
            overview: "Bridge Denon Desktop directly with your Home Assistant server over local REST & Webhooks — trigger blinds, smart plugs, scenes, and scripts from Denon Desktop or your Phone Remote while publishing real-time AVR telemetry to Home Assistant.",
            steps:
            [
                new("01", "Create a Long-Lived Access Token in Home Assistant", "Open Home Assistant in your browser → click your Profile name (bottom-left) → Security tab → scroll to Long-Lived Access Tokens → click Create Token (name it 'Denon Desktop') and copy the JWT string."),
                new("02", "Connect & Sync Entities", "In Plugins → Home Assistant, enter your URL (e.g. http://homeassistant.local:8123 or http://192.168.1.50:8123), paste the token, optionally add a Webhook ID, and click Save & Sync."),
                new("03", "Filter & Trigger Entities", "Use the All / Scenes / Scripts / Switches / Lights chips and search box to find entities quickly. Switches and lights show a live green ON badge and toggle state immediately."),
                new("04", "Use Live Telemetry in Home Assistant Automations", "With auto-push enabled, Denon Desktop publishes sensor.denon_desktop_receiver with state (playing/paused/standby) plus attributes: model, volume, muted, input_source, sound_mode, now_playing, and last_scene."),
            ],
            proTips:
            [
                "No Home Assistant server handy yet? Click Try Demo to explore virtual blinds, subwoofer smart plugs, and scenes right away.",
                "You can also drop custom .NET 10 plugins implementing IDenonPlugin into the External Plugins folder (Plugins → Plugins Folder) and click Rescan DLLs.",
            ],
            troubleshooting:
            [
                new("Save & Sync returns 401 Unauthorized?", "Make sure you copied the complete Long-Lived Access Token without extra spaces or quotes, and check that your Home Assistant URL uses the right http:// or https:// scheme and port (default :8123)."),
            ],
            quickJumps:
            [
                new("Open Home Assistant Bridge →", "Plugins", "home-assistant"),
            ],
            contextSection: "Plugins",
            contextPluginId: "home-assistant"));

        _allHelpTopics.Add(new HelpGuideTopic(
            id: "shortcuts-hotkeys",
            badge: "KEYS",
            accentHex: "#A855F7",
            category: "Reference",
            title: "Keyboard Shortcuts, Global Hotkeys & Tray Flyout",
            subtitle: "Complete shortcut reference, interactive key recorder, 4-layer collision checker, and Scene/Plugin hotkeys.",
            overview: "Control your Denon AVR, Smart Scenes, Philips Hue lights, and LG TV from any Windows app or fullscreen game using system-wide Global Hotkeys, fast in-app shortcuts, and the floating taskbar Tray Flyout.",
            steps:
            [
                new("01", "Customize Any Shortcut in Settings", "Open Settings → Keyboard Shortcuts, click any shortcut pill, and press your desired key combination. Press Esc to cancel, or Backspace / ✕ to clear."),
                new("02", "Bind Any Smart Scene or Plugin Action", "Click + Scene Key or + Plugin Key to assign a global Windows hotkey to Movie Night, Philips Hue Music Sync, OLED Screen Off, or All Lights Off."),
                new("03", "Check 4-Layer Collision Badges", "Denon Desktop checks every binding live for internal duplicates (CONFLICT), Win32 OS ownership (OS TAKEN), reserved Windows combos (RESERVED), and cross-scope overlap (OVERLAP)."),
                new("04", "Use the System Tray Mini-Flyout", "Left-click the Denon Desktop tray icon anytime for an instant floating mini-controller (Power, Volume, Mute, Now Playing) without pulling up the full window."),
            ],
            proTips:
            [
                "Global hotkeys use a hidden Win32 message-only window so they keep working smoothly even when Denon Desktop is minimized to the system tray.",
            ],
            troubleshooting:
            [
                new("A Global Hotkey shows 'OS TAKEN'?", "Another running app (like NVIDIA Overlay, PowerToys, Discord, or Spotify) already registered that Win32 hotkey. Record a different modifier combo (such as Ctrl+Alt+Shift+...) to resolve it immediately."),
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
            title: "Network Ports, Firewall & Troubleshooting FAQ",
            subtitle: "Every LAN port used by Denon Desktop, 1-click Health Check, raw Telnet/HEOS Console, and top fixes.",
            overview: "Everything in Denon Desktop runs 100% locally on your LAN for zero latency and complete privacy. Below is every protocol & port used, plus fast fixes for the most common home-network snags.",
            steps:
            [
                new("01", "Run the 5-Point Automated Health Check", "Open Health & Support and click Run Health Check to test Network Reachability, AVR Telnet (:23), HEOS Engine (:1255), Audio Output Readiness, and Radio Stream health in one click."),
                new("02", "Use the Built-In AVR / HEOS Diagnostic Console", "Need to test a raw command? In Health & Support, toggle AVR (:23) or HEOS (:1255) and run commands like SI?, MV?, or heos://player/get_players with live output."),
                new("03", "Fix Silent Audio in One Click", "If the receiver is on but silent, click Fix Silent Audio in Health & Support to unmute, verify audible volume, and wake standby."),
                new("04", "Inspect Logs or Copy a Diagnostic Report", "Filter live app logs by Info / Warning / Error, open the logs folder (%LocalAppData%\\DenonDesktop\\logs), or click Copy Diagnostic Report."),
            ],
            proTips:
            [
                "Assign DHCP static IP reservations in your router for your Denon AVR, Philips Hue Bridge, and LG TV so their local IPs never drift after a router reboot.",
            ],
            troubleshooting:
            [
                new("YouTube / PC Audio Cast / Phone Remote won't connect?", "Run Allow Firewall in Cast & Remote (opens private-network TCP 18925) and confirm your Windows network profile is set to 'Private' (not 'Public')."),
                new("AVR Telnet commands lag or fail while HEOS works?", "Denon AVRs only accept one simultaneous Telnet (:23) client. Close other Denon telnet tools or click Reset Connections in Health & Support."),
                new("LG TV pairing fails with 'Unable to connect'?", "Ensure the TV is powered ON (not standby), on the same subnet, and LG Connect Apps is enabled in TV Settings. Denon Desktop automatically tries secure wss://:3001 first, then ws://:3000."),
                new("Smart TV or Hue settings aren't showing up in Phone Remote?", "Check that the plugin is Enabled in Plugins & Extensions — Phone Remote's Lights tab dynamically loads all enabled plugins' actions."),
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
