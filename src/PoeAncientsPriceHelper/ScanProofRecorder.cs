using System.Drawing;
using System.Drawing.Imaging;
using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

internal static class ScanProofRecorder
{
    private const int KeepLatest = 40;

    public static void SaveCheck(
        Bitmap capture,
        RuneshapeRowDetection detection,
        IReadOnlyList<OcrRow> ocrRows,
        IReadOnlyList<PriceRow> displayRows,
        Rectangle captureRegion,
        string source,
        string ocrSource,
        int priceCount,
        string captureDiagnostic)
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "diagnostics", "checks");
            Directory.CreateDirectory(root);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var folder = Path.Combine(root, $"{stamp}-{SafeName(source)}");
            Directory.CreateDirectory(folder);

            capture.Save(Path.Combine(folder, "capture_region.png"), ImageFormat.Png);
            SaveRowProbeImage(capture, detection, Path.Combine(folder, "row_probe.png"));

            File.WriteAllText(
                Path.Combine(folder, "summary.json"),
                JsonConvert.SerializeObject(new
                {
                    Build = BuildInfo.Display,
                    SavedAt = DateTime.Now.ToString("O"),
                    Source = source,
                    OcrSource = ocrSource,
                    PriceCount = priceCount,
                    Capture = RectInfo(captureRegion),
                    Screen = System.Windows.Forms.Screen.FromRectangle(captureRegion).DeviceName,
                    detection.Boundaries,
                    detection.RowPitch,
                    detection.Confidence,
                    detection.HasUsableRows,
                    detection.HasRowCandidates,
                    RowCandidates = detection.Rows.Count,
                    OcrRows = ocrRows.Count,
                    DisplayRows = displayRows.Count,
                    PricedRows = displayRows.Count(row => row.HasPrice),
                    CaptureDiagnostic = captureDiagnostic,
                }, Formatting.Indented));

            File.WriteAllText(
                Path.Combine(folder, "row_detection.json"),
                JsonConvert.SerializeObject(new
                {
                    Capture = RectInfo(captureRegion),
                    detection.Boundaries,
                    detection.RowPitch,
                    detection.Confidence,
                    detection.HasUsableRows,
                    detection.HasRowCandidates,
                    Rows = detection.Rows,
                }, Formatting.Indented));

            File.WriteAllText(
                Path.Combine(folder, "ocr_rows.json"),
                JsonConvert.SerializeObject(ocrRows, Formatting.Indented));

            File.WriteAllText(
                Path.Combine(folder, "priced_rows.json"),
                JsonConvert.SerializeObject(displayRows.Select(row => new
                {
                    row.CenterY,
                    row.Name,
                    row.OcrText,
                    row.Multiplier,
                    row.HasPrice,
                    row.DivineValue,
                    row.ExaltedValue,
                    row.ExactMatch,
                    row.Meme,
                    row.UnpricedReason,
                    Label = PriceOverlayWindow.BuildLabel(row),
                }), Formatting.Indented));

            File.WriteAllText(Path.Combine(root, "latest.txt"), folder);
            Prune(root);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "feedback_log.txt"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] proof-save failed {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }
    }

    private static void SaveRowProbeImage(Bitmap source, RuneshapeRowDetection detection, string path)
    {
        using var annotated = (Bitmap)source.Clone();
        using var g = Graphics.FromImage(annotated);
        using var boundaryPen = new Pen(Color.Lime, 2);
        using var centerPen = new Pen(Color.Cyan, 1);
        using var textPen = new Pen(Color.Magenta, 1);
        using var brush = new SolidBrush(Color.Yellow);
        using var font = new Font("Consolas", 14, FontStyle.Bold);

        g.DrawString(
            $"rows={detection.Rows.Count} confidence={detection.Confidence:0.000}",
            font,
            brush,
            8,
            8);

        for (int i = 0; i < detection.Rows.Count; i++)
        {
            var row = detection.Rows[i];
            g.DrawLine(boundaryPen, 0, row.Top, annotated.Width, row.Top);
            g.DrawLine(boundaryPen, 0, row.Bottom, annotated.Width, row.Bottom);
            g.DrawLine(centerPen, 0, row.CenterY, annotated.Width, row.CenterY);
            g.DrawLine(textPen, 0, row.TextCenterY, annotated.Width, row.TextCenterY);
            g.DrawString((i + 1).ToString(), font, brush, 8, Math.Max(0, row.CenterY - 10));
        }

        annotated.Save(path, ImageFormat.Png);
    }

    private static void Prune(string root)
    {
        var dirs = Directory.GetDirectories(root)
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .Skip(KeepLatest);
        foreach (var dir in dirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    private static object RectInfo(Rectangle r) => new { r.X, r.Y, r.Width, r.Height, r.Left, r.Top, r.Right, r.Bottom };

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim('_').Replace(' ', '-');
    }
}
