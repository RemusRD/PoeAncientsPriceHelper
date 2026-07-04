using System.Drawing;
using System.Text.Json;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class OverlayAlignmentPipelineTests
{
    [Theory]
    [InlineData(false, 42, 96, 56)]
    [InlineData(false, 42, 96, 84)]
    [InlineData(true, 48, 134, 109)]
    [InlineData(true, 48, 134, 124)]
    public void RowStripOcr_PublishesRewardTextCenterNotVisualBandCenter(
        bool tall,
        int top,
        int bottom,
        int textCenterY)
    {
        var kind = tall ? RowKind.Tall : RowKind.Standard;
        var row = new RuneshapeRow(top, bottom, (top + bottom) / 2, textCenterY, kind);

        var displayCenter = OcrScanner.RowDisplayCenterForTests(row);

        Assert.Equal(textCenterY, displayCenter);
        if (textCenterY != row.CenterY)
            Assert.NotEqual(row.CenterY, displayCenter);
    }

    [Fact]
    public void PriceRows_PreserveOcrDisplayCenters()
    {
        var ocrRows = new[]
        {
            new OcrRow("orb of alchemy", "Orb of Alchemy", 56),
            new OcrRow("breath of aldur", "Breath of Aldur", 225),
            new OcrRow("unknown reward name", "Unknown Reward Name", 341),
        };
        var prices = new Dictionary<string, PriceEntry>
        {
            ["orb of alchemy"] = new(0.003m, 0.5m),
            ["breath of aldur"] = new(0.01m, 1.5m),
        };

        var rows = ScanEngine.BuildPriceRowsForTests(ocrRows, prices);

        Assert.Collection(rows,
            row =>
            {
                Assert.True(row.HasPrice);
                Assert.Equal(56, row.CenterY);
                Assert.Equal("orb of alchemy", row.Name);
            },
            row =>
            {
                Assert.True(row.HasPrice);
                Assert.Equal(225, row.CenterY);
                Assert.Equal("breath of aldur", row.Name);
            },
            row =>
            {
                Assert.False(row.HasPrice);
                Assert.Equal(341, row.CenterY);
                Assert.Equal(UnpricedReason.MissingPrice, row.UnpricedReason);
            });
    }

    [Fact]
    public void OverlayJson_PreservesDisplayCentersAndUnpricedReasons()
    {
        OverlayJson.ResetCacheForTests();
        var rows = new[]
        {
            new PriceRow(109, "Breath of Aldur", 0.01m, 1.5m, true, Name: "breath of aldur", ExactMatch: true),
            new PriceRow(225, "Uncut Skill Gem", 0m, 0m, false, Name: "uncut skill gem", UnpricedReason: UnpricedReason.NeedsGemLevel),
        };
        using var output = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(output);
            OverlayJson.UpdateState(rows, panelOpen: true, reading: false);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal("overlayRows", root.GetProperty("event").GetString());
        Assert.True(root.GetProperty("panelOpen").GetBoolean());
        Assert.False(root.GetProperty("reading").GetBoolean());

        var jsonRows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(109, jsonRows[0].GetProperty("centerY").GetInt32());
        Assert.True(jsonRows[0].GetProperty("hasPrice").GetBoolean());
        Assert.Equal("breath of aldur", jsonRows[0].GetProperty("name").GetString());

        Assert.Equal(225, jsonRows[1].GetProperty("centerY").GetInt32());
        Assert.False(jsonRows[1].GetProperty("hasPrice").GetBoolean());
        Assert.Equal("NeedsGemLevel", jsonRows[1].GetProperty("reason").GetString());
    }

    [Fact]
    public void OverlayJson_DedupesRepeatedVisibleStateAndHide()
    {
        OverlayJson.ResetCacheForTests();
        var region = new Rectangle(69, 205, 663, 715);
        var rows = new[]
        {
            new PriceRow(109, "Breath of Aldur", 0.01m, 1.5m, true, Name: "breath of aldur", ExactMatch: true),
        };
        using var output = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(output);
            OverlayJson.EnsureVisible(region, 8);
            OverlayJson.EnsureVisible(region, 8);
            OverlayJson.UpdateState(rows, panelOpen: true, reading: false);
            OverlayJson.UpdateState(rows, panelOpen: true, reading: false);
            OverlayJson.HideNow();
            OverlayJson.HideNow();
        }
        finally
        {
            Console.SetOut(originalOut);
            OverlayJson.ResetCacheForTests();
        }

        var events = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString())
            .ToArray();

        Assert.Equal(new[] { "overlayShow", "overlayRows", "overlayHide" }, events);
    }

    [Fact]
    public void OverlayJson_ReEmitsShowAfterHide_SameRegion()
    {
        // Hide() resets the show-dedup state, so showing the same region again after a hide must
        // re-emit overlayShow. A regression where Hide forgot to re-arm _isHidden/_lastShowKey would
        // leave the overlay permanently hidden, and nothing else covers this re-arm path.
        OverlayJson.ResetCacheForTests();
        var region = new Rectangle(69, 205, 663, 715);
        using var output = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(output);
            OverlayJson.EnsureVisible(region, 8);
            OverlayJson.HideNow();
            OverlayJson.EnsureVisible(region, 8);
        }
        finally
        {
            Console.SetOut(originalOut);
            OverlayJson.ResetCacheForTests();
        }

        var events = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString())
            .ToArray();

        Assert.Equal(new[] { "overlayShow", "overlayHide", "overlayShow" }, events);
    }

    [Fact]
    public void OverlayJson_SerializesMemeAndReasonTokensTheOverlayRendererExpects()
    {
        // overlay.js hard-codes these exact strings (row.meme === 'Mirror'/'Headhunter',
        // row.reason === 'Loading', markerText('NeedsGemLevel')). Renaming a MemeKind/UnpricedReason
        // member would silently break the overlay; pin the JSON contract here.
        OverlayJson.ResetCacheForTests();
        var rows = new[]
        {
            new PriceRow(10, "x", 0m, 0m, false, Name: "a", UnpricedReason: UnpricedReason.Loading),
            new PriceRow(20, "x", 0m, 0m, false, Name: "b", UnpricedReason: UnpricedReason.MissingPrice),
            new PriceRow(30, "x", 0m, 0m, false, Name: "c", UnpricedReason: UnpricedReason.NeedsGemLevel),
            new PriceRow(40, "x", 0m, 0m, false, Name: "d", UnpricedReason: UnpricedReason.Unknown),
            new PriceRow(50, "x", 0m, 0m, true,  Name: "e", Meme: MemeKind.Mirror),
            new PriceRow(60, "x", 0m, 0m, true,  Name: "f", Meme: MemeKind.Headhunter),
            new PriceRow(70, "x", 0.01m, 1.5m, true, Name: "g", Meme: MemeKind.None),
        };
        using var output = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(output);
            OverlayJson.UpdateState(rows, panelOpen: true, reading: false);
        }
        finally
        {
            Console.SetOut(originalOut);
            OverlayJson.ResetCacheForTests();
        }

        var jsonRows = JsonDocument.Parse(output.ToString()).RootElement
            .GetProperty("rows").EnumerateArray().ToArray();

        Assert.Equal("Loading", jsonRows[0].GetProperty("reason").GetString());
        Assert.Equal("MissingPrice", jsonRows[1].GetProperty("reason").GetString());
        Assert.Equal("NeedsGemLevel", jsonRows[2].GetProperty("reason").GetString());
        Assert.Equal("Unknown", jsonRows[3].GetProperty("reason").GetString());
        Assert.Equal("Mirror", jsonRows[4].GetProperty("meme").GetString());
        Assert.Equal("Headhunter", jsonRows[5].GetProperty("meme").GetString());
        Assert.Equal("None", jsonRows[6].GetProperty("meme").GetString());
    }

    [Fact]
    public void OverlayJson_DoesNotRefreshForDebugOnlyRowMovement()
    {
        OverlayJson.ResetCacheForTests();
        var rows = new[]
        {
            new PriceRow(109, "Breath of Aldur", 0.01m, 1.5m, true, Name: "breath of aldur", ExactMatch: true),
        };
        var firstDebug = new[]
        {
            new RuneshapeRow(42, 96, 69, 56, RowKind.Standard),
        };
        var secondDebug = new[]
        {
            new RuneshapeRow(44, 98, 71, 58, RowKind.Standard),
        };
        using var output = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(output);
            OverlayJson.UpdateState(rows, panelOpen: true, reading: false, firstDebug, debugLayout: true);
            OverlayJson.UpdateState(rows, panelOpen: true, reading: false, secondDebug, debugLayout: true);
        }
        finally
        {
            Console.SetOut(originalOut);
            OverlayJson.ResetCacheForTests();
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.True(doc.RootElement.GetProperty("debugLayout").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("debugRows", out _));
    }
}
