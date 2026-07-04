using System.Drawing;

namespace PoeAncientsPriceHelper;

internal sealed class ListDetector
{
    private const int BrightnessThreshold = 100;
    private const int Cols = 12;
    // Sample the RIGHT portion only — the left ~25% icon column has dark gaps that pull the
    // average down on an open panel. Skipping it raises panel readings well above the dark
    // game world, widening the gap against bright-but-patchy outdoor backgrounds.
    private const double LeftFraction = 0.40;
    private const double RightFraction = 0.98;
    private static readonly double[] RowFractions = [0.20, 0.35, 0.50, 0.65, 0.80];

    public bool IsOpen(Bitmap regionBitmap) => IsOpen(regionBitmap, out _);

    // Averages a grid of pixels across the right portion of the region at several heights.
    // Panel UI is consistently brighter than the dark PoE2 game-world background.
    public bool IsOpen(Bitmap regionBitmap, out Color sampledAvg)
    {
        int x0 = (int)(regionBitmap.Width * LeftFraction);
        int x1 = (int)(regionBitmap.Width * RightFraction);
        int span = Math.Max(1, x1 - x0);

        long r = 0, g = 0, b = 0;
        int count = 0;
        foreach (var yf in RowFractions)
        {
            int cy = Math.Clamp((int)(regionBitmap.Height * yf), 0, regionBitmap.Height - 1);
            for (int i = 0; i < Cols; i++)
            {
                int cx = Math.Clamp(x0 + (int)((i + 0.5) * span / Cols), 0, regionBitmap.Width - 1);
                var px = regionBitmap.GetPixel(cx, cy);
                r += px.R; g += px.G; b += px.B; count++;
            }
        }

        sampledAvg = Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
        return (sampledAvg.R + sampledAvg.G + sampledAvg.B) / 3 > BrightnessThreshold;
    }
}
