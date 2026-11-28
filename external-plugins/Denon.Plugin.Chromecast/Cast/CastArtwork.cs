using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Denon.Plugin.Chromecast.Cast;

/// <summary>
/// Fetches the artwork a Cast app reports. The URL comes from the device, so it is checked: http(s) only, and never
/// this PC (loopback) or a link-local address (169.254.x.x holds cloud metadata services). Redirects are followed by
/// hand and checked the same way. The body is read up to a size limit and never buffered whole first.
/// </summary>
internal static class CastArtwork
{
    private const int MaxRedirects = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The artwork bytes and media type, or null when the URL is refused, fails, or is too large.</summary>
    public static Task<(byte[] Bytes, string MediaType)?> FetchAsync(Uri uri, int maxBytes, CancellationToken ct) =>
        FetchAsync(uri, maxBytes, CreateHandler(), ct);

    /// <summary>As above, over <paramref name="handler"/> (tests pass a fake one). The handler is disposed.</summary>
    internal static async Task<(byte[] Bytes, string MediaType)?> FetchAsync(
        Uri uri,
        int maxBytes,
        HttpMessageHandler handler,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var http = new HttpClient(handler) { Timeout = Timeout };
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowedImageUri(uri))
            {
                return null;
            }

            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (IsRedirect(response.StatusCode))
            {
                if (response.Headers.Location is not { } location)
                {
                    return null;
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadBoundedAsync(body, maxBytes, timeout.Token);
            if (bytes is null)
            {
                return null;
            }

            return (bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }

        return null; // Too many redirects.
    }

    /// <summary>http or https, and not a refused address when the host is an IP literal or "localhost".</summary>
    internal static bool IsAllowedImageUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Names are checked again on the resolved address when the connection opens.
        return !IPAddress.TryParse(uri.DnsSafeHost, out var address) || IsAllowedImageAddress(address);
    }

    /// <summary>False for loopback, link-local (169.254.x.x, fe80::/10), unspecified, multicast and broadcast.</summary>
    internal static bool IsAllowedImageAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] != 0 && b[0] != 127 && !(b[0] == 169 && b[1] == 254) && b[0] < 224;
        }

        return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>. Returns null as soon as the body is larger.</summary>
    internal static async Task<byte[]?> ReadBoundedAsync(Stream body, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false, // The address check below must see the real target, not a proxy.
            ConnectTimeout = Timeout,
            ConnectCallback = async (context, ct) =>
            {
                // Checks the address a name resolves to, so a name that points at this PC is refused too.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(IsAllowedImageAddress).ToArray();
                if (allowed.Length == 0 || allowed.Length != addresses.Length)
                {
                    throw new HttpRequestException($"Artwork host {context.DnsEndPoint.Host} is not allowed.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
}
