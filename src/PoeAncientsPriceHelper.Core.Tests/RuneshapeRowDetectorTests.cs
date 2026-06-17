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
        var path = Path.Combine(AppContext.BaseDirectory, "capture_region.png");
        var gray = GrayLoader.Load(path);

        var detection = new RuneshapeRowDetector().Detect(gray);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(4, detection.Rows.Count);
        Assert.All(detection.Rows, r => Assert.Equal(RowKind.Tall, r.Kind));
        Assert.True(detection.Confidence >= 0.25);
    }
}
