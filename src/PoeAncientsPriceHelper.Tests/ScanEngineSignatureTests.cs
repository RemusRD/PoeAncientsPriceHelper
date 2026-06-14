using System.Drawing;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ScanEngineSignatureTests
{
    [Fact]
    public void PanelSignature_IgnoresIconSideChanges()
    {
        using var bmp = NewPanelBitmap();
        var detection = Detection();
        var before = ScanEngine.PanelSignatureForTests(bmp, detection);

        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.Black))
        {
            g.FillRectangle(brush, 12, 18, 50, 18);
        }

        var after = ScanEngine.PanelSignatureForTests(bmp, detection);

        Assert.Equal(before, after);
    }

    [Fact]
    public void PanelSignature_ChangesWhenRewardTextAreaChanges()
    {
        using var bmp = NewPanelBitmap();
        var detection = Detection();
        var before = ScanEngine.PanelSignatureForTests(bmp, detection);

        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.Black))
        {
            g.FillRectangle(brush, 190, 24, 70, 10);
        }

        var after = ScanEngine.PanelSignatureForTests(bmp, detection);

        Assert.NotEqual(before, after);
    }

    private static Bitmap NewPanelBitmap()
    {
        var bmp = new Bitmap(360, 130);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(170, 145, 100));
        using var line = new Pen(Color.FromArgb(75, 55, 35), 2);
        foreach (var y in new[] { 10, 50, 90, 125 })
            g.DrawLine(line, 0, y, bmp.Width, y);
        return bmp;
    }

    private static RuneshapeRowDetection Detection() => new(
        [10, 50, 90, 125],
        [
            new RuneshapeRow(10, 50, 30, 30),
            new RuneshapeRow(50, 90, 70, 70),
            new RuneshapeRow(90, 125, 108, 108),
        ],
        40,
        0.8);
}
