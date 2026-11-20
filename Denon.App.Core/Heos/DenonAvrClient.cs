using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Models;

namespace Denon.App.Core.Heos;

public sealed class DenonAvrClient : IDisposable
{
    private const int AvrPort = 23;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(900);

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private string? _connectedHost;

    public async Task<AvrDeviceStatus> GetStatusAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var power = await QueryAsync(settings, "PW?", "PW", cancellationToken);
        var mainZone = await QueryAsync(settings, "ZM?", "ZM", cancellationToken);
        var volumeLines = await QueryAllMatchingAsync(settings, "MV?", "MV", cancellationToken);
        var mute = await QueryAsync(settings, "MU?", "MU", cancellationToken);
        var input = await QueryAsync(settings, "SI?", "SI", cancellationToken);

        // Optional / extended AVR telemetry (best-effort so older models never fail status)
        var soundMode = await TryQueryAsync(settings, "MS?", "MS", cancellationToken);
        var ecoMode = await TryQueryAsync(settings, "ECO?", "ECO", cancellationToken);
        var sleepRaw = await TryQueryAsync(settings, "SLP?", "SLP", cancellationToken);
        var zone2 = await TryQueryAsync(settings, "Z2?", "Z2", cancellationToken);

        var (volume, volumeExact, maxVolume) = ParseVolumeLines(volumeLines);

        return new AvrDeviceStatus(
            IsPoweredOn: string.Equals(power, "PWON", StringComparison.OrdinalIgnoreCase),
            IsMainZoneOn: string.Equals(mainZone, "ZMON", StringComparison.OrdinalIgnoreCase),
            Volume: volume,
            IsMuted: string.Equals(mute, "MUON", StringComparison.OrdinalIgnoreCase),
            InputSource: ParsePrefixValue(input, "SI"),
            SoundMode: ParsePrefixValue(soundMode, "MS"),
            EcoMode: ParsePrefixValue(ecoMode, "ECO"),
            SleepTimerMinutes: ParseSleepTimer(sleepRaw),
            IsZone2On: string.Equals(zone2, "Z2ON", StringComparison.OrdinalIgnoreCase),
            MaxVolume: maxVolume,
            VolumeExact: volumeExact);
    }

    public Task SetPowerAsync(ReceiverSettings settings, bool powerOn, CancellationToken cancellationToken) =>
        SendCommandAsync(settings, powerOn ? "PWON" : "PWSTANDBY", cancellationToken);

    public Task SetMainZonePowerAsync(ReceiverSettings settings, bool on, CancellationToken cancellationToken) =>
        SendCommandAsync(settings, on ? "ZMON" : "ZMOFF", cancellationToken);

    public Task SetZone2PowerAsync(ReceiverSettings settings, bool on, CancellationToken cancellationToken) =>
        SendCommandAsync(settings, on ? "Z2ON" : "Z2OFF", cancellationToken);

    public Task SetVolumeAsync(ReceiverSettings settings, int level, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (level > 98)
        {
            level = 98;
        }

        return SendCommandAsync(settings, $"MV{level:00}", cancellationToken);
    }

    public Task SetMuteAsync(ReceiverSettings settings, bool isMuted, CancellationToken cancellationToken) =>
        SendCommandAsync(settings, isMuted ? "MUON" : "MUOFF", cancellationToken);

    public Task SetInputSourceAsync(ReceiverSettings settings, string sourceCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            throw new ArgumentException("Input source code is required.", nameof(sourceCode));
        }

        return SendCommandAsync(settings, $"SI{sourceCode.Trim().ToUpperInvariant()}", cancellationToken);
    }

    public Task SetSoundModeAsync(ReceiverSettings settings, string modeCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modeCode))
        {
            throw new ArgumentException("Sound mode code is required.", nameof(modeCode));
        }

        return SendCommandAsync(settings, $"MS{modeCode.Trim().ToUpperInvariant()}", cancellationToken);
    }

    public Task SetEcoModeAsync(ReceiverSettings settings, string mode, CancellationToken cancellationToken)
    {
        var normalized = mode.Trim().ToUpperInvariant();
        if (normalized is not ("ON" or "AUTO" or "OFF"))
        {
            throw new ArgumentException("ECO mode must be ON, AUTO, or OFF.", nameof(mode));
        }

        return SendCommandAsync(settings, $"ECO{normalized}", cancellationToken);
    }

    public Task SetSleepTimerAsync(ReceiverSettings settings, int? minutes, CancellationToken cancellationToken)
    {
        if (minutes is null or <= 0)
        {
            return SendCommandAsync(settings, "SLPOFF", cancellationToken);
        }

        var clamped = Math.Clamp(minutes.Value, 1, 120);
        return SendCommandAsync(settings, $"SLP{clamped:000}", cancellationToken);
    }

    public async Task EnsureMainZoneNetAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var power = await TryQueryAsync(settings, "PW?", "PW", cancellationToken);
        var wasPoweredOff = !string.Equals(power, "PWON", StringComparison.OrdinalIgnoreCase);

        if (wasPoweredOff)
        {
            await SendCommandAsync(settings, "PWON", cancellationToken);
            await WaitForConditionAsync(
                settings,
                "PW?",
                "PW",
                line => string.Equals(line, "PWON", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }

        var mainZone = await TryQueryAsync(settings, "ZM?", "ZM", cancellationToken);
        var wasZoneOff = !string.Equals(mainZone, "ZMON", StringComparison.OrdinalIgnoreCase);

        if (wasZoneOff)
        {
            await SendCommandAsync(settings, "ZMON", cancellationToken);
            await WaitForConditionAsync(
                settings,
                "ZM?",
                "ZM",
                line => string.Equals(line, "ZMON", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(3),
                cancellationToken);
        }

        var input = await TryQueryAsync(settings, "SI?", "SI", cancellationToken);
        var currentInput = ParsePrefixValue(input, "SI");
        var needsSwitch = !DenonCatalog.InputMatches(currentInput, "NET");

        if (needsSwitch)
        {
            await SendCommandAsync(settings, "SINET", cancellationToken);
            await WaitForConditionAsync(
                settings,
                "SI?",
                "SI",
                line => DenonCatalog.InputMatches(ParsePrefixValue(line, "SI"), "NET"),
                TimeSpan.FromSeconds(3.5),
                cancellationToken);

            // Allow internal HEOS DSP & network module to finish initializing after switching from TV/HDMI
            await Task.Delay(750, cancellationToken);
        }
        else if (wasPoweredOff || wasZoneOff)
        {
            await Task.Delay(600, cancellationToken);
        }
    }

    public async Task<string> SendRawCommandAsync(ReceiverSettings settings, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("An AVR command is required.", nameof(command));
        }

        var lines = await ExchangeLinesAsync(settings.Host, command.Trim().ToUpperInvariant(), TimeSpan.FromMilliseconds(900), cancellationToken);
        return lines.Count == 0 ? "(sent - no reply)" : string.Join(Environment.NewLine, lines);
    }

    public static (int Volume, double VolumeExact, int MaxVolume) ParseVolumeLines(IEnumerable<string> lines)
    {
        var maxVolume = DenonVolumeMath.MaxAbsoluteVolume;
        int? parsedVolume = null;
        double? parsedExact = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("MVMAX", StringComparison.OrdinalIgnoreCase))
            {
                var maxToken = line["MVMAX".Length..].Trim();
                if (TryParseDenonVolumeToken(maxToken, out var maxInt, out _))
                {
                    maxVolume = Math.Clamp(maxInt, 10, 98);
                }

                continue;
            }

            if (line.StartsWith("MV", StringComparison.OrdinalIgnoreCase))
            {
                var token = line[2..].Trim();
                if (TryParseDenonVolumeToken(token, out var volInt, out var volExact))
                {
                    parsedVolume = volInt;
                    parsedExact = volExact;
                }
            }
        }

        return (parsedVolume ?? 0, parsedExact ?? parsedVolume ?? 0, maxVolume);
    }

    public static bool TryParseDenonVolumeToken(string token, out int volume, out double exactVolume)
    {
        volume = 0;
        exactVolume = 0;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // Denon AVR encodes 2-digit whole steps ("35" -> 35.0) and 3-digit half steps ("355" -> 35.5, "350" -> 35.0)
        if (token.Length == 3 && token.All(char.IsDigit))
        {
            if (int.TryParse(token[..2], out var whole) && int.TryParse(token[2..3], out var tenths))
            {
                exactVolume = whole + (tenths / 10.0);
                volume = Math.Clamp(whole, 0, 98);
                return true;
            }
        }

        if (token.Length is 1 or 2 && int.TryParse(token, out var simple))
        {
            volume = Math.Clamp(simple, 0, 98);
            exactVolume = volume;
            return true;
        }

        return false;
    }

    public static int? ParseSleepTimer(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var value = ParsePrefixValue(response, "SLP");
        if (string.Equals(value, "OFF", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value, out var minutes) && minutes > 0 ? minutes : null;
    }

    private async Task WaitForConditionAsync(
        ReceiverSettings settings,
        string queryCommand,
        string prefix,
        Func<string, bool> predicate,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + maxWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(300, cancellationToken);
            var response = await TryQueryAsync(settings, queryCommand, prefix, cancellationToken);
            if (!string.IsNullOrEmpty(response) && predicate(response))
            {
                return;
            }
        }
    }

    private async Task<string> QueryAsync(ReceiverSettings settings, string command, string expectedPrefix, CancellationToken cancellationToken)
    {
        var lines = await QueryAllMatchingAsync(settings, command, expectedPrefix, cancellationToken);
        if (lines.Count > 0)
        {
            return lines[0];
        }

        throw new InvalidOperationException($"AVR query '{command}' did not return a matching response.");
    }

    private async Task<string?> TryQueryAsync(ReceiverSettings settings, string command, string expectedPrefix, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await ExchangeLinesAsync(settings.Host, command, TimeSpan.FromMilliseconds(550), cancellationToken);
            return lines.FirstOrDefault(line => line.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<string>> QueryAllMatchingAsync(
        ReceiverSettings settings,
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var lines = await ExchangeLinesAsync(settings.Host, command, ReadTimeout, cancellationToken);
            var matches = lines
                .Where(line => line.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0)
            {
                return matches;
            }

            await Task.Delay(180, cancellationToken);
        }

        return [];
    }

    private async Task SendCommandAsync(ReceiverSettings settings, string command, CancellationToken cancellationToken)
    {
        await ExchangeLinesAsync(settings.Host, command, TimeSpan.FromMilliseconds(220), cancellationToken);
    }

    private async Task<List<string>> ExchangeLinesAsync(
        string host,
        string command,
        TimeSpan readWindow,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                await EnsureConnectedLockedAsync(host, cancellationToken);
                DrainPendingBytesLocked();

                var payload = Encoding.ASCII.GetBytes(command + "\r");
                await _stream!.WriteAsync(payload, cancellationToken);
                await _stream.FlushAsync(cancellationToken);

                return await ReadFramesLockedAsync(readWindow, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException && attempt == 0)
            {
                lastException = exception;
                ResetConnectionLocked();
                await Task.Delay(150, cancellationToken);
            }
            catch
            {
                ResetConnectionLocked();
                throw;
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        throw lastException ?? new InvalidOperationException($"AVR command '{command}' failed.");
    }

    private async Task EnsureConnectedLockedAsync(string host, CancellationToken cancellationToken)
    {
        if (_client is { Connected: true } &&
            _stream is not null &&
            string.Equals(_connectedHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ResetConnectionLocked();

        var client = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(host, AvrPort, connectCts.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _connectedHost = host;
    }

    private void DrainPendingBytesLocked()
    {
        if (_stream is null || !_stream.DataAvailable)
        {
            return;
        }

        var scratch = new byte[1024];
        while (_stream.DataAvailable)
        {
            var read = _stream.Read(scratch, 0, scratch.Length);
            if (read <= 0)
            {
                break;
            }
        }
    }

    private async Task<List<string>> ReadFramesLockedAsync(TimeSpan readWindow, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var builder = new StringBuilder();
        var deadline = DateTimeOffset.UtcNow + readWindow;
        var hasReceivedAny = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // Once we have at least one complete \r-terminated frame, only wait briefly for follow-up lines (e.g. MVMAX)
            var slice = hasReceivedAny
                ? TimeSpan.FromMilliseconds(Math.Min(120, remaining.TotalMilliseconds))
                : remaining;

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(slice);

            try
            {
                var read = await _stream!.ReadAsync(buffer, readCts.Token);
                if (read <= 0)
                {
                    throw new IOException("AVR Telnet connection closed by remote host.");
                }

                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (builder.ToString().Contains('\r'))
                {
                    hasReceivedAny = true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return builder
            .ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private void ResetConnectionLocked()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _connectedHost = null;
    }

    public void Disconnect()
    {
        _connectionGate.Wait();
        try
        {
            ResetConnectionLocked();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public void Dispose()
    {
        _connectionGate.Wait();
        try
        {
            ResetConnectionLocked();
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private static string ParsePrefixValue(string? response, string prefix)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return string.Empty;
        }

        return response.Length > prefix.Length && response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? response[prefix.Length..].Trim()
            : response.Trim();
    }
}
