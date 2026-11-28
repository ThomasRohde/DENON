using Denon.App.PluginSdk;
using Denon.Plugin.PhilipsHue;
using Denon.Plugin.PhilipsHue.Models;
using Denon.Plugin.PhilipsHue.ViewModels;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The Hue Entertainment area picker on the Music Sync panel (#79): a pick saves the area id, and a new area list
/// from the bridge selects the area in use without a save.
/// </summary>
public sealed class HueEntertainmentAreaPickerTests
{
    private static readonly HueEntertainmentArea LivingRoom = new("area-living", "Living Room", 4);
    private static readonly HueEntertainmentArea Cinema = new("area-cinema", "Cinema", 1);
    private static readonly IReadOnlyList<HueEntertainmentArea> TwoAreas = [LivingRoom, Cinema];

    [Fact]
    public void Resolve_returns_the_saved_area()
    {
        Assert.Same(Cinema, PhilipsHuePlugin.ResolveEntertainmentArea(TwoAreas, "area-cinema"));
        Assert.Same(Cinema, PhilipsHuePlugin.ResolveEntertainmentArea(TwoAreas, "AREA-CINEMA"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("area-deleted")]
    public void Resolve_falls_back_to_the_first_area(string? savedId) =>
        Assert.Same(LivingRoom, PhilipsHuePlugin.ResolveEntertainmentArea(TwoAreas, savedId));

    [Fact]
    public void Resolve_returns_null_without_areas() =>
        Assert.Null(PhilipsHuePlugin.ResolveEntertainmentArea([], "area-cinema"));

    [Fact]
    public async Task Picking_an_area_saves_its_id()
    {
        var (plugin, storage) = await CreatePluginAsync(savedAreaId: string.Empty);
        var vm = new HueDashboardViewModel(plugin);
        vm.ApplyEntertainmentAreas(TwoAreas);
        var savesBefore = storage.Saves;

        vm.SelectedEntertainmentArea = vm.EntertainmentAreas.Single(o => o.Id == Cinema.Id);

        Assert.Equal(Cinema.Id, plugin.Config.MusicSync.SelectedEntertainmentAreaId);
        Assert.Equal(savesBefore + 1, storage.Saves);
        Assert.Equal(string.Empty, vm.EntertainmentAreaHint);
        Assert.Contains("Cinema", vm.EntertainmentAreaSummary);
    }

    [Fact]
    public async Task Applying_areas_selects_the_saved_area_without_a_save()
    {
        var (plugin, storage) = await CreatePluginAsync(savedAreaId: Cinema.Id);
        var vm = new HueDashboardViewModel(plugin);
        var savesBefore = storage.Saves;

        vm.ApplyEntertainmentAreas(TwoAreas);
        vm.ApplyEntertainmentAreas(TwoAreas); // A second bridge read builds the list again.

        Assert.Equal(Cinema.Id, vm.SelectedEntertainmentArea?.Id);
        Assert.Equal(2, vm.EntertainmentAreas.Count);
        Assert.True(vm.HasEntertainmentAreas);
        Assert.Equal(savesBefore, storage.Saves);
        Assert.Equal(Cinema.Id, plugin.Config.MusicSync.SelectedEntertainmentAreaId);
    }

    [Fact]
    public async Task An_unknown_saved_area_shows_the_area_the_plugin_uses_and_stays_saved()
    {
        var (plugin, storage) = await CreatePluginAsync(savedAreaId: "area-deleted");
        var vm = new HueDashboardViewModel(plugin);
        var savesBefore = storage.Saves;

        vm.ApplyEntertainmentAreas(TwoAreas);

        var used = PhilipsHuePlugin.ResolveEntertainmentArea(TwoAreas, "area-deleted");
        Assert.Equal(used?.Id, vm.SelectedEntertainmentArea?.Id);
        Assert.Equal(LivingRoom.Id, vm.SelectedEntertainmentArea?.Id);
        Assert.Equal("area-deleted", plugin.Config.MusicSync.SelectedEntertainmentAreaId);
        Assert.Equal(savesBefore, storage.Saves);
        Assert.Contains("gone", vm.EntertainmentAreaHint);
    }

    [Fact]
    public async Task No_areas_disables_the_picker_with_a_hint()
    {
        var (plugin, _) = await CreatePluginAsync(savedAreaId: Cinema.Id);
        var vm = new HueDashboardViewModel(plugin);
        vm.ApplyEntertainmentAreas(TwoAreas);

        vm.ApplyEntertainmentAreas([]);

        Assert.False(vm.HasEntertainmentAreas);
        Assert.Null(vm.SelectedEntertainmentArea);
        Assert.Contains("create one in the Hue app", vm.EntertainmentAreaHint);
        Assert.Equal(Cinema.Id, plugin.Config.MusicSync.SelectedEntertainmentAreaId);
    }

    [Fact]
    public void Area_label_shows_name_and_channel_count()
    {
        Assert.Equal("Living Room • 4 channels", new HueEntertainmentAreaOption(LivingRoom).Label);
        Assert.Equal("Cinema • 1 channel", new HueEntertainmentAreaOption(Cinema).Label);
    }

    [Fact]
    public async Task Music_sync_settings_save_keeps_the_saved_area()
    {
        var (plugin, _) = await CreatePluginAsync(savedAreaId: "area-deleted");
        var vm = new HueDashboardViewModel(plugin);
        vm.ApplyEntertainmentAreas(TwoAreas);

        vm.MusicSyncSensitivity = 2.0; // Saves the Music Sync settings.

        Assert.Equal("area-deleted", plugin.Config.MusicSync.SelectedEntertainmentAreaId);
    }

    private static async Task<(PhilipsHuePlugin Plugin, CountingStorage Storage)> CreatePluginAsync(string savedAreaId)
    {
        var config = new HuePluginConfig { MusicSync = new HueMusicSyncConfig { SelectedEntertainmentAreaId = savedAreaId } };
        var storage = new CountingStorage(config);
        var plugin = new PhilipsHuePlugin();
        await plugin.InitializeAsync(new FakeContext(storage));
        return (plugin, storage);
    }

    private sealed class CountingStorage(HuePluginConfig config) : IPluginStorage
    {
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public string PluginDataDirectory => Path.GetTempPath();

        public Task<T> LoadConfigAsync<T>(Func<T> defaultFactory, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(config as T ?? defaultFactory());

        public Task SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default)
            where T : class
        {
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeContext(IPluginStorage storage) : IPluginContext, IPluginLogger, IPluginEventLogger, IPluginReceiverBridge
    {
        public string PluginId => "philips-hue";

        public IPluginLogger Logger => this;

        public IPluginEventLogger Events => this;

        public IPluginStorage Storage => storage;

        public IPluginReceiverBridge Receiver => this;

        public IPluginReceiverControl? ReceiverControl => null;

        public bool IsAppShuttingDown => false;

        public PluginReceiverSnapshot CurrentSnapshot => PluginReceiverSnapshot.Empty;

        // Never used to reach a bridge: the tests do not start the plugin.
        public HttpClient CreateHttpClient(bool allowLocalSelfSignedTls = false, TimeSpan? timeout = null) => new();

        public void ReportStatus(string statusText, PluginHealthState health)
        {
        }

        public void ReportStatus(string statusText, PluginHealthState health, IReadOnlyDictionary<string, string>? details)
        {
        }

        public Task<PluginActionResult> RunPluginActionAsync(
            string pluginId,
            string actionId,
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PluginActionResult.Fail("Not in tests."));

        public void Info(string message)
        {
        }

        public void Warning(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }

        public void RecordEvent(
            string eventName,
            IReadOnlyDictionary<string, object?>? data = null,
            string? stream = null,
            string origin = "plugin")
        {
        }
    }
}
