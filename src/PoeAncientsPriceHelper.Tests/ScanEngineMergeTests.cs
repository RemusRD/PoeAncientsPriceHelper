using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ScanEngineMergeTests
{
    [Fact]
    public void MergeReads_EvictsUnmatchedLockedRows_WhenCaptureChanged()
    {
        using var engine = NewEngine();
        var oldRows = new[]
        {
            Row(20, "orb of alchemy"),
            Row(82, "chaos orb"),
            Row(145, "exalted orb"),
            Row(208, "runic alloy"),
            Row(271, "regal orb"),
            Row(334, "exalted orb"),
        };

        var firstDisplay = engine.MergeReadsForTests(oldRows, evictUnmatchedImmediately: true);

        Assert.Equal(6, firstDisplay.Count);

        var newRows = new[]
        {
            Row(23, "chaos orb", multiplier: 3),
            Row(95, "perfect jeweller s orb"),
        };

        var secondDisplay = engine.MergeReadsForTests(newRows, evictUnmatchedImmediately: true);

        Assert.Equal(["chaos orb", "perfect jeweller s orb"], secondDisplay.Select(r => r.Name).ToArray());
        Assert.DoesNotContain(secondDisplay, r => r.Name == "runic alloy");
        Assert.DoesNotContain(secondDisplay, r => r.CenterY > 120);
    }

    [Fact]
    public void MergeReads_KeepsLockedRows_WhenCaptureDidNotChange()
    {
        using var engine = NewEngine();
        _ = engine.MergeReadsForTests([Row(20, "orb of alchemy"), Row(82, "chaos orb")], evictUnmatchedImmediately: true);

        var display = engine.MergeReadsForTests([Row(20, "orb of alchemy")], evictUnmatchedImmediately: false);

        Assert.Equal(["orb of alchemy", "chaos orb"], display.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void MergeReads_ShowsPendingFuzzyPriceAsLoadingUntilConfirmed()
    {
        using var engine = NewEngine();
        var fuzzy = Row(20, "blacksmith s whetstone", exact: false);

        var firstDisplay = engine.MergeReadsForTests([fuzzy], evictUnmatchedImmediately: true);

        var pending = Assert.Single(firstDisplay);
        Assert.False(pending.HasPrice);
        Assert.Equal(UnpricedReason.Loading, pending.UnpricedReason);

        var secondDisplay = engine.MergeReadsForTests([fuzzy], evictUnmatchedImmediately: false);

        var locked = Assert.Single(secondDisplay);
        Assert.True(locked.HasPrice);
        Assert.Equal("blacksmith s whetstone", locked.Name);
    }

    private static ScanEngine NewEngine()
    {
        var http = new HttpClient();
        return new ScanEngine(new AppConfig(), new PriceRepository(http), new IconCache(http));
    }

    private static PriceRow Row(int y, string name, int multiplier = 1, bool exact = true) =>
        new(y, name, 0.01m, 1m, true, multiplier, name, ExactMatch: exact);
}
