using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

    [Fact]
    public void PanelSignature_IgnoresUniformRowBrightnessShift()
    {
        // A hover highlight raises a whole row's brightness by roughly a constant. Mean-normalization
        // makes the signature invariant to this uniform shift, so hovering no longer flips the
        // signature — which previously blanked the overlay (flicker) and evicted slots (dropping a
        // duplicate's price). Adding delta to R,G,B adds exactly delta*1000/1000 = delta to luminance
        // (the weights sum to 1000), so with delta divisible by 8 every quantized sample rises by the
        // same amount and (sample - mean) is preserved exactly.
        using var bmp = NewPanelBitmap();
        var detection = Detection();
        var before = ScanEngine.PanelSignatureForTests(bmp, detection);

        AddConstantBrightness(bmp, y0: 50, y1: 90, delta: 24);

        var after = ScanEngine.PanelSignatureForTests(bmp, detection);

        Assert.Equal(before, after);
    }

    private static void AddConstantBrightness(Bitmap bmp, int y0, int y1, int delta)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * data.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = y0; y < y1 && y < bmp.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = row + x * 3;
                    buf[b] = (byte)Math.Min(255, buf[b] + delta);
                    buf[b + 1] = (byte)Math.Min(255, buf[b + 1] + delta);
                    buf[b + 2] = (byte)Math.Min(255, buf[b + 2] + delta);
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
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

public class ScanEngineMeaningfulChangeTests
{
    [Fact]
    public void SingleRowChange_IsNotMeaningful_TooltipOverlapIgnored()
    {
        // 4-row panel; flipping exactly one row's hash (a tooltip overlapping one name) is not a
        // real content change and must not trigger a re-scan / flicker.
        var prev = Sig(4);
        var curr = Sig(4, changedRowIndex: 1);

        Assert.False(ScanEngine.MeaningfullyChangedForTests(prev, curr));
    }

    [Fact]
    public void TwoRowChanges_AreMeaningful_RealSwitchDetected()
    {
        var prev = Sig(4);
        var curr = Sig(4, changedRowIndex: 0, changedRowIndex2: 2);

        Assert.True(ScanEngine.MeaningfullyChangedForTests(prev, curr));
    }

    [Fact]
    public void AllRowsChanged_IsMeaningful()
    {
        var prev = Sig(6);
        var curr = Sig(6, allDifferent: true);

        Assert.True(ScanEngine.MeaningfullyChangedForTests(prev, curr));
    }

    [Fact]
    public void IdenticalSignature_IsNotMeaningful()
    {
        var sig = Sig(5);

        Assert.False(ScanEngine.MeaningfullyChangedForTests(sig, sig));
    }

    [Fact]
    public void NullPrevious_IsMeaningful_FirstCycle()
    {
        Assert.True(ScanEngine.MeaningfullyChangedForTests(null, Sig(4)));
    }

    [Fact]
    public void DifferentRowCount_IsMeaningful()
    {
        var prev = Sig(4);
        var curr = Sig(5);

        Assert.True(ScanEngine.MeaningfullyChangedForTests(prev, curr));
    }

    [Fact]
    public void SingleRowPanel_AnyChangeIsMeaningful()
    {
        var prev = Sig(1);
        var curr = Sig(1, changedRowIndex: 0);

        Assert.True(ScanEngine.MeaningfullyChangedForTests(prev, curr));
    }

    // Builds a fake "rows:NN:<seg>:<seg>..." signature. Segments are distinct per row+variant so
    // changing a row produces a different segment without touching the format.
    private static string Sig(int rowCount, int changedRowIndex = -1, int changedRowIndex2 = -1, bool allDifferent = false)
    {
        var parts = new List<string> { "rows", rowCount.ToString("X2") };
        for (int i = 0; i < rowCount; i++)
        {
            var variant = (allDifferent ? 1 : 0) + ((i == changedRowIndex || i == changedRowIndex2) ? 1 : 0);
            parts.Add($"{i + 10:X4}{40:X3}{0xABCDEF0123456789UL + (ulong)i + (ulong)variant:X16}");
        }
        return string.Join(":", parts);
    }
}
