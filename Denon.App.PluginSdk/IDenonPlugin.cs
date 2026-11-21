namespace Denon.App.PluginSdk;

/// <summary>
/// Core contract implemented by every built-in or external drop-in Denon Desktop plugin.
/// External plugins can be packaged as a .NET 10 class library referencing Denon.App.PluginSdk
/// and dropped into %LocalAppData%\DenonDesktop\plugins\external\.
/// </summary>
public interface IDenonPlugin : IAsyncDisposable
{
    PluginManifest Manifest { get; }

    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<PluginActionDescriptor> GetActions();

    Task<PluginActionResult> ExecuteActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default);

    Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional interface for plugins that provide an interactive WPF dashboard or settings control
/// hosted inside the Denon Desktop Plugins view.
/// </summary>
public interface IPluginViewProvider
{
    /// <summary>
    /// Returns a WPF FrameworkElement (typically a UserControl) for this plugin's dashboard UI.
    /// Called on the WPF UI thread.
    /// </summary>
    object? CreateDashboardView();
}
