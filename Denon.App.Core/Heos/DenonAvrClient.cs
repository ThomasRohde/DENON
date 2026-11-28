using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Models;

namespace Denon.App.Core.Heos;

public sealed class DenonAvrClient : IDisposable
{
    private const int AvrPort = 23;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>Denon asks for at least 50 ms between two commands.</summary>
    private static readonly TimeSpan MinCommandSpacing = TimeSpan.FromMilliseconds(50);

    private readonly int _port;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private string? _connectedHost;
    private DateTimeOffset _lastWriteAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Set by <see cref="Disconnect"/> while a command holds the connection. The next command drops the
    /// connection and opens a new one. Disconnect never waits for the gate: the UI thread calls it.
    /// </summary>
    private volatile bool _resetRequested;

    public DenonAvrClient()
        : this(AvrPort)
    {
    }

    /// <summary>Tests point the client at a fake receiver on another port.</summary>
    internal DenonAvrClient(int port) => _port = port;

    public async Task<AvrDeviceStatus> GetStatusAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var power = await QueryAsync(settings, "PW?", "PW", cancellationToken);
        var mainZone = await QueryAsync(settings, "ZM?", "ZM", cancellationToken);
        var volumeLines = await QueryAllMatchingAsync(settings, "MV?", "MV", cancellationToken, IsCompleteVolumeReply);
        var mute = await QueryAsync(settings, "MU?", "MU", cancellationToken);
        var input = await QueryAsync(settings, "SI?", "SI", cancellationToken);

        // Optional / extended AVR telemetry (best-effort so older models never fail status)
        var soundMode = await TryQueryAsync(settings, "MS?", "MS", cancellationToken);
        var ecoMode = await TryQueryAsync(settings, "ECO?", "ECO", cancellationToken);
        var sleepRaw = await TryQueryAsync(settings, "SLP?", "SLP", cancellationToken);
        // Z2? answers with several Z2 lines (power, source, volume) in any order: look at all of them.
        var zone2Lines = await TryQueryAllMatchingAsync(settings, "Z2?", "Z2", cancellationToken);

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
            IsZone2On: IsZone2OnReply(zone2Lines),
            MaxVolume: maxVolume,
            VolumeExact: volumeExact);
    }

    /// <summary>The current input (for example "TV"), or null when the receiver does not answer SI?.</summary>
    public async Task<string?> TryGetInputSourceAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var reply = await TryQueryAsync(settings, "SI?", "SI", cancellationToken);
        return reply is null ? null : ParsePrefixValue(reply, "SI");
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

    public async Task<AcousticStatus> GetAcousticStatusAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var multEqRaw = await TryQueryAsync(settings, "PSMULTEQ: ?", "PSMULTEQ:", cancellationToken);
        var dynEqRaw = await TryQueryAsync(settings, "PSDYNEQ ?", "PSDYNEQ", cancellationToken);
        var dynVolRaw = await TryQueryAsync(settings, "PSDYNVOL ?", "PSDYNVOL", cancellationToken);
        var restorerRaw = await TryQueryAsync(settings, "PSRSTR ?", "PSRSTR", cancellationToken);
        var dimmerRaw = await TryQueryAsync(settings, "DIM ?", "DIM", cancellationToken);

        var swLines = await TryQueryAllMatchingAsync(settings, "PSSWL ?", "PSSWL", cancellationToken);
        var dilLines = await TryQueryAllMatchingAsync(settings, "PSDIL ?", "PSDIL", cancellationToken);
        var toneRaw = await TryQueryAsync(settings, "PSTONE CTRL ?", "PSTONE CTRL", cancellationToken);
        var bassRaw = await TryQueryAsync(settings, "PSBAS ?", "PSBAS", cancellationToken);
        var trebleRaw = await TryQueryAsync(settings, "PSTRE ?", "PSTRE", cancellationToken);

        var (swEnabled, swDb) = ParseLevelAdjustLines(swLines, "PSSWL", -12, 12);
        var (dilEnabled, dilDb) = ParseLevelAdjustLines(dilLines, "PSDIL", -12, 12);
        var bassDb = ParseDenonOffsetDb(ParsePrefixValue(bassRaw, "PSBAS"), -6, 6);
        var trebleDb = ParseDenonOffsetDb(ParsePrefixValue(trebleRaw, "PSTRE"), -6, 6);

        return new AcousticStatus(
            MultEq: ParsePrefixValue(multEqRaw, "PSMULTEQ:").ToUpperInvariant(),
            DynamicEq: ParseOnOff(ParsePrefixValue(dynEqRaw, "PSDYNEQ")),
            DynamicVolume: ParsePrefixValue(dynVolRaw, "PSDYNVOL").ToUpperInvariant(),
            Restorer: ParsePrefixValue(restorerRaw, "PSRSTR").ToUpperInvariant(),
            Dimmer: NormalizeDimmerCode(ParsePrefixValue(dimmerRaw, "DIM")),
            SubwooferAdjustEnabled: swEnabled,
            SubwooferTrimDb: swDb,
            DialogAdjustEnabled: dilEnabled,
            DialogTrimDb: dilDb,
            ToneControlEnabled: ParseOnOff(ParsePrefixValue(toneRaw, "PSTONE CTRL")),
            BassDb: bassDb,
            TrebleDb: trebleDb,
            RetrievedAt: DateTimeOffset.UtcNow);
    }

    public Task SetMultEqAsync(ReceiverSettings settings, string mode, CancellationToken cancellationToken)
    {
        var normalized = mode.Trim().ToUpperInvariant();
        if (normalized is not ("AUDYSSEY" or "BYP.LR" or "FLAT" or "OFF"))
        {
            throw new ArgumentException("MultEQ mode must be AUDYSSEY, BYP.LR, FLAT, or OFF.", nameof(mode));
        }

        return SendCommandAsync(settings, $"PSMULTEQ:{normalized}", cancellationToken);
    }

    public Task SetDynamicEqAsync(ReceiverSettings settings, bool enabled, CancellationToken cancellationToken) =>
        SendCommandAsync(settings, enabled ? "PSDYNEQ ON" : "PSDYNEQ OFF", cancellationToken);

    public Task SetDynamicVolumeAsync(ReceiverSettings settings, string mode, CancellationToken cancellationToken)
    {
        var normalized = mode.Trim().ToUpperInvariant();
        if (normalized is not ("OFF" or "LIT" or "MED" or "HEV"))
        {
            throw new ArgumentException("Dynamic Volume must be OFF, LIT, MED, or HEV.", nameof(mode));
        }

        return SendCommandAsync(settings, $"PSDYNVOL {normalized}", cancellationToken);
    }

    public Task SetRestorerAsync(ReceiverSettings settings, string mode, CancellationToken cancellationToken)
    {
        var normalized = mode.Trim().ToUpperInvariant();
        if (normalized is not ("OFF" or "LOW" or "MED" or "HI"))
        {
            throw new ArgumentException("Restorer must be OFF, LOW, MED, or HI.", nameof(mode));
        }

        return SendCommandAsync(settings, $"PSRSTR {normalized}", cancellationToken);
    }

    public Task SetDimmerAsync(ReceiverSettings settings, string mode, CancellationToken cancellationToken)
    {
        var normalized = NormalizeDimmerCode(mode);
        if (normalized is not ("BRI" or "DIM" or "DAR" or "OFF"))
        {
            throw new ArgumentException("Dimmer mode must be BRI, DIM, DAR, or OFF.", nameof(mode));
        }

        return SendCommandAsync(settings, $"DIM {normalized}", cancellationToken);
    }

    public async Task SetSubwooferLevelAsync(ReceiverSettings settings, bool enabled, int? trimDb, CancellationToken cancellationToken)
    {
        await SendCommandAsync(settings, enabled ? "PSSWL ON" : "PSSWL OFF", cancellationToken);
        if (enabled && trimDb is int db)
        {
            var encoded = Math.Clamp(50 + Math.Clamp(db, -12, 12), 38, 62);
            await SendCommandAsync(settings, $"PSSWL {encoded:00}", cancellationToken);
        }
    }

    public async Task SetDialogLevelAsync(ReceiverSettings settings, bool enabled, int? trimDb, CancellationToken cancellationToken)
    {
        await SendCommandAsync(settings, enabled ? "PSDIL ON" : "PSDIL OFF", cancellationToken);
        if (enabled && trimDb is int db)
        {
            var encoded = Math.Clamp(50 + Math.Clamp(db, -12, 12), 38, 62);
            await SendCommandAsync(settings, $"PSDIL {encoded:00}", cancellationToken);
        }
    }

    public async Task SetToneControlAsync(ReceiverSettings settings, bool enabled, int? bassDb, int? trebleDb, CancellationToken cancellationToken)
    {
        await SendCommandAsync(settings, enabled ? "PSTONE CTRL ON" : "PSTONE CTRL OFF", cancellationToken);
        if (enabled && bassDb is int bass)
        {
            var encoded = Math.Clamp(50 + Math.Clamp(bass, -6, 6), 44, 56);
            await SendCommandAsync(settings, $"PSBAS {encoded:00}", cancellationToken);
        }

        if (enabled && trebleDb is int treble)
        {
            var encoded = Math.Clamp(50 + Math.Clamp(treble, -6, 6), 44, 56);
            await SendCommandAsync(settings, $"PSTRE {encoded:00}", cancellationToken);
        }
    }

    public async Task RampVolumeAsync(ReceiverSettings settings, int currentVolume, int targetVolume, CancellationToken cancellationToken)
    {
        targetVolume = Math.Clamp(targetVolume, 0, 98);
        currentVolume = Math.Clamp(currentVolume, 0, 98);
        var distance = Math.Abs(targetVolume - currentVolume);

        if (distance <= 3)
        {
            await SetVolumeAsync(settings, targetVolume, cancellationToken);
            return;
        }

        var stepCount = Math.Clamp(distance / 3, 3, 12);
        for (var i = 1; i <= stepCount; i++)
        {
            var next = i == stepCount
                ? targetVolume
                : (int)Math.Round(currentVolume + ((targetVolume - currentVolume) * (i / (double)stepCount)));
            await SetVolumeAsync(settings, next, cancellationToken);
            if (i < stepCount)
            {
                await Task.Delay(130, cancellationToken);
            }
        }
    }

    public static int ParseDenonOffsetDb(string? token, int minDb, int maxDb)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return 0;
        }

        var trimmed = token.Trim();
        if (trimmed.Length == 3 && trimmed.All(char.IsDigit) && int.TryParse(trimmed[..2], out var whole))
        {
            return Math.Clamp(whole - 50, minDb, maxDb);
        }

        if (int.TryParse(trimmed, out var value))
        {
            return Math.Clamp(value - 50, minDb, maxDb);
        }

        return 0;
    }

    private static (bool? Enabled, int Db) ParseLevelAdjustLines(IReadOnlyList<string> lines, string prefix, int minDb, int maxDb)
    {
        bool? enabled = null;
        var db = 0;

        foreach (var line in lines)
        {
            var value = ParsePrefixValue(line, prefix).Trim().ToUpperInvariant();
            if (value == "ON")
            {
                enabled = true;
            }
            else if (value == "OFF")
            {
                enabled = false;
            }
            else if (!string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit))
            {
                db = ParseDenonOffsetDb(value, minDb, maxDb);
            }
        }

        return (enabled, db);
    }

    private static bool? ParseOnOff(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var upper = value.Trim().ToUpperInvariant();
        return upper switch
        {
            "ON" => true,
            "OFF" => false,
            _ => null,
        };
    }

    private static string NormalizeDimmerCode(string? value)
    {
        var upper = (value ?? string.Empty).Trim().ToUpperInvariant();
        return upper switch
        {
            "BRIGHT" or "BRI" => "BRI",
            "DIM" => "DIM",
            "DARK" or "DAR" => "DAR",
            "OFF" => "OFF",
            _ => upper,
        };
    }

    private async Task<List<string>> TryQueryAllMatchingAsync(
        ReceiverSettings settings,
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var lines = await ExchangeLinesAsync(settings.Host, command, TimeSpan.FromMilliseconds(550), isComplete: null, cancellationToken);
            return lines
                .Where(line => line.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancelled caller must see the cancellation, not an empty answer it would keep as real.
            return [];
        }
    }

    /// <summary>True when one of the Z2? reply lines is exactly "Z2ON" (the other lines are source and volume).</summary>
    public static bool IsZone2OnReply(IEnumerable<string> lines) =>
        lines.Any(static line => string.Equals(line.Trim(), "Z2ON", StringComparison.OrdinalIgnoreCase));

    /// <summary>MV? answers "MV45" and then "MVMAX 98". The reply is complete when both lines are in.</summary>
    internal static bool IsCompleteVolumeReply(IReadOnlyList<string> lines) =>
        lines.Any(static l => l.StartsWith("MVMAX", StringComparison.OrdinalIgnoreCase)) &&
        lines.Any(static l => l.StartsWith("MV", StringComparison.OrdinalIgnoreCase) &&
                              !l.StartsWith("MVMAX", StringComparison.OrdinalIgnoreCase));

    private static Func<IReadOnlyList<string>, bool> HasLineStartingWith(string prefix) =>
        lines => lines.Any(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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

        var lines = await ExchangeLinesAsync(settings.Host, command.Trim().ToUpperInvariant(), TimeSpan.FromMilliseconds(900), isComplete: null, cancellationToken);
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
            var lines = await ExchangeLinesAsync(settings.Host, command, TimeSpan.FromMilliseconds(550), HasLineStartingWith(expectedPrefix), cancellationToken);
            return lines.FirstOrDefault(line => line.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancelled caller must see the cancellation, not an empty answer it would keep as real.
            return null;
        }
    }

    private async Task<List<string>> QueryAllMatchingAsync(
        ReceiverSettings settings,
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<string>, bool>? isComplete = null)
    {
        isComplete ??= HasLineStartingWith(expectedPrefix);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var lines = await ExchangeLinesAsync(settings.Host, command, ReadTimeout, isComplete, cancellationToken);
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
        await ExchangeLinesAsync(settings.Host, command, TimeSpan.FromMilliseconds(220), isComplete: null, cancellationToken);
    }

    /// <summary>
    /// Refuses a command with a control character. A CR or LF inside a source or mode code would make the
    /// receiver run a second command (for example "TV\rMV98"), past the safe-volume and power checks.
    /// </summary>
    internal static void EnsureSingleCommand(string command)
    {
        if (command.Any(char.IsControl))
        {
            throw new ArgumentException("An AVR command must be one line without control characters.", nameof(command));
        }
    }

    private async Task<List<string>> ExchangeLinesAsync(
        string host,
        string command,
        TimeSpan readWindow,
        Func<IReadOnlyList<string>, bool>? isComplete,
        CancellationToken cancellationToken)
    {
        EnsureSingleCommand(command);
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                await EnsureConnectedLockedAsync(host, cancellationToken);
                DrainPendingBytesLocked();

                var sinceLastWrite = DateTimeOffset.UtcNow - _lastWriteAt;
                if (sinceLastWrite < MinCommandSpacing)
                {
                    await Task.Delay(MinCommandSpacing - sinceLastWrite, cancellationToken);
                }

                var payload = Encoding.ASCII.GetBytes(command + "\r");
                await _stream!.WriteAsync(payload, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
                _lastWriteAt = DateTimeOffset.UtcNow;

                return await ReadFramesLockedAsync(readWindow, isComplete, cancellationToken);
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
        if (_resetRequested)
        {
            _resetRequested = false;
            ResetConnectionLocked();
        }

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
            await client.ConnectAsync(host, _port, connectCts.Token);
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

    /// <param name="isComplete">
    /// Tells when the complete lines read so far are the whole answer, so the read ends at once. Null for
    /// answers whose length is not known (several lines): they end 120 ms after the last line, as before.
    /// </param>
    private async Task<List<string>> ReadFramesLockedAsync(
        TimeSpan readWindow,
        Func<IReadOnlyList<string>, bool>? isComplete,
        CancellationToken cancellationToken)
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
                var text = builder.ToString();
                var lastLineEnd = text.LastIndexOf('\r');
                if (lastLineEnd >= 0)
                {
                    hasReceivedAny = true;
                    if (isComplete is not null && isComplete(SplitLines(text[..lastLineEnd])))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return SplitLines(builder.ToString());
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void ResetConnectionLocked()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _connectedHost = null;
    }

    /// <summary>
    /// Drops the connection without waiting. When a command holds it, the next command drops it instead.
    /// Never blocks: the UI thread calls this (Reset, Save &amp; Sync, Import) while a command it started may
    /// still wait for the receiver, and that command needs the UI thread to finish.
    /// </summary>
    public void Disconnect()
    {
        if (!_connectionGate.Wait(0))
        {
            _resetRequested = true;
            return;
        }

        try
        {
            _resetRequested = false;
            ResetConnectionLocked();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>The gate holds no OS handle, so it is not disposed: a command in flight may still release it.</summary>
    public void Dispose() => Disconnect();

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
