using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Models;

namespace Denon.App.Core.Heos;

public sealed class DenonAvrClient
{
    private const int AvrPort = 23;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    public async Task<AvrDeviceStatus> GetStatusAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var power = await QueryAsync(settings, "PW?", cancellationToken);
        var mainZone = await QueryAsync(settings, "ZM?", cancellationToken);
        var volume = await QueryAsync(settings, "MV?", cancellationToken);
        var mute = await QueryAsync(settings, "MU?", cancellationToken);
        var input = await QueryAsync(settings, "SI?", cancellationToken);

        return new AvrDeviceStatus(
            IsPoweredOn: string.Equals(power, "PWON", StringComparison.OrdinalIgnoreCase),
            IsMainZoneOn: string.Equals(mainZone, "ZMON", StringComparison.OrdinalIgnoreCase),
            Volume: ParseVolume(volume),
            IsMuted: string.Equals(mute, "MUON", StringComparison.OrdinalIgnoreCase),
            InputSource: ParseInput(input));
    }

    public Task SetVolumeAsync(ReceiverSettings settings, int level, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (level > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Volume must be between 0 and 100.");
        }

        return SendCommandAsync(settings, $"MV{level:00}", cancellationToken);
    }

    public Task SetMuteAsync(ReceiverSettings settings, bool isMuted, CancellationToken cancellationToken) =>
        SendCommandAsync(settings, isMuted ? "MUON" : "MUOFF", cancellationToken);

    public async Task EnsureMainZoneNetAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        await SendCommandAsync(settings, "PWON", cancellationToken);
        await Task.Delay(350, cancellationToken);
        await SendCommandAsync(settings, "ZMON", cancellationToken);
        await Task.Delay(350, cancellationToken);
        await SendCommandAsync(settings, "SINET", cancellationToken);
        await Task.Delay(350, cancellationToken);
    }

    private async Task<string> QueryAsync(ReceiverSettings settings, string command, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await SendAsync(settings.Host, command, cancellationToken);
            foreach (var line in SplitLines(response))
            {
                if (line.StartsWith(command[..2], StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException($"AVR query '{command}' did not return a matching response.");
    }

    private async Task SendCommandAsync(ReceiverSettings settings, string command, CancellationToken cancellationToken)
    {
        await SendAsync(settings.Host, command, cancellationToken);
    }

    private static async Task<string> SendAsync(string host, string command, CancellationToken cancellationToken)
    {
        return await Task.Run(() => Send(host, command, cancellationToken), cancellationToken);
    }

    private static string Send(string host, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var client = new TcpClient
        {
            ReceiveTimeout = (int)ReceiveTimeout.TotalMilliseconds,
            SendTimeout = (int)SendTimeout.TotalMilliseconds,
        };
        client.Connect(host, AvrPort);

        using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(command + "\r");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        using var bufferStream = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                bufferStream.Write(buffer, 0, read);
                if (read < buffer.Length)
                {
                    break;
                }
            }
            catch (IOException exception) when (exception.InnerException is SocketException socketException &&
                                               socketException.SocketErrorCode == SocketError.TimedOut)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(bufferStream.ToArray()).Trim();
    }

    private static IEnumerable<string> SplitLines(string raw) =>
        raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ParseVolume(string response)
    {
        var value = response.Length > 2 ? response[2..] : "0";
        if (value.Length >= 3 && value[2] != '0')
        {
            value = value[..2];
        }

        return int.TryParse(value, out var volume) ? volume : 0;
    }

    private static string ParseInput(string response) => response.Length > 2 ? response[2..] : string.Empty;
}
