using System.Drawing;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class RuneshapeProofRegressionTests
{
    [Fact]
    public void Fixture_SpiritGemUnhovered_ReadsAllVisibleRows()
    {
        using var scanner = NewScannerOrSkip();
        if (scanner is null) return;

        var rows = ScanFixtureRows(scanner, "spirit-gem-unhovered");

        Assert.Contains(rows, r => r.NormalizedName == "uncut spirit gem level 19" && r.Multiplier == 1);
        Assert.Contains(rows, r => r.NormalizedName == "greater orb of augmentation" && r.Multiplier == 3);
        Assert.Contains(rows, r => r.NormalizedName == "lesser jewellers orb" && r.Multiplier == 1);
        Assert.Contains(rows, r => r.NormalizedName == "regal orb" && r.Multiplier == 3);
        Assert.Contains(rows, r => r.NormalizedName == "exalted orb" && r.Multiplier == 2);
    }

    [Fact]
    public void Fixture_SevenRowPanel_ReadsTransmutationAndTreatsUniqueJewelleryAsSemanticReward()
    {
        using var scanner = NewScannerOrSkip();
        if (scanner is null) return;

        var ocrRows = ScanFixtureRows(scanner, "seven-row-transmutation-jewellery");
        var displayRows = ScanEngine.BuildPriceRowsForTests(ocrRows, ProofPrices);

        Assert.Contains(displayRows, r => r.Name == "orb of transmutation" && r.HasPrice && r.Multiplier == 2);

        var unique = Assert.Single(displayRows, r => r.OcrText.Contains("Unique Jewellery", StringComparison.OrdinalIgnoreCase));
        Assert.True(unique.HasPrice);
        Assert.Equal("random unique", unique.Name);
        Assert.Equal("HH/Mageblood", PriceOverlayWindow.BuildLabel(unique));
    }

    [Fact]
    public void Fixture_TwoRowLargeLeftLayout_FallsBackToFullRegionInsteadOfCollapsingToOneRow()
    {
        using var scanner = NewScannerOrSkip();
        if (scanner is null) return;

        using var bmp = LoadFixture("two-row-large-left-layout");
        var detection = new RuneshapeRowDetector().Detect(bmp);
        var rowStripOcrRows = scanner.ScanRows(bmp, detection.Rows);
        var rowStripDisplayRows = ScanEngine.BuildPriceRowsForTests(rowStripOcrRows, ProofPrices);

        Assert.True(ScanEngine.ShouldTryFullRegionFallbackForTests(
            detection,
            rowStripOcrRows.Count,
            rowStripDisplayRows.Count,
            rowStripDisplayRows.Count(r => r.HasPrice)));

        var fullRegionRows = scanner.Scan(bmp);
        var fullRegionDisplayRows = ScanEngine.BuildPriceRowsForTests(fullRegionRows, ProofPrices);

        Assert.Contains(fullRegionDisplayRows, r => r.Name == "mystic alloy" && r.HasPrice);
        Assert.Contains(fullRegionDisplayRows, r => r.Name == "masterwork rune" && r.HasPrice);
        Assert.True(fullRegionDisplayRows.Count >= detection.Rows.Count);
    }

    [Fact]
    public void Fixture_LongScrolledList_DetectorDoesNotStopAtFirstFiveRows()
    {
        using var bmp = LoadFixture("long-list-skipped-middle");

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.True(detection.HasUsableRows);
        Assert.True(detection.Rows.Count >= 10);
        Assert.Contains(detection.Rows, row => row.CenterY is >= 285 and <= 310);
        Assert.Contains(detection.Rows, row => row.CenterY is >= 345 and <= 375);
        Assert.Contains(detection.Rows, row => row.CenterY is >= 475 and <= 505);
        Assert.Contains(detection.Rows, row => row.CenterY is >= 665 and <= 695);
    }

    [Fact]
    public void Fixture_AldurTallRightAlignedRows_ReadsEveryVisibleReward()
    {
        using var scanner = NewScannerOrSkip();
        if (scanner is null) return;

        using var bmp = LoadFixture("aldur-tall-right-aligned");
        var detection = new RuneshapeRowDetector().Detect(bmp);
        var ocrRows = scanner.ScanRows(bmp, detection.Rows);
        var displayRows = ScanEngine.BuildPriceRowsForTests(ocrRows, ProofPrices);

        Assert.Equal(4, detection.Rows.Count);
        Assert.Contains(displayRows, r => r.Name == "betrayal of aldur" && r.HasPrice);
        Assert.Contains(displayRows, r => r.Name == "ire of aldur" && r.HasPrice);
        Assert.Contains(displayRows, r => r.Name == "passion of aldur" && r.HasPrice);
        Assert.Contains(displayRows, r => r.Name == "breath of aldur" && r.HasPrice);
    }

    private static IReadOnlyList<OcrRow> ScanFixtureRows(OcrScanner scanner, string fixtureName)
    {
        using var bmp = LoadFixture(fixtureName);
        var detection = new RuneshapeRowDetector().Detect(bmp);
        return scanner.ScanRows(bmp, detection.Rows);
    }

    private static Bitmap LoadFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Runeshape", fixtureName, "capture_region.png");
        return (Bitmap)Image.FromFile(path);
    }

    private static OcrScanner? NewScannerOrSkip()
    {
        var tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        return Directory.Exists(tessdataDir) ? new OcrScanner(tessdataDir) : null;
    }

    private static readonly Dictionary<string, PriceEntry> ProofPrices = new()
    {
        ["uncut spirit gem level 19"] = new(0.12m, 17.8m),
        ["greater orb of augmentation"] = new(0.001m, 0.15m),
        ["lesser jeweller s orb"] = new(0.0002m, 0.03m),
        ["orb of transmutation"] = new(0.001m, 0.15m),
        ["orb of alchemy"] = new(0.003m, 0.5m),
        ["exalted orb"] = new(0.006m, 1m),
        ["lesser ward rune"] = new(0.008m, 1.1m),
        ["lesser rebirth rune"] = new(0.02m, 3m),
        ["regal orb"] = new(0.002m, 0.3m),
        ["mystic alloy"] = new(0.035m, 5.1m),
        ["masterwork rune"] = new(0.38m, 55.4m),
        ["betrayal of aldur"] = new(0.01m, 1.5m),
        ["ire of aldur"] = new(0.01m, 1.5m),
        ["passion of aldur"] = new(0.01m, 1.5m),
        ["breath of aldur"] = new(0.01m, 1.5m),
    };
}
