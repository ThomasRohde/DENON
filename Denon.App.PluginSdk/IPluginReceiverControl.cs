namespace Denon.App.PluginSdk;

/// <summary>
/// Scene preset as seen by plugins.
/// </summary>
public sealed record PluginSceneInfo(string Id, string Name, string Description);

/// <summary>
/// Lets a plugin send commands to the Denon/Marantz receiver.
/// The host gives this interface only to plugins whose manifest declares
/// <see cref="PluginCapabilities.ReceiverControl"/>. Every call is written to the app log
/// with the plugin id, so the user can see which plugin changed the receiver.
/// All methods return the receiver snapshot after the command.
/// </summary>
public interface IPluginReceiverControl
{
    Task<PluginReceiverSnapshot> SetPowerAsync(bool powerOn, CancellationToken cancellationToken = default);

    /// <summary>Selects an input by its AVR source code, for example "TV", "BD", "MPLAY", "SAT/CBL", "GAME".</summary>
    Task<PluginReceiverSnapshot> SetInputSourceAsync(string sourceCode, CancellationToken cancellationToken = default);

    /// <summary>Selects a sound mode by its AVR code, for example "MOVIE", "MUSIC", "STEREO", "DIRECT".</summary>
    Task<PluginReceiverSnapshot> SetSoundModeAsync(string modeCode, CancellationToken cancellationToken = default);

    /// <summary>Sets the master volume on the 0–98 front-panel scale.</summary>
    Task<PluginReceiverSnapshot> SetVolumeAsync(int level, CancellationToken cancellationToken = default);

    Task<PluginReceiverSnapshot> SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default);

    IReadOnlyList<PluginSceneInfo> GetScenes();

    /// <summary>Runs a Smart Scene. Matches the scene id first, then the scene name (case-insensitive).</summary>
    Task<PluginReceiverSnapshot> RunSceneAsync(string sceneIdOrName, CancellationToken cancellationToken = default);
}
