using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PoeAncientsPriceHelper.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Offline regression harness for the headless detection pipeline.
//
//   dotnet run --project src/PoeAncientsPriceHelper.Eval -- <cropsDir> [--baseline <json>] [--out <json>]
//
// Runs RuneshapeRowDetector.Detect + RuneshapePanelGate over every frame_*.png capture-region crop,
// computes gate accuracy / row geometry / runtime, and (when a baseline is given) diffs per-frame row
// counts, gate decisions and panelScore against it. Exits non-zero on any regression so it can gate CI.
//
// The cropsDir / baseline can also come from EVAL_CORPUS_DIR / EVAL_BASELINE env vars.

// Deterministic, locale-independent number formatting in the console report and JSON.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const double ScoreHighOpenThreshold = 0.65;   // mirrors ScanEngine's composite panelLooksOpen
const double PanelScoreEpsilon = 0.01;         // max |panelScore - baseline| before we flag port drift

var positional = new List<string>();
string? baselinePath = Environment.GetEnvironmentVariable("EVAL_BASELINE");
string? outPath = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--baseline": baselinePath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}

var cropsDir = positional.FirstOrDefault() ?? Environment.GetEnvironmentVariable("EVAL_CORPUS_DIR");
if (string.IsNullOrWhiteSpace(cropsDir) || !Directory.Exists(cropsDir))
{
    Console.Error.WriteLine("usage: eval <cropsDir> [--baseline <json>] [--out <json>]  (or set EVAL_CORPUS_DIR)");
    Console.Error.WriteLine($"  cropsDir not found: {cropsDir ?? "(none)"}");
    return 2;
}

// Baseline: file -> (rowCount, panelScore, gateOpen, expectedOpen). Used both for ground-truth labels
// (expectedOpen) and for regression diffing.
var baseline = LoadBaseline(baselinePath);

var files = Directory.GetFiles(cropsDir, "frame_*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0)
{
    Console.Error.WriteLine($"no frame_*.png crops in {cropsDir}");
    return 2;
}

var detector = new RuneshapeRowDetector();
var frames = new List<FrameResult>(files.Count);
var runtimes = new List<double>(files.Count);

// Warm up the JIT so runtime percentiles reflect steady state, not first-call compilation.
{
    var (g0, _, _, _, _) = LoadFrame(files[0]);
    detector.Detect(g0);
}

foreach (var file in files)
{
    var name = Path.GetFileName(file);
    var (gray, bgr, stride, w, h) = LoadFrame(file);

    var sw = Stopwatch.StartNew();
    var detection = detector.Detect(gray);
    var score = RuneshapePanelGate.Score(bgr, stride, w, h);
    sw.Stop();

    var ms = sw.Elapsed.TotalMilliseconds;
    runtimes.Add(ms);

    var scoreOpen = score >= RuneshapePanelGate.OpenThreshold;
    // Composite gate exactly as ScanEngine computes panelLooksOpen.
    var gateOpen = scoreOpen && (detection.HasUsableRows || (score >= ScoreHighOpenThreshold && detection.HasRowCandidates));

    baseline.TryGetValue(name, out var b);
    frames.Add(new FrameResult(name, detection.Rows.Count, Math.Round(detection.Confidence, 3),
        detection.RowPitch, detection.HasUsableRows, Math.Round(score, 3), gateOpen,
        b?.ExpectedOpen, b, Math.Round(ms, 3)));
}

// ---- aggregate metrics ----
runtimes.Sort();
// Nearest-rank percentile: rank = ceil(q*N), index = rank-1 (clamped). Avoids the off-by-one of
// truncating q*N when it lands on an integer (e.g. p95 of 100 samples -> index 94, not 95).
double Pct(double q) => runtimes[Math.Clamp((int)Math.Ceiling(q * runtimes.Count) - 1, 0, runtimes.Count - 1)];
int labelled = frames.Count(f => f.ExpectedOpen is not null);
int gateCorrect = frames.Count(f => f.ExpectedOpen is { } e && e == f.GateOpen);
var falseOpen = frames.Where(f => f.ExpectedOpen == false && f.GateOpen).Select(f => f.File).ToList();
var missedOpen = frames.Where(f => f.ExpectedOpen == true && !f.GateOpen).Select(f => f.File).ToList();

// ---- regression diffs vs baseline ----
var countDiffs = frames.Where(f => f.Baseline is { } b && b.RowCount != f.RowCount)
    .Select(f => $"{f.File}: rows {f.Baseline!.RowCount} -> {f.RowCount}").ToList();
var gateDiffs = frames.Where(f => f.Baseline?.GateOpen is { } bg && bg != f.GateOpen)
    .Select(f => $"{f.File}: gateOpen {f.Baseline!.GateOpen} -> {f.GateOpen} (score {f.PanelScore})").ToList();
var scoreDrift = frames.Where(f => f.Baseline?.PanelScore is { } bs && Math.Abs(bs - f.PanelScore) > PanelScoreEpsilon)
    .Select(f => $"{f.File}: panelScore {f.Baseline!.PanelScore} -> {f.PanelScore} (Δ{Math.Abs(f.Baseline!.PanelScore - f.PanelScore):0.000})").ToList();
double maxScoreDelta = frames.Where(f => f.Baseline?.PanelScore is not null)
    .Select(f => Math.Abs(f.Baseline!.PanelScore - f.PanelScore)).DefaultIfEmpty(0).Max();

var summary = new
{
    cropsDir,
    baseline = baselinePath,
    frames = frames.Count,
    labelledFrames = labelled,
    expectedOpenFrames = frames.Count(f => f.ExpectedOpen == true),
    gateOpenFrames = frames.Count(f => f.GateOpen),
    usableFrames = frames.Count(f => f.Usable),
    gateAccuracy = labelled == 0 ? (double?)null : Math.Round((double)gateCorrect / labelled, 4),
    falseGateOpenFrames = falseOpen,
    missedGateOpenFrames = missedOpen,
    avgRowsPerFrame = Math.Round(frames.Average(f => f.RowCount), 2),
    detectorRuntimeMs = new
    {
        avg = Math.Round(runtimes.Average(), 3),
        p50 = Math.Round(Pct(0.50), 3),
        p95 = Math.Round(Pct(0.95), 3),
        max = Math.Round(runtimes.Max(), 3),
    },
    baselineComparison = new
    {
        maxPanelScoreDelta = Math.Round(maxScoreDelta, 4),
        rowCountDiffs = countDiffs,
        gateDecisionDiffs = gateDiffs,
        panelScoreDrift = scoreDrift,
    },
    rows = frames.Select(f => new
    {
        file = f.File,
        expectedOpen = f.ExpectedOpen,
        panelScore = f.PanelScore,
        gateOpen = f.GateOpen,
        usable = f.Usable,
        count = f.RowCount,
        confidence = f.Confidence,
        pitch = f.Pitch,
        runtimeMs = f.RuntimeMs,
    }),
};

var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
if (outPath is not null) File.WriteAllText(outPath, json);

// ---- console report ----
Console.WriteLine($"frames={frames.Count} labelled={labelled} expectedOpen={summary.expectedOpenFrames} gateOpen={summary.gateOpenFrames} usable={summary.usableFrames}");
Console.WriteLine($"gateAccuracy={(summary.gateAccuracy?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "n/a")} avgRows={summary.avgRowsPerFrame}");
Console.WriteLine($"runtimeMs avg={summary.detectorRuntimeMs.avg} p50={summary.detectorRuntimeMs.p50} p95={summary.detectorRuntimeMs.p95} max={summary.detectorRuntimeMs.max}");
if (baseline.Count > 0)
{
    Console.WriteLine($"vs baseline: maxPanelScoreDelta={maxScoreDelta:0.0000} rowCountDiffs={countDiffs.Count} gateDecisionDiffs={gateDiffs.Count} panelScoreDrift={scoreDrift.Count}");
    foreach (var d in countDiffs.Concat(gateDiffs).Concat(scoreDrift).Take(30)) Console.WriteLine("  " + d);
}
if (falseOpen.Count > 0) Console.WriteLine($"falseGateOpen: {string.Join(", ", falseOpen)}");
if (missedOpen.Count > 0) Console.WriteLine($"missedGateOpen: {string.Join(", ", missedOpen)}");
if (outPath is not null) Console.WriteLine($"wrote {outPath}");

bool regressed = countDiffs.Count > 0 || gateDiffs.Count > 0 || maxScoreDelta > PanelScoreEpsilon;
if (regressed) Console.Error.WriteLine("REGRESSION: detector/gate output diverged from baseline");
return regressed ? 1 : 0;

// ---- helpers ----
static (byte[,] gray, byte[] bgr, int stride, int w, int h) LoadFrame(string path)
{
    using var image = Image.Load<Rgb24>(path);
    int w = image.Width, h = image.Height, stride = w * 3;
    var gray = new byte[h, w];
    var bgr = new byte[stride * h];
    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < h; y++)
        {
            var row = accessor.GetRowSpan(y);
            int rowBase = y * stride;
            for (int x = 0; x < w; x++)
            {
                var p = row[x];
                gray[y, x] = (byte)((p.R * 30 + p.G * 59 + p.B * 11) / 100);
                int i = rowBase + x * 3;
                bgr[i] = p.B;
                bgr[i + 1] = p.G;
                bgr[i + 2] = p.R;
            }
        }
    });
    return (gray, bgr, stride, w, h);
}

static Dictionary<string, BaselineRow> LoadBaseline(string? path)
{
    var map = new Dictionary<string, BaselineRow>(StringComparer.Ordinal);
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return map;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (!doc.RootElement.TryGetProperty("rows", out var rows)) return map;
    foreach (var r in rows.EnumerateArray())
    {
        if (!r.TryGetProperty("file", out var fileEl)) continue;
        var file = fileEl.GetString();
        if (file is null) continue;
        map[file] = new BaselineRow(
            RowCount: r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1,
            PanelScore: r.TryGetProperty("panelScore", out var ps) && ps.ValueKind == JsonValueKind.Number ? Math.Round(ps.GetDouble(), 3) : double.NaN,
            GateOpen: r.TryGetProperty("gateOpen", out var go) && (go.ValueKind == JsonValueKind.True || go.ValueKind == JsonValueKind.False) ? go.GetBoolean() : null,
            ExpectedOpen: r.TryGetProperty("expectedOpen", out var eo) && (eo.ValueKind == JsonValueKind.True || eo.ValueKind == JsonValueKind.False) ? eo.GetBoolean() : null);
    }
    return map;
}

internal sealed record BaselineRow(int RowCount, double PanelScore, bool? GateOpen, bool? ExpectedOpen);

internal sealed record FrameResult(
    string File, int RowCount, double Confidence, int? Pitch, bool Usable,
    double PanelScore, bool GateOpen, bool? ExpectedOpen, BaselineRow? Baseline, double RuntimeMs);
