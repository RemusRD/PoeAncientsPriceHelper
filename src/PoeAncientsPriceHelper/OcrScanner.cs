using System.Drawing;
using System.Drawing.Imaging;
using Tesseract;

namespace PoeAncientsPriceHelper;

internal sealed record OcrRow(string NormalizedName, string RawText, int CenterY, int Multiplier = 1);

internal sealed class OcrScanner : IDisposable
{
    private readonly TesseractEngine _engineCol;
    private readonly TesseractEngine _engineSparse;
    private readonly Action<string>? _log;
    private readonly bool _debug;
    private readonly string _debugOutputDir;
    private readonly object _logLock = new();
    private const float MinConfidence = 10f;
    private const int UpscaleFactor = 2;
    private const int MinNameLength = 4;
    private const int RowCropPadding = 1;
    private const int TextCropMargin = 28;
    private const double TextSearchStartFraction = 0.33;
    private const double LargeRowTextStartFraction = 0.55;
    private const int TallRowTextBandHeight = 52;
    private const int MinWordLength = 4;

    internal const double IconColumnFraction = 0.30;
    internal const double RightTrimFraction = 0.02;

    public OcrScanner(string tessdataDir, Action<string>? log = null, bool debug = false, string? debugOutputDir = null)
    {
        _engineCol = new TesseractEngine(tessdataDir, "eng", EngineMode.LstmOnly);
        _engineSparse = new TesseractEngine(tessdataDir, "eng", EngineMode.LstmOnly);
        ConfigureEngine(_engineCol);
        ConfigureEngine(_engineSparse);
        _log = log;
        _debug = debug;
        _debugOutputDir = debugOutputDir ?? AppContext.BaseDirectory;
    }

    public IReadOnlyList<OcrRow> Scan(Bitmap regionBitmap)
    {
        int leftCut = Math.Max(1, (int)(regionBitmap.Width * IconColumnFraction));
        int rightCut = (int)(regionBitmap.Width * RightTrimFraction);
        int cropW = Math.Max(1, regionBitmap.Width - leftCut - rightCut);
        using var cropped = CropBitmap(regionBitmap, leftCut, 0, cropW, regionBitmap.Height);
        using var inverted = Preprocess(cropped);
        using var upscaled = Upscale(inverted, UpscaleFactor);
        byte[] png = ToPng(upscaled);
        int height = regionBitmap.Height;

        var tCol = Task.Run(() => RunPass(_engineCol, png, PageSegMode.SingleColumn, height));
        var tSparse = Task.Run(() => RunPass(_engineSparse, png, PageSegMode.SparseText, height));
        Task.WaitAll(tCol, tSparse);
        var rows = MergeByPosition(tCol.Result, tSparse.Result);

        if (_debug && rows.Count <= 2)
        {
            try { upscaled.Save(Path.Combine(_debugOutputDir, "debug_ocr.png"), System.Drawing.Imaging.ImageFormat.Png); }
            catch { }
        }
        return rows;
    }

    public IReadOnlyList<OcrRow> ScanRows(Bitmap regionBitmap, IReadOnlyList<RuneshapeRow> rows)
    {
        if (rows.Count == 0) return [];

        var result = new List<OcrRow>(rows.Count);
        List<Bitmap>? debugStrips = _debug ? new List<Bitmap>(rows.Count) : null;

        try
        {
            foreach (var row in rows)
            {
                var ocrRow = ScanSingleRow(regionBitmap, row, debugStrips);
                if (ocrRow is not null)
                    result.Add(ocrRow);
            }

            if (_debug && result.Count < rows.Count && debugStrips is { Count: > 0 })
            {
                try { SaveContactSheet(debugStrips, Path.Combine(_debugOutputDir, "debug_ocr_rows.png")); }
                catch { }
            }
        }
        finally
        {
            if (debugStrips is not null)
                foreach (var strip in debugStrips)
                    strip.Dispose();
        }

        result.Sort((x, y) => x.CenterY.CompareTo(y.CenterY));
        return result;
    }

    internal OcrRow? ScanSingleRow(Bitmap regionBitmap, RuneshapeRow row, List<Bitmap>? debugStrips = null)
    {
        if (row.Visibility != RowVisibility.Full)
            return null;

        int leftCut = Math.Max(1, (int)(regionBitmap.Width * IconColumnFraction));
        int rightCut = (int)(regionBitmap.Width * RightTrimFraction);

        int top = Math.Clamp(row.Top + RowCropPadding, 0, regionBitmap.Height - 1);
        int bottom = Math.Clamp(row.Bottom - RowCropPadding, top + 1, regionBitmap.Height);
        int cropH = bottom - top;
        if (cropH < 8) return null;

        if (row.Kind == RowKind.Tall)
            return ScanTallRow(regionBitmap, row, top, bottom, leftCut, rightCut, debugStrips);

        return ScanStandardRow(regionBitmap, row, top, bottom, leftCut, rightCut, debugStrips);
    }

    private OcrRow? ScanStandardRow(
        Bitmap regionBitmap, RuneshapeRow row,
        int top, int bottom, int leftCut, int rightCut,
        List<Bitmap>? debugStrips)
    {
        int cropW = Math.Max(1, regionBitmap.Width - leftCut - rightCut);
        int cropH = bottom - top;

        using var rowStrip = CropBitmap(regionBitmap, leftCut, top, cropW, cropH);
        using var cropped = CropTextCluster(rowStrip, leftCut, regionBitmap.Width);
        using var processed = Preprocess(cropped);
        using var upscaled = Upscale(processed, UpscaleFactor);
        debugStrips?.Add((Bitmap)upscaled.Clone());
        byte[] png = ToPng(upscaled);

        RunSingleLine(_engineCol, png, row.CenterY, out var ocrRow);
        return ocrRow;
    }

    private OcrRow? ScanTallRow(
        Bitmap regionBitmap, RuneshapeRow row,
        int top, int bottom, int leftCut, int rightCut,
        List<Bitmap>? debugStrips)
    {
        int bandH = Math.Min(bottom - top, TallRowTextBandHeight);
        int bandTop = Math.Clamp(row.TextCenterY - bandH / 2, top, bottom - bandH);
        int cropW = Math.Max(1, regionBitmap.Width - leftCut - rightCut);

        using var bandStrip = CropBitmap(regionBitmap, leftCut, bandTop, cropW, bandH);
        using var textCluster = CropTextCluster(bandStrip, leftCut, regionBitmap.Width, LargeRowTextStartFraction);
        using var processed = Preprocess(textCluster);
        using var upscaled = Upscale(processed, UpscaleFactor);
        debugStrips?.Add((Bitmap)upscaled.Clone());
        byte[] png = ToPng(upscaled);

        RunSingleLine(_engineCol, png, row.CenterY, out var ocrRow);
        return ocrRow;
    }

    private static void ConfigureEngine(TesseractEngine engine)
    {
        engine.SetVariable("user_defined_dpi", "300");
        engine.SetVariable("preserve_interword_spaces", "1");
        engine.SetVariable("tessedit_char_whitelist", "0123456789xXabcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ ");
    }

    private IReadOnlyList<OcrRow> RunPass(TesseractEngine engine, byte[] png, PageSegMode mode, int regionHeight)
    {
        using var pix = Pix.LoadFromMemory(png);
        using var page = engine.Process(pix, mode);
        return ExtractRows(page, regionHeight, UpscaleFactor);
    }

    private bool RunSingleLine(TesseractEngine engine, byte[] png, int displayCenterY, out OcrRow row)
    {
        row = null!;
        using var pix = Pix.LoadFromMemory(png);
        using var page = engine.Process(pix, PageSegMode.SingleLine);
        var text = page.GetText();
        float conf = page.GetMeanConfidence() * 100f;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (conf < MinConfidence)
            return false;

        var normalizedRaw = NormalizeName(text);
        int multiplier = ExtractMultiplier(normalizedRaw);
        var normalized = StripLeadingNoise(normalizedRaw);
        if (normalized.Length < MinNameLength || !HasLongWord(normalized, MinWordLength))
            return false;

        row = new OcrRow(normalized, text.Trim(), displayCenterY, multiplier);
        return true;
    }

    private static IReadOnlyList<OcrRow> MergeByPosition(IReadOnlyList<OcrRow> a, IReadOnlyList<OcrRow> b)
    {
        const int Tol = 25;
        static int Letters(string s) { int c = 0; foreach (var ch in s) if (char.IsLetter(ch)) c++; return c; }

        var result = new List<OcrRow>(a);
        foreach (var rb in b)
        {
            int idx = -1;
            for (int i = 0; i < result.Count; i++)
                if (Math.Abs(result[i].CenterY - rb.CenterY) <= Tol) { idx = i; break; }
            if (idx < 0) result.Add(rb);
            else if (Letters(rb.NormalizedName) > Letters(result[idx].NormalizedName)) result[idx] = rb;
        }
        result.Sort((x, y) => x.CenterY.CompareTo(y.CenterY));
        return result;
    }

    private static Bitmap CropBitmap(Bitmap src, int x, int y, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
        return dst;
    }

    private static Bitmap CropTextCluster(Bitmap rowStrip, int stripSourceX, int sourceWidth)
    {
        return CropTextCluster(rowStrip, stripSourceX, sourceWidth, TextSearchStartFraction);
    }

    private static Bitmap CropTextCluster(Bitmap rowStrip, int stripSourceX, int sourceWidth, double searchStartFraction)
    {
        if (!TryFindTextCluster(rowStrip, stripSourceX, sourceWidth, searchStartFraction, out var left, out var right))
            return (Bitmap)rowStrip.Clone();

        left = Math.Clamp(left - TextCropMargin, 0, rowStrip.Width - 1);
        right = Math.Clamp(right + TextCropMargin, left + 1, rowStrip.Width);
        return CropBitmap(rowStrip, left, 0, right - left, rowStrip.Height);
    }

    private static bool TryFindTextCluster(Bitmap rowStrip, int stripSourceX, int sourceWidth, double searchStartFraction, out int left, out int right)
    {
        left = 0;
        right = rowStrip.Width;
        if (rowStrip.Width < 80 || rowStrip.Height < 16)
            return false;

        int searchLeft = Math.Clamp((int)Math.Round(sourceWidth * searchStartFraction) - stripSourceX, 0, rowStrip.Width - 1);
        int searchRight = Math.Max(searchLeft + 1, rowStrip.Width - Math.Max(3, (int)Math.Round(sourceWidth * RightTrimFraction)));
        int yTop = Math.Clamp((int)Math.Round(rowStrip.Height * 0.18), 0, rowStrip.Height - 1);
        int yBottom = Math.Clamp((int)Math.Round(rowStrip.Height * 0.86), yTop + 1, rowStrip.Height);
        int minDarkPixels = Math.Max(4, (int)Math.Round((yBottom - yTop) * 0.12));

        var stripBuf = BitmapBuffer.Copy(rowStrip, out var stripStride);
        var darkColumns = new bool[rowStrip.Width];
        for (int x = searchLeft; x < searchRight; x++)
        {
            int dark = 0;
            for (int y = yTop; y < yBottom; y++)
            {
                int i = y * stripStride + x * 3;
                int gray = (stripBuf[i + 2] * 30 + stripBuf[i + 1] * 59 + stripBuf[i] * 11) / 100;
                if (gray < 95 && ++dark >= minDarkPixels)
                    break;
            }
            darkColumns[x] = dark >= minDarkPixels;
        }

        int first = -1;
        int last = -1;
        for (int x = searchLeft; x < searchRight; x++)
        {
            if (!HasDarkNeighbor(darkColumns, x))
                continue;

            first = first < 0 ? x : first;
            last = x;
        }

        if (first < 0 || last - first < 18)
            return false;

        left = first;
        right = last + 1;
        return true;
    }

    private static bool HasDarkNeighbor(bool[] darkColumns, int x)
    {
        if (!darkColumns[x])
            return false;

        int from = Math.Max(0, x - 2);
        int to = Math.Min(darkColumns.Length - 1, x + 2);
        int count = 0;
        for (int i = from; i <= to; i++)
            if (darkColumns[i]) count++;
        return count >= 2;
    }

    private static void SaveContactSheet(IReadOnlyList<Bitmap> strips, string path)
    {
        int width = strips.Max(s => s.Width);
        int height = strips.Sum(s => s.Height + 8);
        using var sheet = new Bitmap(width, Math.Max(1, height), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.White);
        int y = 0;
        foreach (var strip in strips)
        {
            g.DrawImage(strip, 0, y);
            y += strip.Height + 8;
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private IReadOnlyList<OcrRow> ExtractRows(Page page, int bitmapHeight, int scale = 1)
    {
        var rows = new List<OcrRow>();
        var diag = new List<string>();
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
            var text = iter.GetText(PageIteratorLevel.TextLine);
            float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
            int centerY = Math.Clamp((box.Y1 + (box.Y2 - box.Y1) / 2) / scale, 0, bitmapHeight - 1);

            string? reject = null;
            string normalized = "";
            int multiplier = 1;
            if (string.IsNullOrWhiteSpace(text)) reject = "empty";
            else if (conf < MinConfidence) reject = "lowconf";
            else
            {
                var normalizedRaw = NormalizeName(text);
                multiplier = ExtractMultiplier(normalizedRaw);
                normalized = StripLeadingNoise(normalizedRaw);
                if (normalized.Length < MinNameLength) reject = "short";
                else if (!HasLongWord(normalized, MinWordLength)) reject = "noword";
            }

            if (reject is null)
                rows.Add(new OcrRow(normalized, text.Trim(), centerY, multiplier));
            diag.Add($"y={centerY} conf={conf:0} '{(text ?? "").Trim()}'{(reject is null ? "" : $" REJ:{reject}")}");
        }
        while (iter.Next(PageIteratorLevel.TextLine));

        if (rows.Count <= 2 && diag.Count > 0)
            lock (_logLock) { _log?.Invoke($"OCR raw {diag.Count} lines → " + string.Join(" | ", diag)); }

        return rows;
    }

    private static Bitmap Upscale(Bitmap src, int factor)
    {
        var dst = new Bitmap(src.Width * factor, src.Height * factor, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    internal static int ExtractMultiplier(string normalized)
    {
        var matches = Regex.Matches(normalized, @"(?<!\d)(\d{1,3})\s*x(?=\s*[a-z])");
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            if (int.TryParse(m.Groups[1].Value, out var n) && n >= 1)
                return Math.Min(n, 999);
        }
        return 1;
    }

    internal static string StripLeadingNoise(string normalized)
    {
        var s = normalized;
        var qm = Regex.Match(s, @"(?<!\d)\d{1,3}\s*x\s*");
        if (qm.Success) s = s.Substring(qm.Index + qm.Length);
        if (Regex.IsMatch(s, @"^(?:my|sm)\s+(?:chaos|exalted|regal)\s+(?:mo|orb)\b"))
            return s.Trim();
        s = Regex.Replace(s, @"^(?:\S{1,2}\s+|[a-z]{1,3}x\s+|\S*\d\S*\s+)+", "");
        s = Regex.Replace(s, @"^x\s*(?=[a-z])", "");
        s = Regex.Replace(s, @"^[^a-z]+", "");
        return s.Trim();
    }

    private static bool HasLongWord(string normalized, int minLen)
    {
        int run = 0;
        foreach (char c in normalized)
        {
            if (char.IsLetter(c)) { if (++run >= minLen) return true; }
            else run = 0;
        }
        return false;
    }

    private static Bitmap Preprocess(Bitmap src)
    {
        var srcBuf = BitmapBuffer.Copy(src, out var srcStride);
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        var data = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * dst.Height;
            var buf = new byte[len];
            int min = 255;
            int max = 0;
            for (int y = 0; y < src.Height; y++)
            {
                int srow = y * srcStride;
                for (int x = 0; x < src.Width; x++)
                {
                    int si = srow + x * 3;
                    int gray = (srcBuf[si + 2] * 30 + srcBuf[si + 1] * 59 + srcBuf[si] * 11) / 100;
                    if (gray < min) min = gray;
                    if (gray > max) max = gray;
                }
            }

            int range = Math.Max(1, max - min);
            for (int y = 0; y < dst.Height; y++)
            {
                int srow = y * srcStride;
                int drow = y * data.Stride;
                for (int x = 0; x < dst.Width; x++)
                {
                    int si = srow + x * 3;
                    int di = drow + x * 3;
                    int gray = (srcBuf[si + 2] * 30 + srcBuf[si + 1] * 59 + srcBuf[si] * 11) / 100;
                    int stretched = Math.Clamp((gray - min) * 255 / range, 0, 255);
                    buf[di] = buf[di + 1] = buf[di + 2] = (byte)stretched;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { dst.UnlockBits(data); }
        return dst;
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    internal static string NormalizeName(string text)
    {
        var s = text.ToLowerInvariant();
        s = s.Replace("ﬁ", "fi").Replace("ﬂ", "fl").Replace('_', ' ');
        s = Regex.Replace(s, @"[^\w\s]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    public void Dispose() { _engineCol.Dispose(); _engineSparse.Dispose(); }
}
