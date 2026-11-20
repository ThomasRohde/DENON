using System.Net.Http;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class StreamProbeService : IDisposable
{
    private readonly HttpClient _httpClient;

    public StreamProbeService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DenonDesktop/2.0");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "1");
    }

    public async Task<StreamProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return new StreamProbeResult(false, null, null, null, null, null, "Invalid URL format.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return new StreamProbeResult(false, null, null, null, null, null, "Stream URL must start with http:// or https://.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var statusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var stationName = TryGetHeader(response, "icy-name");
            var bitrate = TryGetHeader(response, "icy-br");
            var resolvedUrl = response.RequestMessage?.RequestUri?.ToString();

            if (!response.IsSuccessStatusCode)
            {
                return new StreamProbeResult(
                    false,
                    statusCode,
                    contentType,
                    stationName,
                    bitrate,
                    resolvedUrl,
                    $"HTTP {statusCode} ({response.ReasonPhrase})");
            }

            var details = new List<string> { $"HTTP {statusCode}" };
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                details.Add(contentType);
            }

            if (!string.IsNullOrWhiteSpace(bitrate))
            {
                details.Add($"{bitrate} kbps");
            }

            if (!string.IsNullOrWhiteSpace(stationName))
            {
                details.Add(stationName);
            }

            return new StreamProbeResult(
                true,
                statusCode,
                contentType,
                stationName,
                bitrate,
                resolvedUrl,
                string.Join(" • ", details));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StreamProbeResult(false, null, null, null, null, null, "Stream probe timed out after 6s.");
        }
        catch (Exception exception)
        {
            return new StreamProbeResult(false, null, null, null, null, null, exception.Message);
        }
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var headerValues))
        {
            return headerValues.FirstOrDefault();
        }

        if (response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            return contentValues.FirstOrDefault();
        }

        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
