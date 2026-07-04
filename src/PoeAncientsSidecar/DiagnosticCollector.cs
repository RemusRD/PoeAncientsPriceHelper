using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

internal sealed record DiagnosticBundleResult(string FolderPath, string ZipPath);

internal static class DiagnosticCollector
{
    public static Task<DiagnosticBundleResult> CollectAsync(
        AppConfig config,
        PriceRepository? prices,
        string? captureId = null,
        string? reason = null) =>
        Task.Run(() => Collect(config, prices, captureId, reason));

    private static DiagnosticBundleResult Collect(AppConfig config, PriceRepository? prices, string? captureId, string? reason)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        Directory.CreateDirectory(root);

        var safeBuild = BuildInfo.Display.Replace(' ', '-').Replace('.', '-');
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safeCapture = string.IsNullOrWhiteSpace(captureId) ? "" : "-" + SafeName(captureId);
        var folder = Path.Combine(root, $"{stamp}{safeCapture}-{safeBuild}");
        Directory.CreateDirectory(folder);

        var log = new List<string>();
        void Log(string message) => log.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

        Log($"build={BuildInfo.Display}");
        if (!string.IsNullOrWhiteSpace(captureId)) Log($"captureId={captureId}");
        if (!string.IsNullOrWhiteSpace(reason)) Log($"reason={reason}");
        var version = typeof(DiagnosticCollector).Assembly.GetName().Version;
        Log($"assemblyVersion={version}");
        Log($"baseDir={AppContext.BaseDirectory}");
        Log($"os={Environment.OSVersion}");
        Log($"process64={Environment.Is64BitProcess}");
        Log($"pricesLoaded={prices?.ItemCount ?? 0}");
        if (prices?.LastFetchedAt is { } fetchedAt)
            Log($"priceCacheFetchedAt={fetchedAt:O}");
        if (prices?.LastPoeNinjaSnapshotAt is { } snapshotAt)
            Log($"priceCacheUpstreamSnapshotAt={snapshotAt:O}");
        if (!string.IsNullOrWhiteSpace(prices?.LastFetchError))
            Log($"priceCacheLastError={prices.LastFetchError}");
        if (prices?.LastTypeCounts.Count > 0)
            Log($"priceTypes={string.Join(", ", prices.LastTypeCounts.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        Log($"league={config.LeagueName}");

        CaptureAllScreens(folder, Log);

        Rectangle captureRegion = Rectangle.Empty;
        string captureSource = "none";
        if (PoeWindowLocator.TryGetForegroundClientRect(out var clientRect, out var poeDiagnostic))
        {
            captureRegion = RuneshapeRegionProfiles.Resolve(clientRect);
            captureSource = "auto";
            Log($"poeWindow={poeDiagnostic}");
            Log($"autoCapture={captureRegion}");
        }
        else
        {
            Log($"poeWindow=not found; {poeDiagnostic}");
        }

        if (captureRegion.Width > 0 && captureRegion.Height > 0)
            ProbeCaptureRegion(folder, captureRegion, captureSource, prices, Log);
        else
            Log("captureRegion=none");

        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "app_log.txt"), Path.Combine(folder, "app_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "scan_log.txt"), Path.Combine(folder, "scan_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, ScanProfile.LogFileName), Path.Combine(folder, ScanProfile.LogFileName), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, ScanProfile.LifecycleLogFileName), Path.Combine(folder, ScanProfile.LifecycleLogFileName), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "price_log.txt"), Path.Combine(folder, "price_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "unmatched_log.txt"), Path.Combine(folder, "unmatched_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "feedback_log.txt"), Path.Combine(folder, "feedback_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "overlay_log.txt"), Path.Combine(folder, "overlay_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "crash_log.txt"), Path.Combine(folder, "crash_log.txt"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "debug_ocr.png"), Path.Combine(folder, "latest_debug_ocr.png"), Log);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "debug_ocr_rows.png"), Path.Combine(folder, "latest_debug_ocr_rows.png"), Log);
        CopyLatestCheckProof(folder, Log);

        File.WriteAllLines(Path.Combine(folder, "diagnostics.txt"), log);

        var zipPath = folder + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
        return new DiagnosticBundleResult(folder, zipPath);
    }

    private static void CaptureAllScreens(string folder, Action<string> log)
    {
        var screens = Screen.AllScreens;
        var summaries = new List<object>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            summaries.Add(new
            {
                Index = i,
                screen.DeviceName,
                screen.Primary,
                Bounds = RectInfo(screen.Bounds),
                WorkingArea = RectInfo(screen.WorkingArea),
                screen.BitsPerPixel,
            });

            try
            {
                using var bmp = ScreenCapture.CaptureRegion(screen.Bounds);
                var path = Path.Combine(folder, $"screen_{i}_{SafeName(screen.DeviceName)}.png");
                bmp.Save(path, ImageFormat.Png);
                log($"screen[{i}] saved={path} bounds={screen.Bounds} primary={screen.Primary}");
            }
            catch (Exception ex)
            {
                log($"screen[{i}] ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllText(
            Path.Combine(folder, "screens.json"),
            JsonConvert.SerializeObject(summaries, Formatting.Indented));
    }

    private static void ProbeCaptureRegion(string folder, Rectangle captureRegion, string source, PriceRepository? prices, Action<string> log)
    {
        log($"captureSource={source}");
        using var bmp = ScreenCapture.CaptureRegion(captureRegion);
        bmp.Save(Path.Combine(folder, "capture_region.png"), ImageFormat.Png);

        var rowDetector = new RuneshapeRowDetector();
        var detection = rowDetector.Detect(bmp);
        File.WriteAllText(
            Path.Combine(folder, "row_detection.json"),
            JsonConvert.SerializeObject(new
            {
                Source = source,
                Capture = RectInfo(captureRegion),
                detection.Boundaries,
                detection.RowPitch,
                detection.Confidence,
                detection.HasUsableRows,
                detection.HasRowCandidates,
                Rows = detection.Rows,
            }, Formatting.Indented));

        log($"rows candidates={detection.Rows.Count} usable={detection.HasUsableRows} confidence={detection.Confidence:0.000} pitch={detection.RowPitch}");
        SaveRowProbeImage(bmp, detection, Path.Combine(folder, "row_probe.png"));

        var tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(tessdataDir))
        {
            log($"ocr skipped; tessdata missing at {tessdataDir}");
            return;
        }

        var ocrLog = new List<string>();
        using var scanner = new OcrScanner(tessdataDir, s => ocrLog.Add(s), debug: true, debugOutputDir: folder);
        IReadOnlyList<OcrRow> rows;
        if (detection.ShouldUseRowStrips)
        {
            rows = scanner.ScanRows(bmp, detection.Rows);
            log($"rowStripOcrRows={rows.Count}");
            if (rows.Count == 0)
            {
                log("rowStripOcrRows=0; trying full region OCR");
                rows = scanner.Scan(bmp);
            }
        }
        else
        {
            log(detection.HasRowCandidates
                ? "rowStripOcrRows=skipped; row candidates rejected by gate; trying full region OCR"
                : "rowStripOcrRows=skipped; no row candidates; trying full region OCR");
            rows = scanner.Scan(bmp);
        }

        File.WriteAllText(
            Path.Combine(folder, "ocr_rows.json"),
            JsonConvert.SerializeObject(rows, Formatting.Indented));
        File.WriteAllLines(Path.Combine(folder, "ocr_probe.txt"), ocrLog);
        log($"ocrRows={rows.Count}");
        foreach (var row in rows)
            log($"ocr y={row.CenterY} mult={row.Multiplier} norm='{row.NormalizedName}' raw='{row.RawText}'");

        if (prices?.ItemCount > 0)
        {
            var pricedRows = ScanEngine.BuildPriceRowsForTests(rows, prices.Prices);
            File.WriteAllText(
                Path.Combine(folder, "priced_rows.json"),
                JsonConvert.SerializeObject(pricedRows.Select(row => new
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
                    Label = PriceLabels.BuildLabel(row),
                }), Formatting.Indented));
            log($"pricedRows={pricedRows.Count(row => row.HasPrice)}/{pricedRows.Count}");
            foreach (var row in pricedRows)
                log($"priced y={row.CenterY} hasPrice={row.HasPrice} reason={row.UnpricedReason} name='{row.Name}' label='{PriceLabels.BuildLabel(row)}' raw='{row.OcrText}'");
        }
        else
        {
            log("pricedRows=skipped; prices not loaded");
        }
    }

    private static void SaveRowProbeImage(Bitmap source, RuneshapeRowDetection detection, string path)
    {
        using var annotated = (Bitmap)source.Clone();
        using var g = Graphics.FromImage(annotated);
        using var topBottomPen = new Pen(Color.Lime, 2);
        using var centerPen = new Pen(Color.Cyan, 1);
        using var textPen = new Pen(Color.Magenta, 1);
        using var brush = new SolidBrush(Color.Yellow);
        using var font = new Font("Consolas", 14, FontStyle.Bold);

        g.DrawString(
            $"rows={detection.Rows.Count} usable={detection.HasUsableRows} confidence={detection.Confidence:0.000}",
            font,
            brush,
            8,
            8);

        for (int i = 0; i < detection.Rows.Count; i++)
        {
            var row = detection.Rows[i];
            g.DrawLine(topBottomPen, 0, row.Top, annotated.Width, row.Top);
            g.DrawLine(topBottomPen, 0, row.Bottom, annotated.Width, row.Bottom);
            g.DrawLine(centerPen, 0, row.CenterY, annotated.Width, row.CenterY);
            g.DrawLine(textPen, 0, row.TextCenterY, annotated.Width, row.TextCenterY);
            g.DrawString((i + 1).ToString(), font, brush, 8, row.CenterY - 10);
        }

        annotated.Save(path, ImageFormat.Png);
    }

    private static void CopyIfExists(string source, string destination, Action<string> log)
    {
        try
        {
            if (!File.Exists(source)) return;
            File.Copy(source, destination, overwrite: true);
            log($"copied {source} -> {destination}");
        }
        catch (Exception ex)
        {
            log($"copy failed {source}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CopyLatestCheckProof(string folder, Action<string> log)
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "diagnostics", "checks");
            var latestPath = Path.Combine(root, "latest.txt");
            if (!File.Exists(latestPath))
            {
                log("latest check proof=none");
                return;
            }

            var latest = File.ReadAllText(latestPath).Trim();
            if (!Directory.Exists(latest))
            {
                log($"latest check proof missing: {latest}");
                return;
            }

            var dest = Path.Combine(folder, "latest_check_proof");
            CopyDirectory(latest, dest);
            log($"copied latest check proof {latest} -> {dest}");
        }
        catch (Exception ex)
        {
            log($"copy latest check proof failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static object RectInfo(Rectangle r) => new { r.X, r.Y, r.Width, r.Height, r.Left, r.Top, r.Right, r.Bottom };

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim('_');
    }
}
