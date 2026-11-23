namespace Denon.Plugin.AppleTv.Protocol.Mrp;

/// <summary>What the Apple TV plays now, merged from MRP state messages.</summary>
internal sealed record NowPlayingSnapshot(
    string? AppBundleId,
    string? AppName,
    MrpPlaybackState State,
    string? Title,
    string? Subtitle,
    string? Artist,
    string? Album,
    string? SeriesName,
    int? Season,
    int? Episode,
    double? DurationSeconds,
    double? ElapsedSeconds,
    bool ArtworkAvailable,
    string? ArtworkUrl,
    string? ContentIdentifier,
    DateTimeOffset UpdatedUtc)
{
    public static NowPlayingSnapshot Empty { get; } = new(null, null, MrpPlaybackState.Unknown, null, null, null, null, null, null, null, null, null, false, null, null, DateTimeOffset.MinValue);

    public bool HasMedia => !string.IsNullOrEmpty(Title) || State is MrpPlaybackState.Playing or MrpPlaybackState.Paused;
}

/// <summary>
/// Tracks now-playing state per app (client) and per player, like pyatv's mrp/player_state.py: apps can have
/// several players (DRTV: an "avkit-…" player that plays and an empty "MediaRemote-DefaultPlayer"). The
/// active app comes from SET_NOW_PLAYING_CLIENT, its active player from SET_NOW_PLAYING_PLAYER; SET_STATE and
/// UPDATE_CONTENT_ITEM update the player in their player path.
/// </summary>
internal sealed class MrpNowPlayingTracker
{
    public const string DefaultPlayerId = "MediaRemote-DefaultPlayer";

    private readonly object _sync = new();
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private string? _activeBundle;

    public NowPlayingSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                if (_activeBundle is null || !_clients.TryGetValue(_activeBundle, out var client))
                {
                    return NowPlayingSnapshot.Empty with { AppBundleId = _activeBundle };
                }

                var player = client.ActivePlayer();
                return player is null
                    ? NowPlayingSnapshot.Empty with { AppBundleId = _activeBundle, AppName = client.Name }
                    : player.Snapshot(_activeBundle, client.Name);
            }
        }
    }

    /// <summary>Applies one received ProtocolMessage. Returns true when the visible state may have changed.</summary>
    public bool Apply(ProtoReader message)
    {
        var type = (int)(message.Uint(1) ?? 0);
        lock (_sync)
        {
            switch (type)
            {
                case MrpType.SetNowPlayingClient:
                    var clientInfo = message.Message(MrpExtension.SetNowPlayingClient)?.Message(1);
                    _activeBundle = clientInfo?.String(2);
                    if (_activeBundle is not null)
                    {
                        Client(_activeBundle).SetName(clientInfo?.String(7));
                    }

                    return true;

                case MrpType.SetNowPlayingPlayer:
                    var (playerBundle, playerId) = PathOf(message.Message(MrpExtension.SetNowPlayingPlayer)?.Message(1));
                    if (playerBundle is null)
                    {
                        return false;
                    }

                    Client(playerBundle).ActivePlayerId = playerId ?? DefaultPlayerId;
                    return true;

                case MrpType.SetState:
                    var state = message.Message(MrpExtension.SetState);
                    if (state is null)
                    {
                        return false;
                    }

                    var path = state.Message(9);
                    var (bundle, id) = PathOf(path);
                    bundle ??= _activeBundle;
                    if (bundle is null)
                    {
                        return false;
                    }

                    var client = Client(bundle);
                    client.SetName(path?.Message(2)?.String(7) ?? state.String(5));
                    var player = client.Player(id ?? DefaultPlayerId);

                    if (state.Uint(6) is { } playback)
                    {
                        player.State = (MrpPlaybackState)(int)playback;
                    }

                    if (state.Message(1) is { } info)
                    {
                        player.ApplyNowPlayingInfo(info);
                    }

                    if (state.Message(3) is { } queue)
                    {
                        var items = queue.Messages(2).ToList();
                        var location = (int)(queue.Int(1) ?? 0);
                        var item = location >= 0 && location < items.Count ? items[location] : items.FirstOrDefault();
                        if (item is not null)
                        {
                            player.ApplyContentItem(item, replace: true);
                        }
                    }

                    return true;

                case MrpType.UpdateContentItem:
                    var update = message.Message(MrpExtension.UpdateContentItem);
                    var (updateBundle, updateId) = PathOf(update?.Message(2));
                    updateBundle ??= _activeBundle;
                    if (update is null || updateBundle is null)
                    {
                        return false;
                    }

                    var updateClient = Client(updateBundle);
                    var target = updateClient.Player(updateId ?? updateClient.ActivePlayerId ?? DefaultPlayerId);
                    var updated = false;
                    foreach (var contentItem in update.Messages(1))
                    {
                        updated |= target.ApplyContentItem(contentItem, replace: false);
                    }

                    return updated;

                default:
                    return false;
            }
        }
    }

    /// <summary>PlayerPath: client (2) → bundleIdentifier (2); player (3) → identifier (1).</summary>
    private static (string? Bundle, string? PlayerId) PathOf(ProtoReader? path) =>
        (path?.Message(2)?.String(2), path?.Message(3)?.String(1));

    private ClientState Client(string bundle)
    {
        if (!_clients.TryGetValue(bundle, out var client))
        {
            client = new ClientState();
            _clients[bundle] = client;
        }

        return client;
    }

    private sealed class ClientState
    {
        private readonly Dictionary<string, PlayerState> _players = new(StringComparer.Ordinal);

        public string? Name { get; private set; }

        public string? ActivePlayerId { get; set; }

        public void SetName(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                Name = name;
            }
        }

        public PlayerState Player(string id)
        {
            if (!_players.TryGetValue(id, out var player))
            {
                player = new PlayerState();
                _players[id] = player;
            }

            return player;
        }

        /// <summary>
        /// The named active player (null while it has no state yet: never fall back to an older player then);
        /// without a named player: the most recently updated player with content, else the default player.
        /// </summary>
        public PlayerState? ActivePlayer()
        {
            if (ActivePlayerId is not null)
            {
                return _players.GetValueOrDefault(ActivePlayerId);
            }

            return _players.Values.Where(p => p.HasContent).OrderByDescending(p => p.Updated).FirstOrDefault()
                   ?? _players.GetValueOrDefault(DefaultPlayerId);
        }
    }

    private sealed class PlayerState
    {
        private string? _itemId;
        private string? _title;
        private string? _subtitle;
        private string? _artist;
        private string? _album;
        private string? _series;
        private int? _season;
        private int? _episode;
        private double? _duration;
        private double? _elapsed;
        private bool _artworkAvailable;
        private string? _artworkUrl;
        private string? _contentId;

        public MrpPlaybackState State { get; set; }

        public DateTimeOffset Updated { get; private set; }

        public bool HasContent => _title is not null || _itemId is not null;

        public void ApplyNowPlayingInfo(ProtoReader info)
        {
            _title = info.String(9) ?? _title;
            _artist = info.String(2) ?? _artist;
            _album = info.String(1) ?? _album;
            _duration = info.Double(3) ?? _duration;
            _elapsed = info.Double(4) ?? _elapsed;
            Updated = DateTimeOffset.UtcNow;
        }

        /// <summary>ContentItem: identifier (1), metadata (2), artworkData (3). False for an update to another item.</summary>
        public bool ApplyContentItem(ProtoReader item, bool replace)
        {
            var id = item.String(1);
            if (!replace && id is not null && _itemId is not null && id != _itemId)
            {
                return false;
            }

            if (replace && id != _itemId)
            {
                _title = _subtitle = _artist = _album = _series = _artworkUrl = _contentId = null;
                _season = _episode = null;
                _duration = _elapsed = null;
                _artworkAvailable = false;
            }

            _itemId = id ?? _itemId;
            if (item.Message(2) is { } m)
            {
                _title = m.String(1) ?? _title;
                _subtitle = m.String(2) ?? _subtitle;
                _album = m.String(6) ?? _album;
                _artist = m.String(7) ?? m.String(8) ?? _artist;
                _season = m.Int(10) is { } s ? (int)s : _season;
                _episode = m.Int(11) is { } e ? (int)e : _episode;
                _duration = m.Double(14) ?? _duration;
                _artworkAvailable = m.Bool(19) ?? _artworkAvailable;
                _elapsed = m.Double(35) ?? _elapsed;
                _contentId = m.String(44) ?? _contentId;
                _series = m.String(63) ?? _series;
                _artworkUrl = m.String(70) ?? _artworkUrl;
            }

            if (item.Bytes(3) is { Length: > 0 })
            {
                _artworkAvailable = true;
            }

            Updated = DateTimeOffset.UtcNow;
            return true;
        }

        public NowPlayingSnapshot Snapshot(string bundle, string? appName) => new(
            bundle, appName, State, _title, _subtitle, _artist, _album, _series, _season, _episode,
            _duration, _elapsed, _artworkAvailable, _artworkUrl, _contentId, Updated);
    }
}
