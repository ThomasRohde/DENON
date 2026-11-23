namespace Denon.App.PluginSdk;

public interface IPluginLogger
{
    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);
}

public interface IPluginEventLogger
{
    /// <summary>
    /// Emits a structured, habit-ready JSONL telemetry event into this plugin's event stream
    /// (or a shared domain stream such as "tv", "lighting", or "media").
    /// </summary>
    void RecordEvent(
        string eventName,
        IReadOnlyDictionary<string, object?>? data = null,
        string? stream = null,
        string origin = "plugin");
}

public interface IPluginStorage
{
    string PluginDataDirectory { get; }

    Task<T> LoadConfigAsync<T>(Func<T> defaultFactory, CancellationToken cancellationToken = default)
        where T : class;

    Task SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default)
        where T : class;
}

public interface IPluginReceiverBridge
{
    PluginReceiverSnapshot CurrentSnapshot { get; }
}

public interface IPluginContext
{
    string PluginId { get; }

    IPluginLogger Logger { get; }

    IPluginEventLogger Events { get; }

    IPluginStorage Storage { get; }

    IPluginReceiverBridge Receiver { get; }

    /// <summary>
    /// Receiver command channel. It is null when the plugin manifest does not declare
    /// <see cref="PluginCapabilities.ReceiverControl"/>, or when the host does not support it.
    /// </summary>
    IPluginReceiverControl? ReceiverControl => null;

    /// <summary>
    /// Creates an HttpClient suitable for communicating with external or local LAN hardware bridges.
    /// When <paramref name="allowLocalSelfSignedTls"/> is true, self-signed TLS certificates on RFC1918
    /// private LAN addresses (such as Philips Hue Bridges) are permitted.
    /// </summary>
    HttpClient CreateHttpClient(bool allowLocalSelfSignedTls = false, TimeSpan? timeout = null);

    /// <summary>
    /// Publishes the plugin's live health state and human-readable status message to the Plugins UI.
    /// </summary>
    void ReportStatus(string statusText, PluginHealthState health);
}
