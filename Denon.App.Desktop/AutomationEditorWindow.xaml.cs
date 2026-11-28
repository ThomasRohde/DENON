using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Denon.App.Core.Infrastructure;
using Denon.App.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using MessageBox = System.Windows.MessageBox;

namespace Denon.App.Desktop;

/// <summary>
/// The automation node editor (Automations/*.html, served by <see cref="InsightsServer"/> on 127.0.0.1) in its own
/// window. The page stays on that server: links elsewhere open in the browser.
/// </summary>
public partial class AutomationEditorWindow : Window
{
    private readonly InsightsServer _server;
    private bool _ready;
    private bool _closeConfirmed;
    private bool _askingToClose;
    private string _lastUrl = string.Empty;

    public AutomationEditorWindow(InsightsServer server)
    {
        _server = server;
        InitializeComponent();
        Closing += OnClosing;
        SourceInitialized += (_, _) => ApplyFrameTheme();
        Desktop.Infrastructure.Theming.ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            Desktop.Infrastructure.Theming.ThemeManager.ThemeChanged -= OnThemeChanged;
            EditorWebView.Dispose();
        };
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>The Windows title bar is dark in a dark theme and light in a light one.</summary>
    private void ApplyFrameTheme()
    {
        try
        {
            var dark = Desktop.Infrastructure.Theming.ThemeManager.Current.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch
        {
            // Older Windows: no dark frame.
        }
    }

    /// <summary>A theme switch: the frame, and the page's theme.css again (new address, so no cached copy).</summary>
    private async void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyFrameTheme();
        if (!_ready || EditorWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                "(() => { const l = document.getElementById('theme-css'); if (l) l.href = '/theme.css?v=' + Date.now(); })();");
        }
        catch
        {
            // The page is not ready; it loads the new theme when it opens.
        }
    }

    /// <summary>
    /// A closing window would drop the page and its unsaved changes without a word (the page's own "leave?" prompt
    /// does not run when the window closes). So the window asks the page, and with unsaved changes offers Save,
    /// Discard or Cancel. When the app exits it closes without asking.
    /// </summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || !_ready || App.IsExiting || EditorWebView.CoreWebView2 is null)
        {
            return;
        }

        e.Cancel = true; // Decide after asking the page (async), then close again.
        if (_askingToClose)
        {
            return;
        }

        _askingToClose = true;
        try
        {
            string dirty;
            try
            {
                dirty = await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.denonEditor?.isDirty() === true");
            }
            catch
            {
                dirty = "false"; // The page is gone or broken: nothing to keep.
            }

            if (dirty != "true")
            {
                CloseConfirmed();
                return;
            }

            var answer = MessageBox.Show(
                this,
                "This automation has changes that are not saved.\n\nYes: save, then close.\nNo: close without saving.\nCancel: keep editing.",
                "Save the automation?",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);

            switch (answer)
            {
                case MessageBoxResult.Yes:
                    // The page saves and answers with a "saved" web message (OnWebMessageReceived closes on success).
                    try
                    {
                        await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.denonEditor?.saveForClose()");
                    }
                    catch (Exception ex)
                    {
                        // Keep the window open: the changes are still in the page.
                        MessageBox.Show(this, $"The automation could not be saved: {ex.Message}", "Not saved",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    break;
                case MessageBoxResult.No:
                    CloseConfirmed();
                    break;
            }
        }
        finally
        {
            _askingToClose = false;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "saved")
            {
                return;
            }

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                CloseConfirmed();
            }
            else
            {
                MessageBox.Show(this, "The automation was not saved. The editor shows what to fix.", "Not saved",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not one of ours.
        }
    }

    private void CloseConfirmed()
    {
        _closeConfirmed = true;
        Dispatcher.BeginInvoke(Close);
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
                core.WebMessageReceived += OnWebMessageReceived;
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
