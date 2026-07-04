using PoeAncientsPriceHelper.Core;

namespace PoeAncientsPriceHelper.Core.Tests;

public class RuneshapeRowDetectorTests
{
    // Loads the REAL aldur-tall-right-aligned fixture (the same capture the Windows regression
    // suite asserts over) and verifies the headless Core detector produces identical results on
    // macOS. If this passes, the detector port is byte-for-byte behavior-compatible.
    [Fact]
    public void Detect_AldurFixture_FindsFourTallRows()
    {
        var path = FixturePath("aldur-tall-right-aligned");
        var gray = GrayLoader.Load(path);

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(4, detection.Rows.Count);
        Assert.All(detection.Rows, r => Assert.Equal(RowKind.Tall, r.Kind));
        Assert.True(detection.Confidence >= 0.25);
    }

    [Theory]
    [InlineData("aldur-tall-right-aligned", 4)]
    [InlineData("long-list-skipped-middle", 10)]
    [InlineData("nae72-alignment-current", 6)]
    [InlineData("nae72-alignment-gems", 6)]
    [InlineData("seven-row-transmutation-jewellery", 7)]
    [InlineData("spirit-gem-unhovered", 5)]
    public void Detect_RealFixtures_FindsUsableRowsAcrossCapturedLayouts(string fixtureName, int minimumRows)
    {
        var gray = GrayLoader.Load(FixturePath(fixtureName));

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.True(detection.Rows.Count >= minimumRows);
        Assert.All(detection.Rows, row =>
        {
            Assert.InRange(row.CenterY, row.Top, row.Bottom);
            Assert.InRange(row.TextCenterY, row.Top, row.Bottom);
        });
    }

    [Theory]
    [InlineData("capture-20260620-tall-currency-mixed", 7, 100, 112, RowKind.Tall)]
    [InlineData("capture-20260620-hover-tall-rows", 7, 100, 112, RowKind.Tall)]
    [InlineData("capture-20260620-tall-gem-rows", 7, 100, 112, RowKind.Tall)]
    public void Detect_RecordedTallLayouts_DoesNotSplitIconAndTextHalves(
        string fixtureName,
        int expectedRows,
        int minPitch,
        int maxPitch,
        RowKind dominantKind)
    {
        var gray = GrayLoader.Load(FixturePath(fixtureName));

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(expectedRows, detection.Rows.Count);
        Assert.InRange(detection.RowPitch!.Value, minPitch, maxPitch);
        Assert.True(detection.Confidence >= 0.85);
        Assert.True(detection.Rows.Count(row => row.Kind == dominantKind) >= expectedRows - 1);
        Assert.Equal(RowVisibility.PartialTop, detection.Rows[0].Visibility);
        Assert.Equal(RowVisibility.PartialBottom, detection.Rows[^1].Visibility);
    }

    [Theory]
    [InlineData("capture-20260620-standard-currency-rows", 11, 63)]
    [InlineData("capture-20260620-bottom-standard-rows", 11, 63)]
    public void Detect_RecordedStandardLayouts_DoesNotMergeAdjacentRows(
        string fixtureName,
        int expectedRows,
        int expectedPitch)
    {
        var gray = GrayLoader.Load(FixturePath(fixtureName));

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(expectedRows, detection.Rows.Count);
        Assert.InRange(detection.RowPitch!.Value, expectedPitch - 2, expectedPitch + 2);
        Assert.True(detection.Confidence >= 0.90);
        Assert.True(detection.Rows.Count(row => row.Kind == RowKind.Tall) <= 1);
    }

    [Fact]
    public void Detect_RecordedTopAlloyLayout_RetainsReadableTopRows()
    {
        var gray = GrayLoader.Load(FixturePath("capture-20260620-top-alloy-rows"));

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(11, detection.Rows.Count);
        Assert.True(detection.Confidence >= 0.90);
        Assert.Equal(RowVisibility.PartialTop, detection.Rows[0].Visibility);
        Assert.InRange(detection.Rows[0].CenterY, 14, 24);
        Assert.Equal(RowKind.Tall, detection.Rows[1].Kind);
        Assert.InRange(detection.Rows[1].TextCenterY, 108, 124);
    }

    [Theory]
    [InlineData(663, 715)]
    [InlineData(520, 640)]
    [InlineData(900, 980)]
    public void Detect_SyntheticTallLayouts_UsesRewardTextCenterNotVisualBandCenter(int width, int height)
    {
        var specs = new[]
        {
            new RowSpec(48, 134, 116),
            new RowSpec(164, 250, 232),
            new RowSpec(280, 366, 348),
            new RowSpec(396, 482, 464),
        };
        var gray = SyntheticPanel(width, height, specs);

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.True(detection.Rows.Count >= specs.Length);
        foreach (var (row, spec) in detection.Rows.Zip(specs))
        {
            Assert.Equal(RowKind.Tall, row.Kind);
            Assert.InRange(Math.Abs(row.CenterY - spec.BandCenter), 0, 3);
            Assert.InRange(Math.Abs(row.TextCenterY - spec.TextCenterY), 0, 4);
            Assert.True(Math.Abs(row.TextCenterY - row.CenterY) >= 20);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(96)]
    public void Detect_SyntheticLayouts_IsLocalToCaptureRegionAndIgnoresMonitorOffset(int yOffset)
    {
        var specs = new[]
        {
            new RowSpec(42 + yOffset, 96 + yOffset, 56 + yOffset),
            new RowSpec(116 + yOffset, 170 + yOffset, 158 + yOffset),
            new RowSpec(190 + yOffset, 244 + yOffset, 204 + yOffset),
            new RowSpec(264 + yOffset, 318 + yOffset, 306 + yOffset),
        };
        var gray = SyntheticPanel(663, 430 + yOffset, specs);

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        foreach (var (row, spec) in detection.Rows.Zip(specs))
        {
            Assert.InRange(Math.Abs(row.TextCenterY - spec.TextCenterY), 0, 4);
            Assert.InRange(row.TextCenterY, row.Top, row.Bottom);
        }
    }

    [Fact]
    public void Detect_SyntheticPartialRows_MarksPartialVisibility()
    {
        var specs = new[]
        {
            new RowSpec(0, 54, 30),
            new RowSpec(74, 128, 116),
            new RowSpec(148, 202, 162),
            new RowSpec(222, 279, 264),
        };
        var gray = SyntheticPanel(663, 280, specs);

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(RowVisibility.PartialTop, detection.Rows[0].Visibility);
        Assert.Equal(RowVisibility.PartialBottom, detection.Rows[^1].Visibility);
    }

    private static string FixturePath(string fixtureName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Runeshape", fixtureName, "capture_region.png");

    private sealed record RowSpec(int Top, int Bottom, int TextCenterY)
    {
        public int BandCenter => (Top + Bottom) / 2;
    }

    private static byte[,] SyntheticPanel(int width, int height, IReadOnlyList<RowSpec> rows)
    {
        var gray = new byte[height, width];
        Fill(gray, 28);

        foreach (var row in rows)
        {
            FillRect(gray, 0, row.Top, width, row.Bottom - row.Top, 188);
            DrawTextCluster(gray, width, row.TextCenterY);
        }

        return gray;
    }

    private static void Fill(byte[,] gray, byte value)
    {
        int height = gray.GetLength(0);
        int width = gray.GetLength(1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                gray[y, x] = value;
    }

    private static void FillRect(byte[,] gray, int x, int y, int width, int height, byte value)
    {
        int imageHeight = gray.GetLength(0);
        int imageWidth = gray.GetLength(1);
        int left = Math.Clamp(x, 0, imageWidth);
        int top = Math.Clamp(y, 0, imageHeight);
        int right = Math.Clamp(x + width, left, imageWidth);
        int bottom = Math.Clamp(y + height, top, imageHeight);

        for (int yy = top; yy < bottom; yy++)
            for (int xx = left; xx < right; xx++)
                gray[yy, xx] = value;
    }

    private static void DrawTextCluster(byte[,] gray, int width, int centerY)
    {
        int x = Math.Clamp((int)Math.Round(width * 0.56), 0, width - 1);
        int w = Math.Clamp((int)Math.Round(width * 0.16), 48, 116);
        FillRect(gray, x, centerY - 5, w, 3, 26);
        FillRect(gray, x + 4, centerY - 1, w - 10, 3, 18);
        FillRect(gray, x + 12, centerY + 3, w - 24, 3, 34);
    }
}
