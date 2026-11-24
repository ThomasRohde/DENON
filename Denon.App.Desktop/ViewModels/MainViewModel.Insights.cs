using System.Windows.Input;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Services;

namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// Settings → Open Insights: starts the Insights page (a local web server, this PC only) and opens it in the browser.
/// </summary>
public sealed partial class MainViewModel
{
    private const string InsightsIdleText = "Watch time, your weekly rhythm, volume and who is in control, in your browser. Only this PC can open it.";

    private InsightsServer? _insightsServer;
    private ICommand? _openInsightsCommand;
    private string _insightsStatusText = InsightsIdleText;

    public string InsightsStatusText
    {
        get => _insightsStatusText;
        private set => SetProperty(ref _insightsStatusText, value);
    }

    public ICommand OpenInsightsCommand => _openInsightsCommand ??= new RelayCommand(_ => _ = OpenInsightsAsync());

    /// <summary>The app calls this once at start-up.</summary>
    public void AttachInsights(InsightsServer server) => _insightsServer = server;

    private async Task OpenInsightsAsync()
    {
        if (_insightsServer is null)
        {
            return;
        }

        string url;
        try
        {
            url = await _insightsServer.StartAsync();
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Could not start Insights: {exception.Message}");
            InsightsStatusText = $"Could not start Insights: {exception.Message}";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            InsightsStatusText = $"Insights is open in your browser ({_insightsServer.Address}, this PC only).";
            StatusMessage = "Insights opened in your browser.";
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Could not open the browser for Insights: {exception.Message}");
            InsightsStatusText = $"Windows could not open the browser. Open this address yourself: {url}";
        }
    }
}
