using System.Text;

namespace Denon.App.Core.Setup;

/// <summary>A command the gate refused to build.</summary>
public sealed class SetupCommandRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// One receiver command that came out of <see cref="AvrCommandGate"/>. Only the gate can make one, so the client's
/// setup methods (which take nothing else) can only send what a profile describes, with a value from its own list.
/// </summary>
public sealed class AvrSetupCommand
{
    internal AvrSetupCommand(string text, bool isRead, string settingId)
    {
        Text = text;
        IsRead = isRead;
        SettingId = settingId;
    }

    /// <summary>The command, without the closing carriage return.</summary>
    public string Text { get; }

    /// <summary>A query ("PSREFLEV ?"): it never changes the receiver.</summary>
    public bool IsRead { get; }

    public string SettingId { get; }

    public override string ToString() => Text;
}

/// <summary>
/// The only way a profile reaches the receiver. A read is the profile's read command; a write is the profile's write
/// template with one value from the setting's own list. Nothing else is built, whatever the JSON says: a few commands
/// stay blocked for every profile, and a command has to stay inside a short list of families.
/// </summary>
public static class AvrCommandGate
{
    /// <summary>Power, factory reset, front panel and remote-control commands. No profile can reach them.</summary>
    public static IReadOnlyList<string> BlockedPrefixes { get; } = ["SYRST", "PW", "SYPANEL", "RC"];

    /// <summary>The command families a profile may use: sound processing, ECO, dimmer, sound mode and video.</summary>
    public static IReadOnlyList<string> AllowedPrefixes { get; } = ["PS", "ECO", "DIM", "MS", "VS", "CV"];

    /// <summary>Denon's limit for one command, with its closing carriage return.</summary>
    public const int MaxCommandBytes = 135;

    public const string ValuePlaceholder = "{value}";

    public static AvrSetupCommand BuildRead(ProfileSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!setting.IsReadable)
        {
            throw new SetupCommandRefusedException($"'{setting.Id}' has no read command.");
        }

        var text = setting.Read!.Trim();
        EnsureAllowed(text, isRead: true);
        return new AvrSetupCommand(text, isRead: true, setting.Id);
    }

    public static AvrSetupCommand BuildWrite(ProfileSetting setting, string token)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!setting.IsWritable)
        {
            throw new SetupCommandRefusedException($"'{setting.Id}' cannot be set: it is advice only or has no write command.");
        }

        var allowed = setting.FindValue(token)
            ?? throw new SetupCommandRefusedException($"'{token}' is not one of the values '{setting.Id}' allows.");

        var template = setting.Write!.Trim();
        if (template.Split(ValuePlaceholder).Length != 2)
        {
            throw new SetupCommandRefusedException($"The write command of '{setting.Id}' needs exactly one {ValuePlaceholder}.");
        }

        var text = template.Replace(ValuePlaceholder, allowed.Token, StringComparison.Ordinal);
        EnsureAllowed(text, isRead: false);
        return new AvrSetupCommand(text, isRead: false, setting.Id);
    }

    /// <summary>The rules every command meets. Throws <see cref="SetupCommandRefusedException"/> when one fails.</summary>
    public static void EnsureAllowed(string command, bool isRead)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new SetupCommandRefusedException("A command is required.");
        }

        if (command.Any(static c => c is < ' ' or > '~'))
        {
            throw new SetupCommandRefusedException("A command is one line of printable ASCII.");
        }

        var upper = command.TrimStart().ToUpperInvariant();
        if (!string.Equals(upper, command, StringComparison.Ordinal))
        {
            throw new SetupCommandRefusedException("A command is upper case without leading space.");
        }

        if (BlockedPrefixes.FirstOrDefault(p => upper.StartsWith(p, StringComparison.Ordinal)) is { } blocked)
        {
            throw new SetupCommandRefusedException($"Commands starting with {blocked} are blocked for profiles.");
        }

        if (!AllowedPrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal)))
        {
            throw new SetupCommandRefusedException($"'{command}' is not in a command family a profile may use ({string.Join(", ", AllowedPrefixes)}).");
        }

        if (isRead != upper.EndsWith('?'))
        {
            throw new SetupCommandRefusedException(isRead
                ? "A read command ends with '?'."
                : "A write command is never a query.");
        }

        if (!isRead && upper.Contains('?'))
        {
            throw new SetupCommandRefusedException("A write command has no '?'.");
        }

        if (Encoding.ASCII.GetByteCount(command) + 1 > MaxCommandBytes)
        {
            throw new SetupCommandRefusedException($"A command is at most {MaxCommandBytes} bytes with its carriage return.");
        }
    }
}
