using Denon.Plugin.HomeAssistant;
using Denon.Plugin.PhilipsHue.Services;
using Denon.Plugin.PhilipsHue.ViewModels;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The Home Assistant and Philips Hue plugins: only listed entity kinds can be triggered (#32), and a failed command
/// shows an error instead of closing the app (#46). A Hue read that answers with an error says so (#64).
/// </summary>
public sealed class HomeAutomationPluginTests
{
    [Theory]
    [InlineData("scene.movie_time", "scene")]
    [InlineData("script.close_theater_curtains", "script")]
    [InlineData("switch.subwoofer_smart_plug", "switch")]
    [InlineData("light.kitchen_2", "light")]
    [InlineData("input_boolean.guest_mode", "input_boolean")]
    [InlineData("  scene.evening_relax ", "scene")]
    public void A_listed_entity_id_is_accepted(string entityId, string domain)
    {
        Assert.True(HomeAssistantPlugin.TryParseEntityId(entityId, out var parsed));
        Assert.Equal(domain, parsed);
    }

    [Theory]
    [InlineData("cover.garage_door")] // A domain the plugin does not list: toggle would open the garage.
    [InlineData("lock.front_door")]
    [InlineData("alarm_control_panel.home")]
    [InlineData("scene.%2e%2e/%2e%2e/events/x")] // Path traversal into other API endpoints.
    [InlineData("../config/core/check_config#.x")]
    [InlineData("scene.a/b")]
    [InlineData("scene.Movie")]
    [InlineData("scene.")]
    [InlineData(".movie")]
    [InlineData("scene")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_entity_id_is_refused(string? entityId)
    {
        Assert.False(HomeAssistantPlugin.TryParseEntityId(entityId, out var domain));
        Assert.Equal(string.Empty, domain);
    }

    [Fact]
    public void A_failed_home_assistant_tile_click_shows_an_error_instead_of_crashing()
    {
        var viewModel = new HomeAssistantViewModel(new HomeAssistantPlugin());

        // Before, the exception left an async void command and closed the app.
        var ex = Record.Exception(() =>
            viewModel.TriggerEntityCommand.Execute(new HaEntityItem("cover.garage_door", "Garage", "cover", "closed")));

        Assert.Null(ex);
        Assert.Contains("⚠", viewModel.StatusMessage);
        Assert.Contains("cover.garage_door", viewModel.StatusMessage);
        Assert.True(viewModel.TriggerEntityCommand.CanExecute(null)); // Not stuck busy.
    }

    [Fact]
    public async Task A_failed_hue_command_goes_to_its_dashboards_error_handler()
    {
        Exception? seen = null;
        var command = HueAsyncCommand.WithErrors(e => seen = e, async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Could not reach Philips Hue Bridge");
        });

        command.Execute(null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (seen is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.IsType<InvalidOperationException>(seen);
        Assert.True(command.CanExecute(null));

        // Without a handler the error is dropped, never thrown out of async void.
        var silent = new HueAsyncCommand(_ => throw new InvalidOperationException("x"));
        Assert.Null(Record.Exception(() => silent.Execute(null)));
    }

    [Fact]
    public async Task Saving_home_assistant_without_url_or_token_does_not_claim_a_sync()
    {
        var viewModel = new HomeAssistantViewModel(new HomeAssistantPlugin());

        viewModel.SaveAndConnectCommand.Execute(null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!viewModel.StatusMessage.StartsWith('✓') && !viewModel.StatusMessage.Contains('⚠') && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains("Enter the Home Assistant URL", viewModel.StatusMessage);
        Assert.DoesNotContain("synced", viewModel.StatusMessage);
    }

    [Fact]
    public void A_hue_read_that_answers_with_an_error_throws_its_description()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            HueBridgeClient.ThrowIfHueError("""[{"error":{"type":7,"address":"/lights","description":"invalid value"}}]"""));
        Assert.Contains("invalid value", error.Message);

        HueBridgeClient.ThrowIfHueError("""{"1":{"name":"Lamp"}}""");
        HueBridgeClient.ThrowIfHueError("""[{"success":{"/lights/1/state/on":true}}]""");
    }
}
