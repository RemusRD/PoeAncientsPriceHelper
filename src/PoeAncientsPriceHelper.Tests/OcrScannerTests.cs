using System.Drawing;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class OcrScannerTests
{
    [Theory]
    [InlineData("Support: Scattering Flame", "support scattering flame")]
    [InlineData("Chilling Flux", "chilling flux")]
    [InlineData("Skill: Grip Filters", "skill grip filters")]
    [InlineData("  VERISIUM FLUX  ", "verisium flux")]
    [InlineData("Rune-of-Aldur", "rune of aldur")]
    public void NormalizeName_ProducesExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, OcrScanner.NormalizeName(input));
    }

    [Fact]
    public void NormalizeName_EmptyAfterStrip_ReturnsEmpty()
    {
        Assert.Equal("", OcrScanner.NormalizeName(":::---"));
    }

    [Fact]
    public void NormalizeName_CollapseWhitespace()
    {
        Assert.Equal("a b c", OcrScanner.NormalizeName("a   b   c"));
    }

    [Theory]
    [InlineData("14x adaptive alloy", "adaptive alloy")]
    [InlineData("10xdivine orb", "divine orb")]
    [InlineData("1 mystic alloy", "mystic alloy")]
    [InlineData("3x rune of aldur", "rune of aldur")]
    [InlineData("adaptive alloy", "adaptive alloy")]
    [InlineData("1 1 adaptive alloy", "adaptive alloy")]
    [InlineData("e l8 n 1x the greatwolf s rune of willpower", "the greatwolf s rune of willpower")]
    [InlineData("oa a 1x greater orb of transmutation", "greater orb of transmutation")]
    [InlineData("b l38 unique quarterstaff", "unique quarterstaff")]
    [InlineData("krogin 1x ancient rune of decay", "ancient rune of decay")]
    [InlineData("3x my chaos mo", "my chaos mo")]
    [InlineData("3x sm regal orb", "sm regal orb")]
    [InlineData("xlesser jeweller s orb", "lesser jeweller s orb")]
    [InlineData("x chaos orb", "chaos orb")]
    [InlineData("hefod 1x ancient rune of the titan", "ancient rune of the titan")]
    [InlineData("nerog 11x ancient rune of discovery", "ancient rune of discovery")]
    [InlineData("ancient rune of shattering", "ancient rune of shattering")]
    public void StripLeadingNoise_RemovesQuantityPrefix(string input, string expected)
    {
        Assert.Equal(expected, OcrScanner.StripLeadingNoise(input));
    }

    [Theory]
    [InlineData("14x adaptive alloy", 14)]
    [InlineData("10xdivine orb", 10)]
    [InlineData("77a2x diving orej", 2)]
    [InlineData("3x rune of aldur", 3)]
    [InlineData("1 mystic alloy", 1)]              // no x marker → default 1
    [InlineData("adaptive alloy", 1)]             // no quantity → default 1
    [InlineData("e l8 n 1x the greatwolf", 1)]
    [InlineData("krogin 2x ancient rune of decay", 2)]
    [InlineData("nerog 11x ancient rune of discovery", 11)]
    [InlineData("oa a 1x greater orb of transmutation", 1)]
    [InlineData("warding rune of protection i", 1)] // roman numeral, not a multiplier
    public void ExtractMultiplier_ReadsQuantity(string input, int expected)
    {
        Assert.Equal(expected, OcrScanner.ExtractMultiplier(input));
    }

    // Removed ScanRows_Proof155133_*: it keyed off an ephemeral /tmp bundle that never exists on CI
    // (or any other machine), so it was a permanent silent no-op masquerading as OCR coverage. The
    // same real-capture currency/multiplier scenario (exalted orb x2, regal orb x3, …) is covered by
    // RuneshapeProofRegressionTests against in-repo Fixtures/Runeshape captures that actually run.
}
