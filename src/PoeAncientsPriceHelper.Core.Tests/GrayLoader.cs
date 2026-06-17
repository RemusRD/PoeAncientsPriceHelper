using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoeAncientsPriceHelper.Core.Tests;

// Loads a PNG into a byte[,] grayscale buffer using the SAME luminance weights as the original
// System.Drawing ToGray (B*11 + G*59 + R*30)/100, so the detector sees identical input on macOS.
internal static class GrayLoader
{
    public static byte[,] Load(string path)
    {
        using var image = Image.Load<Rgb24>(path);
        var gray = new byte[image.Height, image.Width];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    var p = row[x];
                    gray[y, x] = (byte)((p.R * 30 + p.G * 59 + p.B * 11) / 100);
                }
            }
        });
        return gray;
    }
}
