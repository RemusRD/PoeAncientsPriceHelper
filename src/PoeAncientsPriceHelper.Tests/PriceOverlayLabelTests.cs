using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class PriceOverlayLabelTests
{
    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.20", "1.2")]
    [InlineData("1.23", "1.23")]
    [InlineData("10.00", "10")]
    [InlineData("10.50", "10.5")]
    [InlineData("0.030", "0.03")]
    public void FormatAmount_TrimsNoiseWithoutRoundingToZero(string raw, string expected)
    {
        Assert.Equal(expected, PriceLabels.FormatAmount(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BuildLabel_MultipliesStackedDivinesAndShowsEachValue()
    {
        var row = new PriceRow(10, "10x Divine Orb", 1m, 141.1m, true, Multiplier: 10, Name: "divine orb", ExactMatch: true);

        Assert.Equal("10d (1d ea)", PriceLabels.BuildLabel(row));
    }

    [Fact]
    public void BuildLabel_ShowsEachValueForStackedExalts()
    {
        var row = new PriceRow(10, "2x Exalted Orb", 1m / 141.1m, 1m, true, Multiplier: 2, Name: "exalted orb", ExactMatch: true);

        Assert.Equal("2ex (1ex ea)", PriceLabels.BuildLabel(row));
    }

    [Fact]
    public void BuildLabel_KeepsSmallExaltedValuesVisible()
    {
        var row = new PriceRow(10, "Lesser Jeweller's Orb", 0.0002m, 0.03m, true, Name: "lesser jeweller s orb", ExactMatch: true);

        Assert.Equal("0.03ex", PriceLabels.BuildLabel(row));
    }

    [Theory]
    [InlineData(1, "1 Mirror")]
    [InlineData(5, "5 Mirrors")]
    public void BuildLabel_UsesFunMirrorLabelForRandomCurrency(int multiplier, string expected)
    {
        var row = new PriceRow(10, $"{multiplier}x Random Currency", 0m, 0m, true, Multiplier: multiplier, Name: "random currency", ExactMatch: true, Meme: MemeKind.Mirror);

        Assert.Equal(expected, PriceLabels.BuildLabel(row));
    }

    [Fact]
    public void BuildLabel_UsesFunChaseUniqueLabelForRandomUniques()
    {
        var row = new PriceRow(10, "Random Unique Item", 0m, 0m, true, Name: "random unique", ExactMatch: true, Meme: MemeKind.Headhunter);

        Assert.Equal("HH/Mageblood", PriceLabels.BuildLabel(row));
    }
}
