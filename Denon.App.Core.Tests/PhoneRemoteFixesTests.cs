using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The phone remote keeps its pairing on the iPhone home screen and has a real power button; the last station the
/// app started survives a restart, so a stream that still plays shows its name and logo.
/// </summary>
public sealed class PhoneRemoteFixesTests
{
    [Fact]
    public void The_last_station_survives_a_restart_and_is_forgotten_on_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "denon-last-station-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "last-station.txt");
        try
        {
            Assert.Null(new LastStationFile(path).Load());

            new LastStationFile(path).Save("p8jazz");
            Assert.Equal("p8jazz", new LastStationFile(path).Load());

            new LastStationFile(path).Save(null);
            Assert.False(File.Exists(path));
            Assert.Null(new LastStationFile(path).Load());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void The_page_keeps_the_pairing_code_in_its_address_and_sets_power_explicitly()
    {
        var html = RemoteWebUiHtml.PageHtml;
        // "Add to Home Screen" saves the address; iOS gives the icon its own storage.
        Assert.Contains("params.set('pair', token)", html);
        Assert.Matches(@"if \(token\)\s*params\.set\('pair', token\)", html); // only a missing token clears it
        Assert.Contains("post('/api/power', { on: !state.isAwake })", html);
        Assert.Contains("⏻ Turn off", html);
    }
}
