using System.Drawing;
using System.Drawing.Imaging;

namespace PoeAncientsPriceHelper;

internal static class ScreenCapture
{
    public static Bitmap CaptureRegion(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static bool IsAllBlack(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            int len = stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);
            int stepX = Math.Max(1, bmp.Width / 16);
            int stepY = Math.Max(1, bmp.Height / 8);
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    int i = row + x * 3;
                    if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0) return false;
                }
            }
            return true;
        }
        finally { bmp.UnlockBits(data); }
    }
}
