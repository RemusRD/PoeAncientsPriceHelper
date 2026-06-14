using System.Drawing;
using System.Drawing.Imaging;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class RuneshapeRowDetectorTests
{
    [Fact]
    public void Detect_FindsRegularRuneshapeRows()
    {
        using var bmp = SyntheticPanel([40, 80, 120, 160, 200, 240], textCenterOffset: 20);

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(5, detection.Rows.Count);
        Assert.InRange(detection.RowPitch!.Value, 36, 44);
        Assert.All(detection.Rows.Select(r => r.CenterY).Zip(new[] { 60, 100, 140, 180, 220 }), pair =>
            Assert.InRange(pair.First, pair.Second - 4, pair.Second + 4));
    }

    [Fact]
    public void Detect_RefinesTextCenterInsideRow()
    {
        using var bmp = SyntheticPanel([40, 80, 120, 160, 200], textCenterOffset: 15);

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.True(detection.HasUsableRows);
        Assert.All(detection.Rows.Select(r => r.TextCenterY).Zip(new[] { 55, 95, 135, 175 }), pair =>
            Assert.InRange(pair.First, pair.Second - 6, pair.Second + 6));
    }

    [Fact]
    public void Detect_PrefersRewardBands_WhenTextEdgesOutnumberRows()
    {
        using var bmp = BandedPanel(rowCount: 6, rowHeight: 51, rowPitch: 63, height: 715);

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.True(detection.HasUsableRows);
        Assert.True(detection.ShouldUseRowStrips);
        Assert.Equal(6, detection.Rows.Count);
        Assert.InRange(detection.RowPitch!.Value, 60, 66);
        Assert.All(detection.Rows.Select(r => r.Bottom - r.Top), height =>
            Assert.InRange(height, 48, 56));
        Assert.All(detection.Rows.Select(r => r.CenterY).Zip(new[] { 25, 88, 151, 214, 277, 340 }), pair =>
            Assert.InRange(pair.First, pair.Second - 4, pair.Second + 4));
    }

    [Fact]
    public void Detect_AddsTopRow_WhenFirstBoundaryIsCroppedAtCaptureTop()
    {
        using var bmp = SyntheticPanel([51, 114, 177, 240, 303, 366], textCenterOffset: 28, height: 390);

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.True(detection.HasUsableRows);
        Assert.Equal(6, detection.Rows.Count);
        Assert.InRange(detection.Rows[0].Top, 0, 2);
        Assert.InRange(detection.Rows[0].CenterY, 23, 28);
    }

    [Fact]
    public void Detect_DoesNotInventRowsOnFlatImage()
    {
        using var bmp = new Bitmap(420, 260, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.FromArgb(25, 25, 25));

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.False(detection.HasUsableRows);
    }

    [Fact]
    public void Detect_ReturnsCandidates_ForSingleVisibleRow()
    {
        using var bmp = SyntheticPanel([80, 160], textCenterOffset: 40);

        var detection = new RuneshapeRowDetector().Detect(bmp);

        Assert.False(detection.HasUsableRows);
        Assert.True(detection.HasRowCandidates);
        var row = Assert.Single(detection.Rows);
        Assert.InRange(row.CenterY, 116, 124);
    }

    [Fact]
    public void Detect_DoesNotThrow_WhenNoisyBoundariesOutnumberRows()
    {
        using var bmp = SyntheticPanel([20, 50, 185, 215, 245], textCenterOffset: 15);

        var exception = Record.Exception(() => new RuneshapeRowDetector().Detect(bmp));

        Assert.Null(exception);
    }

    private static Bitmap SyntheticPanel(int[] boundaries, int textCenterOffset, int height = 280)
    {
        var bmp = new Bitmap(420, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(178, 168, 145));

        using var border = new Pen(Color.FromArgb(55, 45, 35), 2);
        foreach (int y in boundaries)
            g.DrawLine(border, 10, y, 400, y);

        using var text = new SolidBrush(Color.FromArgb(30, 25, 20));
        for (int i = 0; i < boundaries.Length - 1; i++)
        {
            int y = boundaries[i] + textCenterOffset;
            g.FillRectangle(text, 210, y - 4, 95, 8);
            g.FillRectangle(text, 315, y - 4, 45, 8);
        }

        return bmp;
    }

    private static Bitmap BandedPanel(int rowCount, int rowHeight, int rowPitch, int height)
    {
        var bmp = new Bitmap(663, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(96, 91, 76));

        using var rowBrush = new SolidBrush(Color.FromArgb(187, 176, 151));
        using var separator = new Pen(Color.FromArgb(57, 45, 34), 3);
        using var text = new SolidBrush(Color.FromArgb(30, 25, 20));
        using var glyph = new Pen(Color.FromArgb(28, 24, 20), 2);

        for (int i = 0; i < rowCount; i++)
        {
            int top = i * rowPitch;
            g.FillRectangle(rowBrush, 0, top, bmp.Width - 8, rowHeight);
            g.DrawLine(separator, 0, top + rowHeight, bmp.Width - 8, top + rowHeight);

            for (int icon = 0; icon < 3; icon++)
            {
                int x = 8 + icon * 54;
                g.DrawRectangle(separator, x - 2, top + 5, 44, 40);
                g.DrawLine(glyph, x + 6, top + 12, x + 34, top + 36);
                g.DrawLine(glyph, x + 34, top + 12, x + 6, top + 36);
            }

            int textY = top + 21;
            g.FillRectangle(text, 285, textY - 4, 18, 8);
            g.FillRectangle(text, 315, textY - 4, 292, 8);
        }

        return bmp;
    }
}
