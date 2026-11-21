namespace Denon.App.PluginSdk;

/// <summary>
/// Zero-allocation real-time audio analysis frame published by Denon Desktop's WASAPI FFT engine
/// to plugins implementing <see cref="IAudioReactivePlugin"/>.
/// </summary>
public readonly record struct PluginAudioFrame(
    double LeftLevel,
    double RightLevel,
    double BassEnergy,
    double MidEnergy,
    double TrebleEnergy,
    double BeatIntensity,
    bool IsBeat,
    bool IsLiveSignal,
    long TimestampMs);

public sealed record PluginAlbumArtPalette(
    string DominantHex,
    string SecondaryHex,
    string TrackTitle,
    string ImageUrl);

/// <summary>
/// Optional interface for plugins that react in real time to live FFT audio spectrum/beats
/// and currently playing album artwork color palettes.
/// </summary>
public interface IAudioReactivePlugin
{
    /// <summary>
    /// True while this plugin actively wants real-time FFT audio frames streamed from the host.
    /// When false across all plugins, the host can keep FFT dispatch idle to save CPU.
    /// </summary>
    bool IsAudioSyncActive { get; }

    /// <summary>
    /// Hot-path callback invoked at ~30–60 Hz with live stereo levels, 3-band FFT energy, and beat onset detection.
    /// Implementations must be non-blocking.
    /// </summary>
    void OnAudioFrame(in PluginAudioFrame frame);

    /// <summary>
    /// Invoked whenever the currently playing track's artwork yields a new dominant color palette.
    /// </summary>
    Task OnAlbumArtPaletteChangedAsync(PluginAlbumArtPalette palette, CancellationToken cancellationToken = default);
}
