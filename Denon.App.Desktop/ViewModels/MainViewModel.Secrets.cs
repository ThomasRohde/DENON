using Denon.App.Desktop.Infrastructure;

namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// Pairing secrets on screen: the Phone Remote QR code (Cast page; it holds the pairing key), and the MCP bearer token and
/// the client config that contains it (Brain page). They are hidden until the user clicks Show (or picks Owner QR or
/// Guest QR), and hidden again when the page changes or the window hides, so a screenshot or screen share does not
/// leak them. Copy buttons still copy the real values.
/// </summary>
public sealed partial class MainViewModel
{
    private const string Dots = "••••••••";

    private bool _areSecretsShown;
    private RelayCommand? _toggleSecretsCommand;
    private RelayCommand? _showPhoneQrCommand;

    public bool AreSecretsShown
    {
        get => _areSecretsShown;
        private set
        {
            if (SetProperty(ref _areSecretsShown, value))
            {
                OnPropertyChanged(nameof(AreSecretsHidden));
                OnPropertyChanged(nameof(SecretsToggleLabel));
                OnPropertyChanged(nameof(RemoteQrToggleLabel));
                NotifySecretDisplays();
            }
        }
    }

    public bool AreSecretsHidden => !AreSecretsShown;

    public RelayCommand ToggleSecretsCommand => _toggleSecretsCommand ??= new RelayCommand(_ => AreSecretsShown = !AreSecretsShown);

    /// <summary>The title bar's Phone QR: opens the Cast page with the QR shown (the user asked for it).</summary>
    public RelayCommand ShowPhoneQrCommand => _showPhoneQrCommand ??= new RelayCommand(_ =>
    {
        ActiveSection = "CastRemote";
        ShowSecrets();
    });

    public string SecretsToggleLabel => AreSecretsShown ? "Hide" : "Show";

    public string RemoteQrToggleLabel => AreSecretsShown ? "Hide QR" : "Show QR";

    public string McpBearerTokenDisplay => AreSecretsShown || string.IsNullOrEmpty(McpBearerToken) ? McpBearerToken : Dots;

    public string McpClientConfigPreviewDisplay =>
        AreSecretsShown || string.IsNullOrEmpty(McpBearerToken) || string.IsNullOrEmpty(McpClientConfigPreview)
            ? McpClientConfigPreview
            : McpClientConfigPreview.Replace(McpBearerToken, Dots, StringComparison.Ordinal);

    /// <summary>Hides the secrets again (page change, window hidden).</summary>
    public void HideSecrets() => AreSecretsShown = false;

    private void ShowSecrets() => AreSecretsShown = true;

    private void NotifySecretDisplays()
    {
        OnPropertyChanged(nameof(McpBearerTokenDisplay));
        OnPropertyChanged(nameof(McpClientConfigPreviewDisplay));
    }
}
