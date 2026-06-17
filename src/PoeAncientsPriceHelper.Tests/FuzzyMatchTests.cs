using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "abd", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("vision", "viswn", 2)]
    public void Levenshtein_ComputesEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, ScanEngine.Levenshtein(a, b));
    }

    // Real misreads from the scan log should clear the fuzzy gate against the correct key,
    // while unrelated items and broader drift should not.
    [Theory]
    [InlineData("greater viswn rune", "greater vision rune", true)]
    [InlineData("greater reblrth rune", "greater rebirth rune", true)]
    [InlineData("grgater inspiration rune", "greater inspiration rune", true)]
    [InlineData("greater vxsyon rume", "greater vision rune", false)]
    [InlineData("greater vision rune", "greater rebirth rune", false)] // different item, must NOT match
    public void Similarity_AbsorbsMisreadsButNotWrongItems(string ocr, string key, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, ScanEngine.FuzzyPriceKeyAcceptsForTests(ocr, key));
    }

    // Uncut gems are pinned by type + level (no fuzzy), so the canonical key must carry both exactly.
    [Theory]
    [InlineData("uncut spirit gem level 19", "uncut spirit gem level 19")]
    [InlineData("uncut skill gem level 7", "uncut skill gem level 7")]
    [InlineData("uncut support gem level 3", "uncut support gem level 3")]
    // Boilerplate slips ("uncot", "ger", "levei") don't hide a gem or change the pinned key.
    [InlineData("uncot spirit gem level 19", "uncut spirit gem level 19")]
    [InlineData("uncul sp jrit gil j l evehg", null)]
    public void TryResolveGemKey_PinsTypeAndLevel(string ocr, string? expectedKey)
    {
        Assert.True(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Equal(expectedKey, key);
    }

    // A gem whose level can't be read is still recognised as a gem (so it never falls through to
    // fuzzy), but yields no key → the row shows '?' instead of guessing a neighbouring level.
    [Fact]
    public void TryResolveGemKey_GemWithoutLevel_RecognisedButNoKey()
    {
        Assert.True(ScanEngine.TryResolveGemKey("uncut spirit gem", out var key));
        Assert.Null(key);
    }

    // Non-gem names are left for the normal exact/prefix/fuzzy path.
    [Theory]
    [InlineData("greater vision rune")]
    [InlineData("exalted orb")]
    [InlineData("support gem ahn s citadel")]
    [InlineData("support runic infusion")]
    public void TryResolveGemKey_NonGem_ReturnsFalse(string ocr)
    {
        Assert.False(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData("pivine orb", "divine orb")]
    [InlineData("diving orej", "divine orb")]
    [InlineData("vexalteti orb", "exalted orb")]
    [InlineData("xlesser jeweller s orb", "lesser jeweller s orb")]
    [InlineData("perfect jewellers orb", "perfect jeweller s orb")]
    [InlineData("perfect jewelers orb", "perfect jeweller s orb")]
    [InlineData("perfect jewellefs oorb", "perfect jeweller s orb")]
    [InlineData("perfect transute", "perfect orb of transmutation")]
    [InlineData("perfect transmute", "perfect orb of transmutation")]
    [InlineData("perfect transmut orb", "perfect orb of transmutation")]
    [InlineData("perfect orb transmute", "perfect orb of transmutation")]
    [InlineData("perfect orb transmutation", "perfect orb of transmutation")]
    [InlineData("perfect augmentation", "perfect orb of augmentation")]
    [InlineData("perfect augmentatiom", "perfect orb of augmentation")]
    [InlineData("blacksmith whetstone", "blacksmith s whetstone")]
    [InlineData("blacksmiths whetstone", "blacksmith s whetstone")]
    [InlineData("arcanists etcher", "arcanist s etcher")]
    [InlineData("armourers scrap", "armourer s scrap")]
    [InlineData("artificers orb", "artificer s orb")]
    [InlineData("glassblowers bauble", "glassblower s bauble")]
    [InlineData("gemcutters prism", "gemcutter s prism")]
    [InlineData("biacksmith s whetstonc", "blacksmith s whetstone")]
    [InlineData("blacksmilh whet stone", "blacksmith s whetstone")]
    [InlineData("whetstone", "blacksmith s whetstone")]
    [InlineData("support ahn s citadel", "ahn s citadel")]
    [InlineData("bauble", "glassblower s bauble")]
    [InlineData("augmentation", "orb of augmentation")]
    [InlineData("chaos orh", "chaos orb")]
    [InlineData("chaos qrbj", "chaos orb")]
    [InlineData("exaltecl qrpj", "exalted orb")]
    [InlineData("ixexalted orb", "exalted orb")]
    [InlineData("exalted orbl", "exalted orb")]
    [InlineData("exalted or b", "exalted orb")]
    [InlineData("rerel oer", "regal orb")]
    [InlineData("did lx runic allo j", "runic alloy")]
    [InlineData("runic aionh", "runic alloy")]
    [InlineData("macterwark riine", "masterwork rune")]
    [InlineData("macterwark rnne", "masterwork rune")]
    [InlineData("master work rune", "masterwork rune")]
    [InlineData("masterwork riine", "masterwork rune")]
    [InlineData("orb of dt rsrhucacith", "orb of transmutation")]
    [InlineData("orb of fee mittatibn", "orb of transmutation")]
    [InlineData("my chaos mo", "greater chaos orb")]
    [InlineData("sm regal orb", "greater regal orb")]
    [InlineData("craaker exalted orb", "greater exalted orb")]
    [InlineData("like of aldur", "ire of aldur")]
    [InlineData("deioh of aldur", "passion of aldur")]
    [InlineData("deih of aldur", "passion of aldur")]
    public void TryResolvePrice_HandlesProofBundleOcrVariants(string ocr, string expectedKey)
    {
        var prices = new Dictionary<string, PriceEntry>
        {
            ["divine orb"] = new(1m, 141.1m),
            ["exalted orb"] = new(1m / 141.1m, 1m),
            ["chaos orb"] = new(0.01m, 1.4m),
            ["greater chaos orb"] = new(0.02m, 2.8m),
            ["greater exalted orb"] = new(0.02m, 2.8m),
            ["greater regal orb"] = new(0.02m, 2.8m),
            ["lesser jeweller s orb"] = new(0.0002m, 0.03m),
            ["orb of transmutation"] = new(0.001m, 0.14m),
            ["perfect jeweller s orb"] = new(0.2m, 28.2m),
            ["perfect orb of transmutation"] = new(0.079m, 11.2m),
            ["perfect orb of augmentation"] = new(0.063m, 9.0m),
            ["blacksmith s whetstone"] = new(0.001m, 0.14m),
            ["arcanist s etcher"] = new(0.001m, 0.14m),
            ["armourer s scrap"] = new(0.001m, 0.14m),
            ["artificer s orb"] = new(0.001m, 0.14m),
            ["ahn s citadel"] = new(0.08m, 11.3m),
            ["glassblower s bauble"] = new(0.001m, 0.14m),
            ["gemcutter s prism"] = new(0.001m, 0.14m),
            ["orb of augmentation"] = new(0.001m, 0.14m),
            ["regal orb"] = new(0.004m, 0.6m),
            ["masterwork rune"] = new(0.01m, 1.4m),
            ["adaptive alloy"] = new(0.01m, 1.4m),
            ["runic alloy"] = new(0.01m, 1.4m),
            ["swift alloy"] = new(0.01m, 1.4m),
            ["ire of aldur"] = new(0.01m, 1.4m),
            ["passion of aldur"] = new(0.01m, 1.4m)
        };

        Assert.True(ScanEngine.TryResolvePrice(prices, ocr, out var key, out _, out _));
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("blacksmiths whetstone", "blacksmith s whetstone")]
    [InlineData("arcanists etcher", "arcanist s etcher")]
    [InlineData("armourers scrap", "armourer s scrap")]
    [InlineData("artificers orb", "artificer s orb")]
    [InlineData("glassblowers bauble", "glassblower s bauble")]
    [InlineData("gemcutters prism", "gemcutter s prism")]
    [InlineData("cartographers chisel", "cartographer s chisel")]
    public void TryResolvePrice_TreatsPossessiveCurrencyAsExactAfterCorrection(string ocr, string expected)
    {
        var prices = new Dictionary<string, PriceEntry>
        {
            ["blacksmith s whetstone"] = new(0.001m, 0.14m),
            ["arcanist s etcher"] = new(0.001m, 0.14m),
            ["armourer s scrap"] = new(0.001m, 0.14m),
            ["artificer s orb"] = new(0.001m, 0.14m),
            ["glassblower s bauble"] = new(0.001m, 0.14m),
            ["gemcutter s prism"] = new(0.001m, 0.14m),
            ["cartographer s chisel"] = new(0.001m, 0.14m)
        };

        Assert.True(ScanEngine.TryResolvePrice(prices, ocr, out var key, out _, out var exact));
        Assert.Equal(expected, key);
        Assert.True(exact);
    }

    [Theory]
    [InlineData("5x random currency", "random currency", "5 Mirrors")]
    [InlineData("random unique item", "random unique", "HH/Mageblood")]
    [InlineData("rare unique item", "random unique", "HH/Mageblood")]
    [InlineData("unique belt", "random unique", "HH/Mageblood")]
    [InlineData("unique jewellery", "random unique", "HH/Mageblood")]
    public void BuildPriceRows_UsesFunLabelsForSemanticRewards(string ocr, string expectedName, string expectedLabel)
    {
        var rows = ScanEngine.BuildPriceRowsForTests(
            [new OcrRow(OcrScanner.NormalizeName(ocr), ocr, 10, OcrScanner.ExtractMultiplier(ocr))],
            new Dictionary<string, PriceEntry>());

        var row = Assert.Single(rows);
        Assert.True(row.HasPrice);
        Assert.Equal(expectedName, row.Name);
        Assert.Equal(expectedLabel, PriceOverlayWindow.BuildLabel(row));
    }

    [Fact]
    public void BuildPriceRows_TriesPriceMatchBeforeSuppressingImplausibleUnknowns()
    {
        var rows = ScanEngine.BuildPriceRowsForTests(
            [new OcrRow("diving orej", "diving orej", 10)],
            new Dictionary<string, PriceEntry>
            {
                ["divine orb"] = new(1m, 141.1m)
            });

        var row = Assert.Single(rows);
        Assert.True(row.HasPrice);
        Assert.Equal("divine orb", row.Name);
    }

    [Fact]
    public void BuildPriceRows_KeepsBlacksmithWhetstoneVisibleWhenPriceCacheMisses()
    {
        var rows = ScanEngine.BuildPriceRowsForTests(
            [new OcrRow("blacksmith s whetstone", "Blacksmith's Whetstone", 10)],
            new Dictionary<string, PriceEntry>());

        var row = Assert.Single(rows);
        Assert.False(row.HasPrice);
        Assert.Equal(UnpricedReason.MissingPrice, row.UnpricedReason);
        Assert.Equal("blacksmith s whetstone", row.Name);
    }

    [Fact]
    public void BuildPriceRows_KeepsPluralSkillRuneRowsVisibleWhenPriceCacheMisses()
    {
        var rows = ScanEngine.BuildPriceRowsForTests(
            [new OcrRow("skills conductive runes", "Skills Conductive Runes", 10)],
            new Dictionary<string, PriceEntry>());

        var row = Assert.Single(rows);
        Assert.False(row.HasPrice);
        Assert.Equal(UnpricedReason.MissingPrice, row.UnpricedReason);
        Assert.Equal("skills conductive runes", row.Name);
    }

    [Fact]
    public void BuildPriceRows_SuppressesImplausibleUnpricedNoise()
    {
        var rows = ScanEngine.BuildPriceRowsForTests(
            [new OcrRow("path of exile 2", "Path of Exile 2", 10)],
            new Dictionary<string, PriceEntry>());

        Assert.Empty(rows);
    }
}
