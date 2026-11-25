using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace Denon.App.Core.Services;

/// <summary>
/// One thing that happened in the room: a receiver change, a TV app, a cast session, a light change,
/// a plugin event or a webhook call. The same shape as a line in the event log.
/// </summary>
public sealed record RoomEvent(
    DateTimeOffset Timestamp,
    string Stream,
    string Device,
    string Event,
    string Origin,
    IReadOnlyDictionary<string, object?>? Data)
{
    /// <summary>A data value as plain text: booleans as "true"/"false", numbers in invariant culture.</summary>
    public string? GetText(string key)
    {
        if (Data is null || !Data.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b ? "true" : "false",
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            JsonElement el => el.GetRawText(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}

/// <summary>
/// Live, in-process feed of room events. <see cref="EventTelemetryLog.Record"/> publishes every event here,
/// whether or not event logging is turned on, so automations can react to events without the log.
/// Handlers run on one background dispatcher, one event at a time, in order; a slow or failing handler
/// does not block the code that raised the event.
/// </summary>
public sealed class RoomEventBus : IDisposable
{
    private const int RecentCapacity = 200;

    private readonly AppLogStore _logStore;
    private readonly Channel<RoomEvent> _queue = Channel.CreateBounded<RoomEvent>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly object _gate = new();
    private readonly Queue<RoomEvent> _recent = new(RecentCapacity);
    private readonly Task _dispatcher;
    private Action<RoomEvent>[] _handlers = [];
    private bool _disposed;

    public RoomEventBus(AppLogStore logStore)
    {
        _logStore = logStore;
        _dispatcher = Task.Run(DispatchLoopAsync);
    }

    public void Publish(RoomEvent roomEvent)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_recent.Count >= RecentCapacity)
            {
                _recent.Dequeue();
            }

            _recent.Enqueue(roomEvent);
        }

        _queue.Writer.TryWrite(roomEvent);
    }

    /// <summary>Calls <paramref name="handler"/> for every event published after this call. Dispose to stop.</summary>
    public IDisposable Subscribe(Action<RoomEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }

        return new Subscription(this, handler);
    }

    /// <summary>The latest events, newest first (at most 200 are kept).</summary>
    public IReadOnlyList<RoomEvent> GetRecent(int max = 50)
    {
        lock (_gate)
        {
            return _recent.Reverse().Take(Math.Clamp(max, 1, RecentCapacity)).ToList();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _dispatcher.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down.
        }
    }

    private void Unsubscribe(Action<RoomEvent> handler)
    {
        lock (_gate)
        {
            _handlers = _handlers.Where(h => h != handler).ToArray();
        }
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var roomEvent in _queue.Reader.ReadAllAsync())
        {
            Action<RoomEvent>[] handlers;
            lock (_gate)
            {
                handlers = _handlers;
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(roomEvent);
                }
                catch (Exception ex)
                {
                    _logStore.Warning($"[EventBus] A handler failed on {roomEvent.Event}: {ex.Message}");
                }
            }
        }
    }

    private sealed class Subscription(RoomEventBus bus, Action<RoomEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                bus.Unsubscribe(handler);
            }
        }
    }
}
