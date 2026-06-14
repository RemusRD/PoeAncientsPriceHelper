using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ProofBundleRegressionTests
{
    [Fact]
    public void Proof151330_PricesKnownRowsAndMarksUncutGemLevelUnknown()
    {
        var rows = new[]
        {
            FromRaw("ﬂ v1x Uncul:_|Sp_jrit Gil‘j‘l Evehg_)", 85),
            FromRaw("i 1x Lesser Jeweller’-s. Orb]", 156),
            FromRaw("| kg 2X vExalteti Orb=", 282)
        };

        var resolved = ScanEngine.BuildPriceRowsForTests(rows, Prices()).ToList();

        Assert.Equal(3, resolved.Count);
        Assert.False(resolved[0].HasPrice);
        Assert.Equal(UnpricedReason.NeedsGemLevel, resolved[0].UnpricedReason);

        Assert.True(resolved[1].HasPrice);
        Assert.Equal("lesser jeweller s orb", resolved[1].Name);
        Assert.Equal("0.03ex", PriceOverlayWindow.BuildLabel(resolved[1]));

        Assert.True(resolved[2].HasPrice);
        Assert.Equal("exalted orb", resolved[2].Name);
        Assert.Equal(2, resolved[2].Multiplier);
        Assert.Equal("2ex (1ex ea)", PriceOverlayWindow.BuildLabel(resolved[2]));
    }

    [Fact]
    public void Proof151610_DivineOcrVariantsResolveAndQuantityIsRecoveredWhenSeen()
    {
        var rows = new[]
        {
            FromRaw("BB g pivine orb", 71),
            FromRaw("ﬂx Vi s g 77A2x Diving OrEJ", 236),
            FromRaw("10xDivine Orb", 299)
        };

        var resolved = ScanEngine.BuildPriceRowsForTests(rows, Prices()).ToList();

        Assert.All(resolved, row =>
        {
            Assert.True(row.HasPrice);
            Assert.Equal("divine orb", row.Name);
        });
        Assert.Equal(1, resolved[0].Multiplier);
        Assert.Equal(2, resolved[1].Multiplier);
        Assert.Equal(10, resolved[2].Multiplier);
        Assert.Equal("10d (1d ea)", PriceOverlayWindow.BuildLabel(resolved[2]));
    }

    private static OcrRow FromRaw(string raw, int centerY)
    {
        var normalizedRaw = OcrScanner.NormalizeName(raw);
        return new OcrRow(
            OcrScanner.StripLeadingNoise(normalizedRaw),
            raw,
            centerY,
            OcrScanner.ExtractMultiplier(normalizedRaw));
    }

    private static IReadOnlyDictionary<string, PriceEntry> Prices() => new Dictionary<string, PriceEntry>
    {
        ["divine orb"] = new(1m, 141.1m),
        ["exalted orb"] = new(1m / 141.1m, 1m),
        ["lesser jeweller s orb"] = new(0.0002m, 0.03m),
        ["perfect jeweller s orb"] = new(0.2m, 28.2m),
        ["uncut spirit gem level 19"] = new(0.12m, 16.9m)
    };
}
