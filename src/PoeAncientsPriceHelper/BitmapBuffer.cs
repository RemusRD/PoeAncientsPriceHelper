using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PoeAncientsPriceHelper;

// Reads a Format24bppRgb bitmap into a managed buffer in one LockBits pass so hot loops can index
// pixels directly, avoiding the per-call managed↔native GDI+ boundary crossing of Bitmap.GetPixel
// (~10-50x slower). Layout is BGR: buf[y*stride + x*3] = B, +1 = G, +2 = R.
internal static class BitmapBuffer
{
    public static byte[] Copy(Bitmap bmp, out int stride)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            stride = Math.Abs(data.Stride);
            var buf = new byte[stride * data.Height];
            for (var y = 0; y < data.Height; y++)
            {
                var source = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(source, buf, y * stride, stride);
            }
            return buf;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
