using System.Text.RegularExpressions;
using Denon.App.Core.Setup;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// A receiver in memory for the setup check. It answers reads from the profile's own commands and keeps values per
/// input for per-input settings. No test sends anything to a real device.
/// </summary>
internal sealed class FakeSetupLink : IReceiverSetupLink
{
    private readonly ReceiverProfile _profile;
    private readonly Dictionary<(string Input, string Id), string> _perInput = [];
    private readonly Dictionary<string, string> _global = [];

    public FakeSetupLink(ReceiverProfile profile) => _profile = profile;

    public string Model { get; set; } = "Denon AVR-X2400H";

    public string ReceiverId { get; set; } = "192.0.2.10";

    public string Input { get; set; } = "TV";

    public string SoundMode { get; set; } = "STEREO";

    public bool IsPoweredOn { get; set; } = true;

    public bool IsMainZoneOn { get; set; } = true;

    /// <summary>Every command that reached the receiver, in order.</summary>
    public List<AvrSetupCommand> Sent { get; } = [];

    public IEnumerable<string> Reads => Sent.Where(static c => c.IsRead).Select(static c => c.Text);

    public IEnumerable<string> Writes => Sent.Where(static c => !c.IsRead).Select(static c => c.Text);

    /// <summary>Raw reply lines for settings whose reply is not the write command's echo (the trim, the delay).</summary>
    public Dictionary<string, string[]> RawReplies { get; } = new(StringComparer.Ordinal);

    /// <summary>Setting ids the receiver does not answer a read for.</summary>
    public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);

    /// <summary>Setting ids whose write the receiver ignores (it keeps its old value).</summary>
    public HashSet<string> IgnoresWrites { get; } = new(StringComparer.Ordinal);

    /// <summary>Setting ids whose read throws (a broken link).</summary>
    public HashSet<string> ThrowsOnRead { get; } = new(StringComparer.Ordinal);

    /// <summary>Called right before each write reaches the receiver.</summary>
    public Action<AvrSetupCommand>? BeforeWrite { get; set; }

    /// <summary>When set, this write throws as a connection error would.</summary>
    public Func<AvrSetupCommand, bool>? FailWrite { get; set; }

    /// <summary>Called once after the first read, to change the input "while the check runs".</summary>
    public Action? AfterFirstRead { get; set; }

    private bool _firstReadDone;

    public void Set(string id, string token, string? input = null)
    {
        var setting = _profile.Settings.First(s => s.Id == id);
        if (setting.Scope == SettingScope.Global)
        {
            _global[id] = token;
        }
        else
        {
            _perInput[(input ?? Input, id)] = token;
        }
    }

    public string? Get(string id, string? input = null)
    {
        var setting = _profile.Settings.First(s => s.Id == id);
        return setting.Scope == SettingScope.Global
            ? _global.GetValueOrDefault(id)
            : _perInput.GetValueOrDefault((input ?? Input, id));
    }

    public Task<SetupContext> GetContextAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SetupContext(Model, Input, SoundMode, IsPoweredOn, IsMainZoneOn, ReceiverId));

    public Task<IReadOnlyList<string>> QueryAsync(AvrSetupCommand command, Func<string, bool> answerLine, CancellationToken cancellationToken)
    {
        Assert.True(command.IsRead);
        Sent.Add(command);
        var setting = _profile.Settings.First(s => s.Id == command.SettingId);
        if (ThrowsOnRead.Contains(setting.Id))
        {
            throw new IOException("The link dropped.");
        }

        IReadOnlyList<string> lines = [];
        if (!Silent.Contains(setting.Id))
        {
            lines = Reply(setting);
        }

        if (!_firstReadDone)
        {
            _firstReadDone = true;
            AfterFirstRead?.Invoke();
        }

        return Task.FromResult(lines);
    }

    private IReadOnlyList<string> Reply(ProfileSetting setting)
    {
        if (RawReplies.TryGetValue(setting.Id, out var raw))
        {
            return raw;
        }

        if (setting.Id == "soundMode")
        {
            return [$"MS{SoundMode}"];
        }

        var token = Get(setting.Id);
        return token is null || setting.Write is null
            ? []
            : [setting.Write.Replace(AvrCommandGate.ValuePlaceholder, token, StringComparison.Ordinal)];
    }

    public Task SendAsync(AvrSetupCommand command, CancellationToken cancellationToken)
    {
        Assert.False(command.IsRead);
        BeforeWrite?.Invoke(command);
        Sent.Add(command);
        if (FailWrite?.Invoke(command) == true)
        {
            throw new IOException("The link dropped.");
        }

        var setting = _profile.Settings
            .Where(s => s.Write is not null && command.Text.StartsWith(s.Write![..s.Write.IndexOf(AvrCommandGate.ValuePlaceholder, StringComparison.Ordinal)], StringComparison.Ordinal))
            .OrderByDescending(s => s.Write!.IndexOf(AvrCommandGate.ValuePlaceholder, StringComparison.Ordinal))
            .FirstOrDefault();
        if (setting is not null && !IgnoresWrites.Contains(setting.Id))
        {
            var template = setting.Write!;
            var prefix = template[..template.IndexOf(AvrCommandGate.ValuePlaceholder, StringComparison.Ordinal)];
            Set(setting.Id, command.Text[prefix.Length..]);
        }

        return Task.CompletedTask;
    }
}

internal static class SetupTestProfiles
{
    public static ReceiverProfile X2400H()
    {
        var library = ReceiverProfileLibrary.Load(null);
        Assert.Empty(library.Problems);
        return library.FindForModel("Denon AVR-X2400H") ?? throw new InvalidOperationException("No bundled X2400H profile.");
    }

    /// <summary>A fake whose settings are all at a value the films recommendation does not want.</summary>
    public static FakeSetupLink FakeWithWrongValues(ReceiverProfile profile)
    {
        var link = new FakeSetupLink(profile);
        link.Set("multEq", "FLAT");
        link.Set("dynamicEq", "OFF");
        link.Set("refLevel", "15");
        link.Set("dynamicVolume", "HEV");
        link.Set("cinemaEq", "ON");
        link.Set("restorer", "HI");
        link.Set("tone", "ON");
        link.Set("drc", "HI");
        link.Set("loudnessManagement", "OFF");
        link.Set("lfeLevel", "05");
        link.Set("eco", "ON");
        link.RawReplies["subTrim"] = ["PSSWL ON", "PSSWL 50"];
        link.RawReplies["audioDelay"] = ["PSDELAY 000"];
        return link;
    }

    public static string MinimalProfileJson(string settingsJson = DefaultSetting) =>
        "{ \"id\": \"test-profile\", \"name\": \"Test\", \"models\": [\"AVR-TEST1\"], \"settings\": [" + settingsJson + "] }";

    public const string DefaultSetting = """
        {
          "id": "dynamicEq", "label": "Dynamic EQ", "group": "Audyssey", "scope": "perInput", "tier": "apply",
          "read": "PSDYNEQ ?", "readParse": "^PSDYNEQ (ON|OFF)$", "write": "PSDYNEQ {value}",
          "values": [ { "token": "ON" }, { "token": "OFF" } ],
          "recommend": { "films": "ON" }, "why": "Because."
        }
        """;
}
