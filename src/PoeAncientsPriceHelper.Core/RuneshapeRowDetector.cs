namespace PoeAncientsPriceHelper.Core;

public enum RowKind { Standard, Tall }
public enum RowVisibility { Full, PartialTop, PartialBottom }

public sealed record RuneshapeRow(
    int Top, int Bottom, int CenterY, int TextCenterY,
    RowKind Kind = RowKind.Standard,
    RowVisibility Visibility = RowVisibility.Full);

public sealed record RuneshapeRowDetection(
    IReadOnlyList<int> Boundaries,
    IReadOnlyList<RuneshapeRow> Rows,
    int? RowPitch,
    double Confidence)
{
    public bool HasUsableRows => Rows.Count >= 3 &&
                                 Confidence >= 0.25 &&
                                 Rows.All(row => row.Bottom - row.Top >= 34);
    public bool HasRowCandidates => Rows.Count > 0;
    public bool ShouldUseRowStrips => HasUsableRows || Rows.Count is > 0 and <= 2;
}

// Ported verbatim from the WPF app's detector. The only change vs. the original is the entry
// point: Detect(byte[,] gray) instead of Detect(Bitmap), with ToGray() moved to the caller
// (System.Drawing on Windows, ImageSharp on Mac). Every threshold, fraction, and heuristic is
// preserved so detection is byte-for-byte identical to the shipped engine.
public sealed class RuneshapeRowDetector
{
    public RuneshapeRowDetection Detect(byte[,] gray)
    {
        int width = gray.GetLength(1);
        int height = gray.GetLength(0);
        if (width < 80 || height < 80)
            return Empty();

        var separators = DetectSeparatorRows(gray);
        if (separators.HasUsableRows)
            return RefineRows(gray, separators);

        var bands = DetectBrightBands(gray);
        if (bands.HasUsableRows)
            return RefineRows(gray, bands);

        var edgePeaks = FindPeaks(EdgeScore(gray), height);
        var edge = InferRows(edgePeaks, gray);

        var darkPeaks = FindPeaks(DarkScore(gray), height);
        var dark = InferRows(darkPeaks, gray);

        var chosen = dark.Confidence > edge.Confidence + 0.10 ? dark : edge;

        if (chosen.Rows.Count == 0 && bands.Rows.Count > 0)
            return RefineRows(gray, bands);

        if (chosen.Rows.Count == 0)
            return chosen;

        return RefineRows(gray, chosen);
    }

    private static RuneshapeRowDetection RefineRows(byte[,] gray, RuneshapeRowDetection detection)
    {
        int imageHeight = gray.GetLength(0);
        var rows = new List<RuneshapeRow>(detection.Rows.Count);
        foreach (var row in detection.Rows)
        {
            int height = row.Bottom - row.Top;
            var kind = height > 65 ? RowKind.Tall : RowKind.Standard;
            var visibility = row.Top < 5 ? RowVisibility.PartialTop
                           : row.Bottom > imageHeight - 5 ? RowVisibility.PartialBottom
                           : RowVisibility.Full;
            rows.Add(row with
            {
                TextCenterY = RefineTextCenter(gray, row.Top, row.Bottom, row.CenterY),
                Kind = kind,
                Visibility = visibility
            });
        }

        return detection with { Rows = rows };
    }

    private static RuneshapeRowDetection Empty() => new([], [], null, 0);

    private static RuneshapeRowDetection DetectSeparatorRows(byte[,] gray)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        if (!HasRuneIconColumn(gray))
            return Empty();

        var peaks = FindSeparatorPeaks(SeparatorScore(gray), h).ToList();
        if (peaks.Count < 2)
            return Empty();

        var boundaries = new List<int>(peaks);
        if (h - boundaries[^1] < 24)
            boundaries[^1] = h;

        if (boundaries[0] is >= 35 and <= 130 && HasLikelyRewardText(gray, 0, boundaries[0]))
            boundaries.Insert(0, 0);

        int bottomGap = h - boundaries[^1];
        if (bottomGap is >= 34 and <= 130 && HasLikelyRewardText(gray, boundaries[^1], h))
            boundaries.Add(h);
        boundaries = NormalizeSeparatorBoundaries(boundaries);

        var rows = new List<RuneshapeRow>();
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            int top = boundaries[i];
            int bottom = boundaries[i + 1];
            int height = bottom - top;
            int minHeight = top == 0 || bottom == h ? 34 : 42;
            if (height < minHeight || height > 130)
                continue;
            if (!HasLikelyRewardText(gray, top, bottom))
                continue;

            int center = (top + bottom) / 2;
            rows.Add(new RuneshapeRow(top, bottom, center, center));
        }

        rows = SplitMergedStandardRows(gray, MergeSplitTallRows(gray, MergeCloseRows(rows)));
        if (rows.Count == 0)
            return Empty();

        var rowBoundaries = rows
            .SelectMany(row => new[] { row.Top, row.Bottom })
            .Distinct()
            .Order()
            .ToList();
        var heights = rows.Select(row => (double)(row.Bottom - row.Top)).ToList();
        int? pitch = heights.Count == 0 ? null : (int)Math.Round(Median(heights));
        double countConfidence = Math.Min(1.0, rows.Count / 6.0);
        double heightConfidence = heights.Count == 0 ? 0 : heights.Average(HeightFit);
        double confidence = Math.Round(countConfidence * Math.Max(0.30, heightConfidence), 3);

        return new RuneshapeRowDetection(rowBoundaries, rows, pitch, confidence);
    }

    private static bool HasRuneIconColumn(byte[,] gray)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        int left = Math.Clamp((int)Math.Round(w * 0.02), 0, w - 1);
        int right = Math.Clamp((int)Math.Round(w * 0.42), left + 1, w);
        int width = right - left;
        int rowDarkThreshold = Math.Max(8, (int)Math.Round(width * 0.035));
        int rowDarkCeiling = Math.Max(rowDarkThreshold + 1, (int)Math.Round(width * 0.60));
        int activeRows = 0;
        int darkPixels = 0;

        for (int y = 0; y < h; y++)
        {
            int rowDark = 0;
            for (int x = left; x < right; x++)
            {
                if (gray[y, x] < 95)
                    rowDark++;
            }

            if (rowDark >= rowDarkThreshold && rowDark <= rowDarkCeiling)
                activeRows++;
            darkPixels += rowDark;
        }

        return activeRows >= Math.Max(20, (int)Math.Round(h * 0.12)) &&
               darkPixels >= rowDarkThreshold * Math.Max(12, h / 20);
    }

    private static double[] SeparatorScore(byte[,] gray)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        var dark = new double[h];
        var edge = new double[h];

        for (int y = 0; y < h; y++)
        {
            int darkPixels = 0;
            long edgeSum = 0;
            for (int x = 0; x < w; x++)
            {
                if (gray[y, x] < 94)
                    darkPixels++;
                if (y > 0 && y < h - 1)
                    edgeSum += Math.Abs(gray[y + 1, x] - gray[y - 1, x]);
            }

            dark[y] = (double)darkPixels / w;
            edge[y] = (double)edgeSum / Math.Max(1, w * 255);
        }

        var darkNorm = Normalize(dark);
        var edgeNorm = Normalize(edge);
        var score = new double[h];
        for (int y = 0; y < h; y++)
            score[y] = darkNorm[y] * 0.62 + edgeNorm[y] * 0.38;
        return score;
    }

    private static IReadOnlyList<int> FindSeparatorPeaks(double[] score, int height)
    {
        var smoothed = Smooth(score, 9);
        double threshold = Math.Max(Percentile(smoothed, 0.86), smoothed.Average() + StdDev(smoothed) * 0.55);
        var raw = new List<(int Y, double Value)>();
        for (int y = 5; y < height - 5; y++)
        {
            double value = smoothed[y];
            if (value < threshold) continue;
            bool localMax = true;
            for (int dy = -5; dy <= 5; dy++)
                if (smoothed[y + dy] > value) { localMax = false; break; }
            if (localMax) raw.Add((y, value));
        }

        var merged = new List<(int Y, double Value)>();
        foreach (var peak in raw)
        {
            if (merged.Count == 0 || peak.Y - merged[^1].Y > 22)
                merged.Add(peak);
            else if (peak.Value > merged[^1].Value)
                merged[^1] = peak;
        }
        return merged.Select(p => p.Y).ToList();
    }

    private static List<RuneshapeRow> MergeCloseRows(IReadOnlyList<RuneshapeRow> rows)
    {
        var merged = new List<RuneshapeRow>();
        foreach (var row in rows.OrderBy(row => row.CenterY))
        {
            if (merged.Count == 0 || row.CenterY - merged[^1].CenterY > 22)
            {
                merged.Add(row);
                continue;
            }

            var last = merged[^1];
            if (row.Bottom - row.Top > last.Bottom - last.Top)
                merged[^1] = row;
        }
        return merged;
    }

    private static List<RuneshapeRow> MergeSplitTallRows(byte[,] gray, IReadOnlyList<RuneshapeRow> rows)
    {
        var merged = new List<RuneshapeRow>();
        for (int i = 0; i < rows.Count; i++)
        {
            var current = rows[i];
            if (i + 1 >= rows.Count)
            {
                merged.Add(current);
                continue;
            }

            var next = rows[i + 1];
            int currentHeight = current.Bottom - current.Top;
            int nextHeight = next.Bottom - next.Top;
            int combinedHeight = next.Bottom - current.Top;
            if (currentHeight is >= 42 and <= 70 &&
                nextHeight is >= 42 and <= 70 &&
                combinedHeight is >= 96 and <= 118)
            {
                int currentName = RewardNameColumnScore(gray, current.Top, current.Bottom);
                int nextName = RewardNameColumnScore(gray, next.Top, next.Bottom);
                if (Math.Min(currentName, nextName) < 60 &&
                    Math.Max(currentName, nextName) >= 70)
                {
                    int top = current.Top;
                    int bottom = next.Bottom;
                    merged.Add(new RuneshapeRow(top, bottom, (top + bottom) / 2, (top + bottom) / 2));
                    i++;
                    continue;
                }
            }

            merged.Add(current);
        }

        return merged;
    }

    private static List<RuneshapeRow> SplitMergedStandardRows(byte[,] gray, IReadOnlyList<RuneshapeRow> rows)
    {
        var standardHeights = rows
            .Select(row => row.Bottom - row.Top)
            .Where(height => height is >= 52 and <= 80)
            .Select(height => (double)height)
            .ToList();
        if (standardHeights.Count < 3)
            return rows.ToList();

        int pitch = (int)Math.Round(Median(standardHeights));
        if (pitch is < 54 or > 76)
            return rows.ToList();

        var split = new List<RuneshapeRow>(rows.Count);
        foreach (var row in rows)
        {
            int height = row.Bottom - row.Top;
            if (height >= pitch * 1.75 &&
                height <= pitch * 2.25)
            {
                int middle = row.Top + pitch;
                if (middle - row.Top >= 42 &&
                    row.Bottom - middle >= 42 &&
                    RewardNameColumnScore(gray, row.Top, middle) >= 60 &&
                    RewardNameColumnScore(gray, middle, row.Bottom) >= 60)
                {
                    split.Add(new RuneshapeRow(row.Top, middle, (row.Top + middle) / 2, (row.Top + middle) / 2));
                    split.Add(new RuneshapeRow(middle, row.Bottom, (middle + row.Bottom) / 2, (middle + row.Bottom) / 2));
                    continue;
                }
            }

            split.Add(row);
        }

        return split;
    }

    private static List<int> NormalizeSeparatorBoundaries(IReadOnlyList<int> boundaries)
    {
        var normalized = new List<int>(boundaries.Count);
        foreach (int boundary in boundaries.Order())
        {
            if (normalized.Count > 0 && boundary - normalized[^1] < 42)
            {
                if (normalized[^1] == 0 && boundary - normalized[^1] >= 34)
                {
                    normalized.Add(boundary);
                    continue;
                }
                continue;
            }
            normalized.Add(boundary);
        }
        return normalized;
    }

    private static int RewardNameColumnScore(byte[,] gray, int topBoundary, int bottomBoundary)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        int top = Math.Clamp(topBoundary, 0, h - 1);
        int bottom = Math.Clamp(bottomBoundary, top + 1, h);
        int height = bottom - top;
        if (height < 24) return 0;

        int searchTop = Math.Clamp(top + Math.Max(6, (int)Math.Round(height * 0.20)), top, bottom - 1);
        int searchBottom = Math.Clamp(bottom - Math.Max(4, (int)Math.Round(height * 0.12)), searchTop + 1, bottom);
        int left = Math.Clamp((int)Math.Round(w * 0.43), 0, w - 1);
        int right = Math.Clamp((int)Math.Round(w * 0.96), left + 1, w);
        int minDarkPixels = Math.Max(4, (int)Math.Round((searchBottom - searchTop) * 0.12));

        var darkColumns = new bool[right - left];
        for (int x = left; x < right; x++)
        {
            int dark = 0;
            for (int y = searchTop; y < searchBottom; y++)
            {
                if (gray[y, x] < 95 && ++dark >= minDarkPixels)
                    break;
            }
            darkColumns[x - left] = dark >= minDarkPixels;
        }

        int score = 0;
        for (int i = 0; i < darkColumns.Length; i++)
        {
            if (!darkColumns[i])
                continue;

            int from = Math.Max(0, i - 2);
            int to = Math.Min(darkColumns.Length - 1, i + 2);
            int neighbors = 0;
            for (int j = from; j <= to; j++)
                if (darkColumns[j]) neighbors++;
            if (neighbors >= 2)
                score++;
        }

        return score;
    }

    private static double HeightFit(double height)
    {
        if (height is < 34 or > 132)
            return 0;

        double standard = 1.0 - Math.Min(1.0, Math.Abs(height - 63.0) / 24.0);
        double tall = 1.0 - Math.Min(1.0, Math.Abs(height - 108.0) / 30.0);
        double partial = height < 56 ? 0.70 : 0.0;
        return Math.Max(partial, Math.Max(standard, tall));
    }

    private static double[] Normalize(double[] values)
    {
        double min = values.Min();
        double max = values.Max();
        double span = max - min;
        if (span <= 0.000001)
            return new double[values.Length];

        var normalized = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
            normalized[i] = (values[i] - min) / span;
        return normalized;
    }

    private static double[] EdgeScore(byte[,] gray)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        var score = new double[h];
        for (int y = 1; y < h - 1; y++)
        {
            long sum = 0;
            for (int x = 0; x < w; x++)
                sum += Math.Abs(gray[y + 1, x] - gray[y - 1, x]);
            score[y] = (double)sum / w;
        }
        return score;
    }

    private static double[] DarkScore(byte[,] gray)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        var score = new double[h];
        for (int y = 0; y < h; y++)
        {
            long sum = 0;
            for (int x = 0; x < w; x++)
                sum += gray[y, x];
            score[y] = 255.0 - (double)sum / w;
        }
        return score;
    }

    private static RuneshapeRowDetection DetectBrightBands(byte[,] gray)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        var score = new double[h];
        for (int y = 0; y < h; y++)
        {
            long sum = 0;
            for (int x = 0; x < w; x++)
                sum += gray[y, x];
            score[y] = (double)sum / w;
        }

        var smoothed = Smooth(score, 9);
        double threshold = (Percentile(smoothed, 0.20) + Percentile(smoothed, 0.80)) / 2.0;
        var rows = new List<RuneshapeRow>();
        var boundaries = new List<int>();
        int? start = null;

        for (int y = 0; y < h; y++)
        {
            bool bright = smoothed[y] >= threshold;
            if (bright && start is null)
            {
                start = y;
                continue;
            }

            if ((!bright || y == h - 1) && start is { } top)
            {
                int end = bright && y == h - 1 ? h : y;
                AddBrightBand(gray, rows, boundaries, top, end, h);
                start = null;
            }
        }

        if (rows.Count == 0)
            return Empty();

        if (rows.Count >= 3)
            rows = MergeOverlappingRows(FillMissingBrightRows(gray, rows));

        boundaries = rows.SelectMany(row => new[] { row.Top, row.Bottom }).ToList();
        var centers = rows.Select(row => row.CenterY).ToList();
        var gaps = centers.Zip(centers.Skip(1), (a, b) => (double)(b - a)).ToList();
        var heights = rows.Select(row => (double)(row.Bottom - row.Top)).ToList();
        int? pitch = gaps.Count == 0 ? null : (int)Math.Round(Median(gaps));
        double gapConfidence = gaps.Count == 0 ? 0.5 : Math.Max(0.55, 1.0 - StdDev(gaps) / 18.0);
        double heightConfidence = Math.Max(0.55, 1.0 - StdDev(heights) / 15.0);
        double countConfidence = Math.Min(1.0, rows.Count / 6.0);
        double confidence = countConfidence * gapConfidence * heightConfidence;

        return new RuneshapeRowDetection(boundaries, rows, pitch, Math.Round(confidence, 3));
    }

    private static List<RuneshapeRow> FillMissingBrightRows(byte[,] gray, IReadOnlyList<RuneshapeRow> detectedRows)
    {
        var ordered = detectedRows.OrderBy(row => row.CenterY).ToList();
        if (ordered.Count < 3)
            return ordered;

        var gaps = ordered.Zip(ordered.Skip(1), (a, b) => (double)(b.CenterY - a.CenterY)).ToList();
        int pitch = (int)Math.Round(Median(gaps));
        if (pitch is < 42 or > 76)
            return ordered;

        var heights = ordered.Select(row => (double)(row.Bottom - row.Top)).ToList();
        int rowHeight = Math.Clamp((int)Math.Round(Median(heights)), 34, Math.Min(70, pitch - 4));
        int halfHeight = Math.Max(17, rowHeight / 2);
        int h = gray.GetLength(0);
        var filled = new List<RuneshapeRow>();

        for (int i = 0; i < ordered.Count - 1; i++)
        {
            filled.Add(ordered[i]);
            int nextCenter = ordered[i].CenterY + pitch;
            while (nextCenter < ordered[i + 1].CenterY - pitch * 0.45)
            {
                if (TryCreateTextRow(gray, nextCenter, halfHeight, h, out var synthetic))
                    filled.Add(synthetic);
                nextCenter += pitch;
            }
        }

        filled.Add(ordered[^1]);

        int projected = ordered[^1].CenterY + pitch;
        while (projected + halfHeight <= h)
        {
            if (!TryCreateTextRow(gray, projected, halfHeight, h, out var synthetic))
                break;
            filled.Add(synthetic);
            projected += pitch;
        }

        return filled
            .OrderBy(row => row.CenterY)
            .DistinctBy(row => row.CenterY / 8)
            .ToList();
    }

    private static List<RuneshapeRow> MergeOverlappingRows(IReadOnlyList<RuneshapeRow> rows)
    {
        const int MaxMergedHeight = 130;
        var ordered = rows.OrderBy(row => row.Top).ToList();
        if (ordered.Count <= 1)
            return ordered;

        var merged = new List<RuneshapeRow>();
        foreach (var row in ordered)
        {
            if (merged.Count == 0)
            {
                merged.Add(row);
                continue;
            }

            var last = merged[^1];
            if (row.Top <= last.Bottom + 4 && Math.Max(last.Bottom, row.Bottom) - last.Top <= MaxMergedHeight)
            {
                int top = last.Top;
                int bottom = Math.Max(last.Bottom, row.Bottom);
                merged[^1] = new RuneshapeRow(top, bottom, (top + bottom) / 2, (top + bottom) / 2);
                continue;
            }

            merged.Add(row);
        }

        return merged;
    }

    private static bool TryCreateTextRow(byte[,] gray, int center, int halfHeight, int imageHeight, out RuneshapeRow row)
    {
        int top = Math.Clamp(center - halfHeight, 0, imageHeight - 1);
        int bottom = Math.Clamp(center + halfHeight, top + 1, imageHeight);
        if (bottom - top < 34 || !HasLikelyRewardText(gray, top, bottom))
        {
            row = default!;
            return false;
        }

        row = new RuneshapeRow(top, bottom, (top + bottom) / 2, (top + bottom) / 2);
        return true;
    }

    private static void AddBrightBand(byte[,] gray, List<RuneshapeRow> rows, List<int> boundaries, int start, int end, int height)
    {
        const int MinHeight = 34;
        const int MaxHeight = 130;

        int top = Math.Max(0, start - 2);
        int bottom = Math.Min(height, end + 2);
        int bandHeight = bottom - top;
        if (bandHeight is < MinHeight or > MaxHeight || !HasLikelyRewardText(gray, top, bottom))
            return;

        int center = (top + bottom) / 2;
        rows.Add(new RuneshapeRow(top, bottom, center, center));
        boundaries.Add(top);
        boundaries.Add(bottom);
    }

    private static IReadOnlyList<int> FindPeaks(double[] score, int height)
    {
        var smoothed = Smooth(score, 5);
        double threshold = Math.Max(Percentile(smoothed, 0.88), smoothed.Average() + StdDev(smoothed) * 0.7);
        var raw = new List<(int Y, double Value)>();
        for (int y = 3; y < height - 3; y++)
        {
            double value = smoothed[y];
            if (value < threshold) continue;
            bool localMax = true;
            for (int dy = -3; dy <= 3; dy++)
                if (smoothed[y + dy] > value) { localMax = false; break; }
            if (localMax) raw.Add((y, value));
        }

        var merged = new List<(int Y, double Value)>();
        foreach (var peak in raw)
        {
            if (merged.Count == 0 || peak.Y - merged[^1].Y > 12)
                merged.Add(peak);
            else if (peak.Value > merged[^1].Value)
                merged[^1] = peak;
        }
        return merged.Select(p => p.Y).ToList();
    }

    private static RuneshapeRowDetection InferRows(IReadOnlyList<int> peaks, byte[,] gray)
    {
        var ordered = peaks.Order().ToList();
        if (ordered.Count < 2) return Empty();

        int? bestPitch = null;
        var best = new List<int>();
        foreach (int pitch in CandidatePitches(ordered))
        {
            var chain = LongestChain(ordered, pitch);
            if (chain.Count > best.Count)
            {
                bestPitch = pitch;
                best = chain;
            }
        }

        if (best.Count >= 4)
        {
            best = AddSyntheticTopBoundaryIfNeeded(best, bestPitch, gray);
            var rows = new List<RuneshapeRow>(best.Count - 1);
            for (int i = 0; i < best.Count - 1; i++)
            {
                int center = (best[i] + best[i + 1]) / 2;
                rows.Add(new RuneshapeRow(best[i], best[i + 1], center, center));
            }

            var gaps = best.Zip(best.Skip(1), (a, b) => (double)(b - a)).ToList();
            double spread = StdDev(gaps);
            double confidence = Math.Min(1.0, rows.Count / 10.0) * Math.Max(0.25, 1.0 - spread / 18.0);
            return new RuneshapeRowDetection(best, rows, bestPitch, Math.Round(confidence, 3));
        }

        var fallbackRows = new List<RuneshapeRow>();
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            int top = ordered[i];
            int bottom = ordered[i + 1];
            if (bottom - top is >= 28 and <= 90)
            {
                int center = (top + bottom) / 2;
                fallbackRows.Add(new RuneshapeRow(top, bottom, center, center));
            }
        }
        return new RuneshapeRowDetection(ordered, fallbackRows, null, Math.Min(0.45, fallbackRows.Count / 20.0));
    }

    private static List<int> AddSyntheticTopBoundaryIfNeeded(List<int> boundaries, int? pitch, byte[,] gray)
    {
        if (pitch is not { } p || boundaries.Count == 0)
            return boundaries;

        var first = boundaries[0];
        var tolerance = Math.Max(7, (int)Math.Round(p * 0.22));
        if (first < 28)
            return boundaries;

        if (first <= p - Math.Max(8, tolerance / 2))
            return [0, .. boundaries];

        var projectedTop = first - p;
        if (projectedTop >= 12 &&
            projectedTop <= Math.Max(48, p - 12) &&
            HasLikelyRewardText(gray, projectedTop, first))
        {
            return [projectedTop, .. boundaries];
        }

        return boundaries;
    }

    private static bool HasLikelyRewardText(byte[,] gray, int topBoundary, int bottomBoundary)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        int top = Math.Clamp(topBoundary, 0, h - 1);
        int bottom = Math.Clamp(bottomBoundary, top + 1, h);
        int height = bottom - top;
        if (height < 24) return false;

        int searchTop = Math.Clamp(top + Math.Max(6, (int)Math.Round(height * 0.20)), top, bottom - 1);
        int searchBottom = Math.Clamp(bottom - Math.Max(4, (int)Math.Round(height * 0.12)), searchTop + 1, bottom);
        int left = Math.Clamp((int)Math.Round(w * 0.43), 0, w - 1);
        int right = Math.Clamp((int)Math.Round(w * 0.96), left + 1, w);
        int width = right - left;
        int activeRows = 0;
        int darkPixels = 0;
        int rowDarkThreshold = Math.Max(6, (int)Math.Round(width * 0.012));

        for (int y = searchTop; y < searchBottom; y++)
        {
            int rowDark = 0;
            for (int x = left; x < right; x++)
            {
                if (gray[y, x] < 105)
                    rowDark++;
            }

            if (rowDark >= rowDarkThreshold)
                activeRows++;
            darkPixels += rowDark;
        }

        return activeRows >= 3 && darkPixels >= rowDarkThreshold * 5;
    }

    private static IReadOnlyList<int> CandidatePitches(IReadOnlyList<int> peaks)
    {
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < peaks.Count; i++)
        {
            for (int j = i + 1; j < peaks.Count; j++)
            {
                int diff = peaks[j] - peaks[i];
                if (diff is < 30 or > 95) continue;
                int rounded = (int)Math.Round(diff / 4.0) * 4;
                counts[rounded] = counts.TryGetValue(rounded, out var n) ? n + 1 : 1;
            }
        }
        return counts.Count == 0
            ? [40]
            : counts.OrderByDescending(kvp => kvp.Value).Take(8).Select(kvp => kvp.Key).ToList();
    }

    private static List<int> LongestChain(IReadOnlyList<int> peaks, int pitch)
    {
        int tolerance = Math.Max(7, (int)Math.Round(pitch * 0.22));
        var best = new List<int>();
        foreach (int start in peaks)
        {
            var chain = new List<int> { start };
            int current = start;
            while (true)
            {
                var candidates = peaks
                    .Where(p => p > current + tolerance && Math.Abs(p - (current + pitch)) <= tolerance)
                    .ToList();
                if (candidates.Count == 0) break;
                current = candidates.MinBy(p => Math.Abs(p - (current + pitch)));
                chain.Add(current);
            }
            if (chain.Count > best.Count) best = chain;
        }
        return best;
    }

    private static int RefineTextCenter(byte[,] gray, int topBoundary, int bottomBoundary, int fallback)
    {
        int h = gray.GetLength(0);
        int w = gray.GetLength(1);
        int gap = bottomBoundary - topBoundary;
        if (gap < 24) return fallback;

        int top = Math.Clamp(topBoundary + Math.Max(8, (int)Math.Round(gap * 0.18)), 0, h - 1);
        int bottom = Math.Clamp(bottomBoundary - Math.Max(8, (int)Math.Round(gap * 0.14)), top + 1, h);
        int left = Math.Clamp((int)Math.Round(w * 0.45), 0, w - 1);
        int right = w;
        var values = new List<byte>((bottom - top) * (right - left));
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                values.Add(gray[y, x]);

        double threshold = Math.Min(120.0, Percentile(values.Select(v => (double)v).ToArray(), 0.25) + 8.0);
        var score = new double[bottom - top];
        for (int y = top; y < bottom; y++)
        {
            int count = 0;
            for (int x = left; x < right; x++)
                if (gray[y, x] < threshold) count++;
            score[y - top] = count > (right - left) * 0.38 ? 0 : count;
        }

        if (score.Length == 0 || score.Max() < Math.Max(4.0, (right - left) * 0.008))
            return fallback;

        var smoothed = Smooth(score, 3);
        int best = 0;
        for (int i = 1; i < smoothed.Length; i++)
            if (smoothed[i] > smoothed[best]) best = i;
        return top + best;
    }

    private static double[] Smooth(IReadOnlyList<double> values, int window)
    {
        var result = new double[values.Count];
        int half = window / 2;
        for (int i = 0; i < values.Count; i++)
        {
            double sum = 0;
            int count = 0;
            for (int j = Math.Max(0, i - half); j <= Math.Min(values.Count - 1, i + half); j++)
            {
                sum += values[j];
                count++;
            }
            result[i] = sum / count;
        }
        return result;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        int idx = Math.Clamp((int)Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count <= 1) return 0;
        double avg = values.Average();
        double variance = values.Sum(v => (v - avg) * (v - avg)) / values.Count;
        return Math.Sqrt(variance);
    }
}
