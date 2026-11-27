using Denon.App.Desktop.Controls;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The grid of the plugin list: columns and card width for a number of cards in a width.</summary>
public sealed class EvenWrapPanelTests
{
    [Theory]
    // width, cards, min, max, gap -> columns, card width
    [InlineData(1200, 3, 250, 400, 8, 3, 394.667)]   // 3 cards share the row
    [InlineData(1200, 6, 250, 400, 8, 4, 294)]       // 6 cards: 4 columns, 2 rows
    [InlineData(1700, 3, 250, 400, 8, 3, 400)]       // a wide window: a card stops at the largest width
    [InlineData(520, 6, 250, 400, 8, 2, 256)]        // a narrow window: 2 columns
    [InlineData(200, 6, 250, 400, 8, 1, 200)]        // narrower than one card: 1 column that takes the width there is
    [InlineData(1200, 30, 250, 400, 8, 4, 294)]      // a long list keeps the same grid
    public void Columns_fill_the_width_and_all_cards_have_the_same_width(
        double width, int cards, double min, double max, double gap, int columns, double itemWidth)
    {
        var fit = EvenWrapPanel.Fit(width, cards, min, max, gap);

        Assert.Equal(columns, fit.Columns);
        Assert.Equal(itemWidth, fit.ItemWidth, 3);
    }

    [Fact]
    public void No_cards_and_no_width_limit_have_a_plain_answer()
    {
        Assert.Equal((0, 0), EvenWrapPanel.Fit(1200, 0, 250, 400, 8));
        Assert.Equal((5, 250), EvenWrapPanel.Fit(double.PositiveInfinity, 5, 250, 400, 8));
    }
}
