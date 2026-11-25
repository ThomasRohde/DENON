using System.Diagnostics;
using System.IO;
using System.Windows;
using Denon.App.Core.Infrastructure;
using Denon.App.Desktop.Services;
using Microsoft.Web.WebView2.Core;

namespace Denon.App.Desktop;

/// <summary>
/// The automation node editor (Automations/*.html, served by <see cref="InsightsServer"/> on 127.0.0.1) in its own
/// window. The page stays on that server: links elsewhere open in the browser.
/// </summary>
public partial class AutomationEditorWindow : Window
{
    private readonly InsightsServer _server;
    private bool _ready;
    private string _lastUrl = string.Empty;

    public AutomationEditorWindow(InsightsServer server)
    {
        _server = server;
        InitializeComponent();
    }

    /// <summary>Shows the editor, opened on <paramref name="graphId"/> when given.</summary>
    public async Task ShowEditorAsync(string? graphId)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        try
        {
            // Already open: only switch the automation (the page asks first if there are unsaved changes).
            if (_ready && EditorWebView.Source is { } source && IsOwnPage(source.AbsoluteUri) &&
                source.AbsolutePath.StartsWith("/automations", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(graphId))
                {
                    await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                        // Clear first, so the same id twice still fires hashchange.
                        $"location.hash = ''; location.hash = {System.Text.Json.JsonSerializer.Serialize(Uri.EscapeDataString(graphId))};");
                }

                return;
            }

            var url = await _server.StartAutomationEditorAsync();
            _lastUrl = string.IsNullOrWhiteSpace(graphId) ? url : $"{url}#{Uri.EscapeDataString(graphId)}";
            if (!_ready)
            {
                var userDataDir = Path.Combine(AppDataPaths.RootDirectory, "webview2");
                Directory.CreateDirectory(userDataDir);
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDir);
                await EditorWebView.EnsureCoreWebView2Async(env);
                var core = EditorWebView.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += OnNewWindowRequested;
                _ready = true;
            }

            EditorWebView.CoreWebView2.Navigate(_lastUrl);
        }
        catch (Exception ex)
        {
            FailureText.Text = $"The editor could not open here: {ex.Message}";
            FailurePanel.Visibility = Visibility.Visible;
            EditorWebView.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsOwnPage(string uri) =>
        _server.Address is { } address && uri.StartsWith(address, StringComparison.OrdinalIgnoreCase);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsOwnPage(e.Uri))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUrl.Length > 0)
        {
            OpenExternal(_lastUrl);
        }
    }
}
