using Denon.App.Core.Heos;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>HEOS replies arrive as JSON lines, mixed with events and "command under process" notes (#59).</summary>
public sealed class HeosMessageParserTests
{
    [Fact]
    public void The_reply_to_the_command_is_found_among_events()
    {
        const string raw =
            "{\"heos\": {\"command\": \"event/player_volume_changed\", \"message\": \"pid=1&level=20\"}}\r\n" +
            "{\"heos\": {\"command\": \"player/get_players\", \"result\": \"success\", \"message\": \"\"}, \"payload\": [{\"pid\": 1}]}\r\n";

        var response = HeosMessageParser.Parse(raw, "heos://player/get_players");

        Assert.True(response.IsSuccess);
        Assert.Equal("player/get_players", response.Command);
        Assert.NotNull(response.Payload);
        Assert.Equal(1, response.Payload.Value[0].GetProperty("pid").GetInt32());
    }

    [Fact]
    public void A_reply_to_another_command_is_skipped()
    {
        const string raw =
            "{\"heos\": {\"command\": \"player/get_volume\", \"result\": \"success\", \"message\": \"pid=1&level=30\"}}\n" +
            "{\"heos\": {\"command\": \"player/get_mute\", \"result\": \"success\", \"message\": \"pid=1&state=off\"}}\n";

        var response = HeosMessageParser.Parse(raw, "heos://player/get_mute?pid=1");

        Assert.Equal("off", response.MessageFields["state"]);
    }

    [Fact]
    public void The_final_reply_wins_over_command_under_process()
    {
        const string raw =
            "{\"heos\": {\"command\": \"browse/browse\", \"result\": \"success\", \"message\": \"command under process&sid=1\"}}\n" +
            "{\"heos\": {\"command\": \"browse/browse\", \"result\": \"success\", \"message\": \"sid=1&returned=2\"}}\n";

        var response = HeosMessageParser.Parse(raw, "browse/browse");

        Assert.False(response.IsCommandUnderProcess);
        Assert.Equal("2", response.MessageFields["returned"]);
    }

    [Fact]
    public void Only_command_under_process_is_returned_when_nothing_else_came()
    {
        const string raw = "{\"heos\": {\"command\": \"browse/browse\", \"result\": \"success\", \"message\": \"command under process\"}}";

        Assert.True(HeosMessageParser.Parse(raw, "browse/browse").IsCommandUnderProcess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"heos\": ")]
    [InlineData("{\"other\": {}}")]
    public void Lines_that_are_not_heos_json_are_ignored(string line) =>
        Assert.False(HeosMessageParser.TryParseLine(line, "x", out _));

    [Fact]
    public void No_reply_throws()
    {
        const string raw = "{\"heos\": {\"command\": \"event/sources_changed\"}}\nnoise";

        Assert.Throws<InvalidOperationException>(() => HeosMessageParser.Parse(raw, "player/get_players"));
    }

    [Fact]
    public void Message_fields_are_unescaped_and_a_missing_result_means_fail()
    {
        Assert.True(HeosMessageParser.TryParseLine(
            "{\"heos\": {\"command\": \"player/get_now_playing_media\", \"message\": \"pid=1&song=Caf%C3%A9%20del%20Mar&bad=%E0%A4%A\"}}",
            "x",
            out var response));

        Assert.NotNull(response);
        Assert.False(response.IsSuccess);
        Assert.Equal("Café del Mar", response.MessageFields["song"]);
        Assert.Equal("%E0%A4%A", response.MessageFields["bad"]); // a broken escape stays as sent
    }

    [Theory]
    [InlineData("heos://player/get_players?pid=1", "player/get_players")]
    [InlineData("  /system/heart_beat/ ", "system/heart_beat")]
    [InlineData("", "")]
    public void Command_paths_are_normalized(string command, string expected) =>
        Assert.Equal(expected, HeosMessageParser.NormalizeCommandPath(command));
}
