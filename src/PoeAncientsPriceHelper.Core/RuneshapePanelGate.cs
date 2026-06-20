namespace PoeAncientsPriceHelper.Core;

// Headless "is the Runeshape panel body on screen?" gate — the parchment-colour heuristic that was
// inlined in the Sidecar's LooksLikeRuneshapePanel. Lives in Core so it can be evaluated offline on
// macOS against the capture corpus (the detector already is). Operates on a 24bpp BGR pixel buffer:
// the SAME byte layout System.Drawing's Format24bppRgb produces (buf[i]=B, buf[i+1]=G, buf[i+2]=R),
// so the Sidecar passes its BitmapBuffer.Copy output straight through with zero transformation and
// the score is byte-identical to the original.
public static class RuneshapePanelGate
{
    // Fraction of warm "parchment" samples at/above which the panel body counts as present.
    public const double OpenThreshold = 0.24;

    // Sampled fraction of pixels whose colour matches the panel's parchment palette. Strides the
    // image on a ~64x64 grid (min step 8px) and tests each sample against the warm-colour gate.
    public static double Score(ReadOnlySpan<byte> bgr, int stride, int width, int height)
    {
        var samples = 0;
        var parchment = 0;
        var stepX = Math.Max(8, width / 64);
        var stepY = Math.Max(8, height / 64);

        for (var y = 0; y < height; y += stepY)
        {
            int row = y * stride;
            for (var x = 0; x < width; x += stepX)
            {
                int i = row + x * 3;
                int r = bgr[i + 2];
                int g = bgr[i + 1];
                int b = bgr[i];
                samples++;

                var warm = r >= 85 && r <= 230 &&
                           g >= 75 && g <= 215 &&
                           b >= 45 && b <= 185 &&
                           r >= b + 10 &&
                           Math.Abs(r - g) <= 70;
                var notTooDark = (r + g + b) / 3 >= 70;
                if (warm && notTooDark)
                    parchment++;
            }
        }

        return samples == 0 ? 0 : (double)parchment / samples;
    }

    public static bool LooksOpen(ReadOnlySpan<byte> bgr, int stride, int width, int height, out double score)
    {
        score = Score(bgr, stride, width, height);
        return score >= OpenThreshold;
    }
}
