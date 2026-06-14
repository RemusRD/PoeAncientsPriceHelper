using System.Diagnostics;
using System.Drawing;

namespace PoeAncientsPriceHelper;

internal sealed record ScanOnceResult(
    bool Success,
    string Message,
    int OcrRows,
    int PricedRows,
    bool HasLoadingRows = false,
    int DisplayRows = 0,
    string OcrSource = "",
    bool UsedFallback = false,
    PriceCheckMetrics? PriceMetrics = null);

internal sealed record PriceCheckMetrics(
    int UnpricedRows,
    int LoadingRows,
    int MissingPriceRows,
    int NeedsGemLevelRows,
    int UnknownRows,
    int MissingVisibleRows,
    int MissingPriceOcrRows,
    int NeedsGemLevelOcrRows,
    int SuppressedOcrRows,
    int FailureCount,
    string FailureSummary);

internal sealed class ScanEngine : IDisposable
{
    private const int WatchIntervalMs = 1000;
    private const int ForegroundPollIntervalMs = 200;
    private const int MissingDependencyIntervalMs = 3000;
    private const int ErrorBackoffIntervalMs = 3000;
    private const int WaitingProfileLogIntervalMs = 1000;
    private const int FullRegionFallbackRowThreshold = 2;
    private const int ClosedGateGraceTicks = 1;

    private readonly AppConfig _config;
    private readonly PriceRepository _prices;
    private readonly IconCache _icons;
    private readonly SemaphoreSlim _checkNowLock = new(1, 1);
    private readonly List<RowSlot> _rowSlots = [];
    private readonly Dictionary<string, DateTime> _lastUnmatchedLogByName = new();
    private Dictionary<string, int> _lastPositions = new();
    private string _logPath = "";
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;
    private OcrScanner? _scanner;
    private string? _scannerTessdataDir;
    private string? _lastWatchHash;
    private string? _lastOcrHash;
    private string? _lastFailedOcrHash;
    private DateTime? _lastOcrPriceFetchedAt;
    private DateTime _lastSuccessfulOcrAtUtc = DateTime.MinValue;
    private DateTime _lastFailedOcrAtUtc = DateTime.MinValue;
    private bool _watchPanelVisible;
    private int _closedGateTicks;
    private string? _lastWatchState;
    private int _nextWatchIntervalMs = WatchIntervalMs;
    private DateTime _lastWaitingProfileAtUtc = DateTime.MinValue;
    private IReadOnlyList<PriceRow> _lastDisplayRows = [];
    private volatile bool _dismissedUntilPanelCloses;
    private long _lastWatchStartTick;
    private bool _perfViewActive;
    private int _perfViewId;
    private string _perfViewSignature = "";
    private string _perfViewStartReason = "";
    private DateTime _perfViewStartedAtUtc = DateTime.MinValue;
    private long _perfViewStartedTick;
    private long? _perfFirstOcrTick;
    private long? _perfFirstDisplayTick;
    private long? _perfFirstPricedTick;
    private string _perfFirstOcrSource = "";
    private int _perfCycles;
    private int _perfOcrAttempts;
    private int _perfCacheSkips;
    private int _perfFailedBackoffs;
    private int _perfLoadingCycles;
    private int _perfChangedCycles;
    private int _perfFallbacks;
    private int _perfPriceFailureCycles;
    private int _perfMaxRowCandidates;
    private int _perfMaxDisplayRows;
    private int _perfMaxPricedRows;
    private int _perfMaxUnpricedRows;
    private int _perfMaxMissingPriceRows;
    private int _perfMaxNeedsGemLevelRows;
    private int _perfMaxUnknownRows;
    private int _perfMaxMissingVisibleRows;
    private int _perfMaxSuppressedOcrRows;
    private readonly List<string> _perfFailureSamples = [];
    private double _perfCaptureMs;
    private double _perfRowDetectionMs;
    private double _perfGateHashMs;
    private double _perfOcrMs;
    private double _perfPriceMergeMs;
    private double _perfOverlayUpdateMs;

    public ScanEngine(AppConfig config, PriceRepository prices, IconCache icons)
    {
        _config = config;
        _prices = prices;
        _icons = icons;
    }

    private void Log(string msg)
    {
        if (string.IsNullOrEmpty(_logPath))
            _logPath = Path.Combine(AppContext.BaseDirectory, "scan_log.txt");
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + "\n"); } catch { }
        if (App.DebugMode) Console.WriteLine(line);
    }

    public async Task<ScanOnceResult> CheckNowAsync()
    {
        await _checkNowLock.WaitAsync();
        try
        {
            return await Task.Run(CheckNowCore);
        }
        finally
        {
            _checkNowLock.Release();
        }
    }

    public void StartWatcher()
    {
        if (_watchTask is { IsCompleted: false }) return;

        _watchCts = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchLoopAsync(_watchCts.Token));
        Log($"watch loop started activeInterval={WatchIntervalMs}ms foregroundPollInterval={ForegroundPollIntervalMs}ms");
    }

    public void DismissUntilPanelCloses()
    {
        _dismissedUntilPanelCloses = true;
        PriceOverlayManager.HideNow();
        Log("watch input dismiss requested; suppressing cached rows until panel closes");
    }

    private async Task WatchLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await WatchOnceAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"watch loop ERROR {ex}");
                EnterWaitingForPoe("watch error; hiding overlay and backing off", ErrorBackoffIntervalMs);
            }

            try
            {
                await Task.Delay(_nextWatchIntervalMs, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
        Log("watch loop stopped");
    }

    private async Task WatchOnceAsync(CancellationToken token)
    {
        if (!await _checkNowLock.WaitAsync(0, token))
            return;

        try
        {
            await Task.Run(WatchOnceCore, token);
        }
        finally
        {
            _checkNowLock.Release();
        }
    }

    private void WatchOnceCore()
    {
        var watchStartTick = Stopwatch.GetTimestamp();
        var profile = ScanProfile.Start("watch");
        if (_lastWatchStartTick != 0)
            profile.AddMetric("sinceLastWatchMs", ElapsedMs(_lastWatchStartTick, watchStartTick));
        _lastWatchStartTick = watchStartTick;
        _logPath = Path.Combine(AppContext.BaseDirectory, "scan_log.txt");

        var tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(tessdataDir))
        {
            _nextWatchIntervalMs = MissingDependencyIntervalMs;
            profile.Write("no-tessdata", autoRegion: false, rowCandidates: 0, changed: null, ocrRows: 0, pricedRows: 0,
                activity: "waiting for tessdata", intervalMs: _nextWatchIntervalMs);
            LogWatchState($"watch failed: tessdata not found at {tessdataDir}");
            return;
        }

        if (!TryResolveCaptureRegion(out var captureRegion, out var autoRegion, out var captureDiagnostic))
        {
            EnterWaitingForPoe(
                $"watch waiting for PoE2 foreground; nextInterval={ForegroundPollIntervalMs}ms; {captureDiagnostic}",
                ForegroundPollIntervalMs,
                preserveReadCache: true);
            if (ShouldWriteWaitingProfile())
            {
                profile.Write("no-window", autoRegion: false, rowCandidates: 0, changed: null, ocrRows: 0, pricedRows: 0,
                    activity: "waiting for PoE2", intervalMs: _nextWatchIntervalMs);
            }
            return;
        }

        _lastWaitingProfileAtUtc = DateTime.MinValue;
        _nextWatchIntervalMs = WatchIntervalMs;
        using var bmp = profile.Measure("capture", () => ScreenCapture.CaptureRegion(captureRegion));
        var rowDetector = new RuneshapeRowDetector();
        var rowDetection = profile.Measure("rowDetection", () => rowDetector.Detect(bmp));
        var panelScore = 0.0;
        var panelBodyLooksOpen = false;
        var signature = "";
        var changed = false;
        profile.Measure("gateHash", () =>
        {
            panelBodyLooksOpen = LooksLikeRuneshapePanel(bmp, out panelScore);
            signature = PanelSignature(bmp, rowDetection);
            changed = signature != _lastWatchHash;
        });
        var panelLooksOpen = panelBodyLooksOpen &&
                             (rowDetection.HasUsableRows ||
                              (panelScore >= 0.65 && rowDetection.HasRowCandidates));
        AddGateMetrics(profile, rowDetection, panelScore, panelLooksOpen);

        if (!panelLooksOpen)
        {
            if (_dismissedUntilPanelCloses)
            {
                AddPerformanceCycleMetrics(profile, "dismissed-closed", rowDetection, panelScore, changed);
                CompletePerformanceView("dismissed-closed", rowDetection.Rows.Count, panelScore);
                ClearWatchCache();
                _dismissedUntilPanelCloses = false;
                _closedGateTicks = 0;
                _watchPanelVisible = false;
                PriceOverlayManager.HideNow();
                profile.Write("gate-dismissed-closed", autoRegion, rowDetection.Rows.Count, changed, ocrRows: 0, pricedRows: 0,
                    activity: "capturing/gated", intervalMs: _nextWatchIntervalMs);
                LogWatchState($"watch dismissed panel closed rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
                return;
            }

            _closedGateTicks++;
            if (_watchPanelVisible && _closedGateTicks < ClosedGateGraceTicks)
            {
                PriceOverlayManager.ForceTopmost();
                AddPerformanceCycleMetrics(profile, "gate-grace", rowDetection, panelScore, changed);
                profile.Write("gate-grace", autoRegion, rowDetection.Rows.Count, changed, ocrRows: 0, pricedRows: 0,
                    activity: "capturing/gated", intervalMs: _nextWatchIntervalMs);
                LogWatchState($"watch gate transient closed ticks={_closedGateTicks}/{ClosedGateGraceTicks} rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
                return;
            }

            var gateClosedState = $"watch gate closed screen={System.Windows.Forms.Screen.FromRectangle(captureRegion).DeviceName} rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}";
            if (_watchPanelVisible)
            {
                Log($"watch panel closed gate rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
                PriceOverlayManager.HideNow();
            }
            else if (gateClosedState != _lastWatchState)
            {
                PriceOverlayManager.HideNow();
            }
            _watchPanelVisible = false;
            AddPerformanceCycleMetrics(profile, "gate-closed", rowDetection, panelScore, changed);
            CompletePerformanceView("gate-closed", rowDetection.Rows.Count, panelScore);
            profile.Write("gate-closed", autoRegion, rowDetection.Rows.Count, changed, ocrRows: 0, pricedRows: 0,
                activity: "capturing/gated", intervalMs: _nextWatchIntervalMs);
            ClearWatchCache();
            LogWatchState(gateClosedState);
            return;
        }
        _closedGateTicks = 0;

        if (_dismissedUntilPanelCloses)
        {
            PriceOverlayManager.HideNow();
            _watchPanelVisible = false;
            EnsurePerformanceView(signature, _watchPanelVisible ? "dismissed-open" : "open", rowDetection, panelScore);
            AddPerformanceCycleMetrics(profile, "input-dismissed", rowDetection, panelScore, changed);
            profile.Write("input-dismissed", autoRegion, rowDetection.Rows.Count, changed, ocrRows: 0, pricedRows: 0,
                activity: "waiting for panel close", intervalMs: _nextWatchIntervalMs);
            LogWatchState($"watch input dismissed; waiting for panel close rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
            return;
        }

        EnsurePerformanceView(signature, changed && _perfViewActive ? "signature-change" : "open", rowDetection, panelScore);

        if (changed)
        {
            if (_watchPanelVisible)
                Log($"watch panel signature changed rows={rowDetection.Rows.Count}; clearing cached rows");
            PriceOverlayManager.UpdateState([], false, false);
            ResetReadState();
            _lastOcrHash = null;
            _lastFailedOcrHash = null;
            _lastOcrPriceFetchedAt = null;
        }
        _lastWatchHash = signature;

        var now = DateTime.UtcNow;
        var priceSnapshotChanged = _prices.LastFetchedAt != _lastOcrPriceFetchedAt;
        if (!changed && !priceSnapshotChanged && signature == _lastOcrHash)
        {
            RestoreCachedOverlayIfNeeded(captureRegion);
            AddPerformanceCycleMetrics(profile, "cache-hit", rowDetection, panelScore, changed, cacheHit: true);
            profile.Write("unchanged-skip", autoRegion, rowDetection.Rows.Count, changed, ocrRows: 0, pricedRows: 0,
                activity: "capturing/unchanged", intervalMs: _nextWatchIntervalMs);
            LogWatchState($"watch unchanged skip ocr rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
            return;
        }

        if (!changed && !priceSnapshotChanged && signature == _lastFailedOcrHash && now - _lastFailedOcrAtUtc < TimeSpan.FromSeconds(5))
        {
            AddPerformanceCycleMetrics(profile, "failed-backoff", rowDetection, panelScore, changed, failedBackoff: true);
            profile.Write("failed-backoff", autoRegion, rowDetection.Rows.Count, changed, ocrRows: 0, pricedRows: 0,
                activity: "capturing/ocr-backoff", intervalMs: _nextWatchIntervalMs);
            LogWatchState($"watch retry backoff after failed ocr rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
            return;
        }

        var scanner = GetScanner(tessdataDir);
        var result = CheckBitmap(scanner, bmp, rowDetection, captureRegion, autoRegion, captureDiagnostic, source: "watch", profile, changed,
            panelScore,
            saveProof: changed);
        _watchPanelVisible = result.OcrRows > 0;
        if (result.OcrRows > 0 && !result.HasLoadingRows)
        {
            _lastOcrHash = signature;
            _lastFailedOcrHash = null;
            _lastOcrPriceFetchedAt = _prices.LastFetchedAt;
            _lastSuccessfulOcrAtUtc = now;
        }
        else if (result.HasLoadingRows)
        {
            _lastOcrHash = null;
            _lastFailedOcrHash = null;
        }
        else
        {
            _lastFailedOcrHash = signature;
            _lastFailedOcrAtUtc = now;
            _lastOcrHash = null;
            _lastOcrPriceFetchedAt = _prices.LastFetchedAt;
        }
        LogWatchState($"watch capturing/scanning checked interval={_nextWatchIntervalMs}ms priced={result.PricedRows} ocr={result.OcrRows} loading={result.HasLoadingRows} changed={changed} priceSnapshotChanged={priceSnapshotChanged} rowCandidates={rowDetection.Rows.Count} score={panelScore:0.00}");
    }

    private ScanOnceResult CheckNowCore()
    {
        var profile = ScanProfile.Start("check-now");
        _logPath = Path.Combine(AppContext.BaseDirectory, "scan_log.txt");

        var tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(tessdataDir))
        {
            var message = $"check now failed: tessdata not found at {tessdataDir}";
            profile.Write("no-tessdata", autoRegion: false, rowCandidates: 0, changed: null, ocrRows: 0, pricedRows: 0);
            Log(message);
            return new ScanOnceResult(false, message, 0, 0);
        }

        Rectangle captureRegion = Rectangle.Empty;
        var autoRegion = false;
        var captureDiagnostic = "";
        var foundCaptureRegion = profile.Measure("gateHash", () => TryResolveCaptureRegion(out captureRegion, out autoRegion, out captureDiagnostic));
        if (!foundCaptureRegion)
        {
            var message = $"check now failed: no PoE2 window; {captureDiagnostic}";
            profile.Write("no-window", autoRegion: false, rowCandidates: 0, changed: null, ocrRows: 0, pricedRows: 0);
            Log(message);
            PriceOverlayManager.UpdateState([], false, false);
            ResetReadState();
            return new ScanOnceResult(false, "PoE2 window not found", 0, 0);
        }

        try
        {
            var scanner = GetScanner(tessdataDir);
            var rowDetector = new RuneshapeRowDetector();
            using var bmp = profile.Measure("capture", () => ScreenCapture.CaptureRegion(captureRegion));
            var rowDetection = profile.Measure("rowDetection", () => rowDetector.Detect(bmp));

            return CheckBitmap(scanner, bmp, rowDetection, captureRegion, autoRegion, captureDiagnostic, source: "check now", profile);
        }
        catch (Exception ex)
        {
            profile.Write("error", autoRegion, rowCandidates: 0, changed: null, ocrRows: 0, pricedRows: 0);
            Log($"check now ERROR {ex}");
            PriceOverlayManager.UpdateState([], false, false);
            ResetReadState();
            return new ScanOnceResult(false, ex.Message, 0, 0);
        }
    }

    private ScanOnceResult CheckBitmap(
        OcrScanner scanner,
        Bitmap bmp,
        RuneshapeRowDetection rowDetection,
        Rectangle captureRegion,
        bool autoRegion,
        string captureDiagnostic,
        string source,
        ScanProfile profile,
        bool? changed = null,
        double panelScore = 0.0,
        bool saveProof = false)
    {
        IReadOnlyList<OcrRow> ocrRows;
        var usedRowStrips = false;
        var usedFallback = false;
        var ocrSource = "full-region";
        if (rowDetection.ShouldUseRowStrips)
        {
            ocrRows = profile.Measure("ocr", () => scanner.ScanRows(bmp, rowDetection.Rows));
            usedRowStrips = ocrRows.Count > 0;
            if (usedRowStrips)
                ocrSource = "row-strip";
            if (ocrRows.Count == 0)
            {
                Log($"{source} row strips returned 0 rows; trying full region auto={autoRegion}");
                ocrRows = profile.Measure("ocr", () => scanner.Scan(bmp));
                ocrSource = "full-region";
            }
        }
        else
        {
            if (rowDetection.HasRowCandidates)
                Log($"{source} row candidates rejected by gate count={rowDetection.Rows.Count} confidence={rowDetection.Confidence:0.000} pitch={rowDetection.RowPitch}");
            ocrRows = profile.Measure("ocr", () => scanner.Scan(bmp));
        }

        IReadOnlyList<PriceRow> rows = [];
        List<PriceRow> pricedRows = [];
        profile.Measure("priceMerge", () =>
        {
            rows = BuildPriceRows(ocrRows, ocrSource);
            pricedRows = rows.Where(r => r.HasPrice).ToList();
        });
        if (usedRowStrips && pricedRows.Count == 0 &&
            (rows.Count == 0 || ocrRows.Count <= FullRegionFallbackRowThreshold))
        {
            Log($"{source} row strips produced {ocrRows.Count} OCR row(s), {rows.Count} display row(s), {pricedRows.Count} priced row(s); trying full region fallback");
            var fullOcrRows = profile.Measure("ocr", () => scanner.Scan(bmp));
            IReadOnlyList<PriceRow> fullRows = [];
            List<PriceRow> fullPricedRows = [];
            profile.Measure("priceMerge", () =>
            {
                fullRows = BuildPriceRows(fullOcrRows, "full-fallback");
                fullPricedRows = fullRows.Where(r => r.HasPrice).ToList();
            });
            if (fullPricedRows.Count > 0 || fullOcrRows.Count > ocrRows.Count)
            {
                Log($"{source} using full region fallback priced={fullPricedRows.Count}/{fullRows.Count} ocr={fullOcrRows.Count}");
                ocrRows = fullOcrRows;
                rows = fullRows;
                pricedRows = fullPricedRows;
                ocrSource = "full-fallback";
                usedFallback = true;
            }
        }
        profile.Measure("priceMerge", () =>
        {
            rows = MergeReads(_rowSlots, rows, evictUnmatchedImmediately: changed != false);
            pricedRows = rows.Where(r => r.HasPrice).ToList();
        });
        var hasLoadingRows = rows.Any(r => !r.HasPrice && r.UnpricedReason == UnpricedReason.Loading);
        bool confirmed = pricedRows.Count > 0;
        profile.Measure("overlayUpdate", () =>
        {
            PriceOverlayManager.EnsureVisible(captureRegion, _config.OverlayXOffset, _icons);
            PriceOverlayManager.UpdateState(rows, rows.Count > 0, false);
            if (confirmed || rows.Count > 0) PriceOverlayManager.ForceTopmost();
        });
        _lastDisplayRows = rows;

        var overlayDiagnostic =
            $"{source} result priced={pricedRows.Count}/{rows.Count} ocr={ocrRows.Count} auto={autoRegion} " +
            $"rowCandidates={rowDetection.Rows.Count} rowConfidence={rowDetection.Confidence:0.00} " +
            $"screen={System.Windows.Forms.Screen.FromRectangle(captureRegion).DeviceName} " +
            $"bounds={System.Windows.Forms.Screen.FromRectangle(captureRegion).Bounds} " +
            $"region={captureRegion} priceX={captureRegion.Right + _config.OverlayXOffset} " +
            $"ys=[{string.Join(",", pricedRows.Select(r => captureRegion.Top + r.CenterY))}] {captureDiagnostic}";
        Log(overlayDiagnostic);
        LogFeedback(source, ocrSource, rowDetection, ocrRows, rows, pricedRows);
        if (source == "check now" || saveProof)
            ScanProofRecorder.SaveCheck(bmp, rowDetection, ocrRows, rows, captureRegion, source, ocrSource, _prices.ItemCount, captureDiagnostic);

        var status = confirmed ? "priced" : ocrRows.Count > 0 ? "unpriced" : "no-ocr";
        var activity = source == "watch" ? "capturing/scanning" : "check-now scanning";
        var intervalMs = source == "watch" ? _nextWatchIntervalMs : 0;
        var priceMetrics = BuildPriceCheckMetrics(rowDetection, ocrRows, rows);
        var result = confirmed
            ? new ScanOnceResult(true, $"Found {pricedRows.Count} priced row(s)", ocrRows.Count, pricedRows.Count, hasLoadingRows, rows.Count, ocrSource, usedFallback, priceMetrics)
            : ocrRows.Count > 0
                ? new ScanOnceResult(false, "OCR found rows, but none matched cached prices", ocrRows.Count, 0, hasLoadingRows, rows.Count, ocrSource, usedFallback, priceMetrics)
                : new ScanOnceResult(false, "No readable Runeshape rows found", 0, 0, false, rows.Count, ocrSource, usedFallback, priceMetrics);
        profile.AddMetric("displayRows", rows.Count);
        profile.AddMetric("loadingRows", hasLoadingRows);
        profile.AddMetric("usedFallback", usedFallback);
        AddPriceCheckProfileMetrics(profile, priceMetrics);
        if (source == "watch")
            AddPerformanceOcrMetrics(profile, rowDetection, panelScore, changed, result);
        profile.Write(status, autoRegion, rowDetection.Rows.Count, changed, ocrRows.Count, pricedRows.Count, ocrSource,
            activity, intervalMs);

        return result;
    }

    private void ResetReadState()
    {
        _rowSlots.Clear();
        _lastPositions.Clear();
        _lastDisplayRows = [];
    }

    private void ClearWatchCache()
    {
        ResetReadState();
        _lastOcrHash = null;
        _lastFailedOcrHash = null;
        _lastOcrPriceFetchedAt = null;
        _lastWatchHash = null;
    }

    private PriceCheckMetrics BuildPriceCheckMetrics(
        RuneshapeRowDetection rowDetection,
        IReadOnlyList<OcrRow> ocrRows,
        IReadOnlyList<PriceRow> rows)
    {
        var unpricedRows = rows.Where(row => !row.HasPrice).ToList();
        var loadingRows = unpricedRows.Count(row => row.UnpricedReason == UnpricedReason.Loading);
        var missingPriceRows = unpricedRows.Count(row => row.UnpricedReason == UnpricedReason.MissingPrice);
        var needsGemLevelRows = unpricedRows.Count(row => row.UnpricedReason == UnpricedReason.NeedsGemLevel);
        var unknownRows = unpricedRows.Count(row => row.UnpricedReason == UnpricedReason.Unknown);
        var missingVisibleRows = Math.Max(0, rowDetection.Rows.Count - ocrRows.Count);
        var missingPriceOcrRows = 0;
        var needsGemLevelOcrRows = 0;
        var suppressedOcrRows = 0;
        var samples = new List<string>();

        foreach (var row in ocrRows)
        {
            var outcome = ResolveFeedbackOutcome(row, out _);
            switch (outcome)
            {
                case "missing-price-or-normalization":
                    missingPriceOcrRows++;
                    AddFailureSample(samples, "ocr-missing", row.NormalizedName);
                    break;
                case "needs-gem-level":
                case "missing-gem-price":
                    needsGemLevelOcrRows++;
                    AddFailureSample(samples, outcome, row.NormalizedName);
                    break;
                case "suppressed-non-reward":
                    suppressedOcrRows++;
                    break;
            }
        }

        foreach (var row in unpricedRows)
            AddFailureSample(samples, row.UnpricedReason.ToString(), row.Name);
        if (missingVisibleRows > 0)
            AddFailureSample(samples, "ocr-missed-visible", missingVisibleRows.ToString());

        var failureCount = missingVisibleRows + Math.Max(unpricedRows.Count, missingPriceOcrRows + needsGemLevelOcrRows);
        return new PriceCheckMetrics(
            unpricedRows.Count,
            loadingRows,
            missingPriceRows,
            needsGemLevelRows,
            unknownRows,
            missingVisibleRows,
            missingPriceOcrRows,
            needsGemLevelOcrRows,
            suppressedOcrRows,
            failureCount,
            string.Join("|", samples));
    }

    private static void AddPriceCheckProfileMetrics(ScanProfile profile, PriceCheckMetrics metrics)
    {
        profile.AddMetric("unpricedRows", metrics.UnpricedRows);
        profile.AddMetric("missingPriceRows", metrics.MissingPriceRows);
        profile.AddMetric("needsGemLevelRows", metrics.NeedsGemLevelRows);
        profile.AddMetric("unknownRows", metrics.UnknownRows);
        profile.AddMetric("missingVisibleRows", metrics.MissingVisibleRows);
        profile.AddMetric("missingPriceOcrRows", metrics.MissingPriceOcrRows);
        profile.AddMetric("needsGemLevelOcrRows", metrics.NeedsGemLevelOcrRows);
        profile.AddMetric("suppressedOcrRows", metrics.SuppressedOcrRows);
        profile.AddMetric("priceFailureCount", metrics.FailureCount);
        if (!string.IsNullOrWhiteSpace(metrics.FailureSummary))
            profile.AddMetric("priceFailureSummary", metrics.FailureSummary);
    }

    private void AddPerformancePriceMetrics(ScanProfile profile, PriceCheckMetrics metrics)
    {
        if (metrics.FailureCount > 0)
            _perfPriceFailureCycles++;
        _perfMaxUnpricedRows = Math.Max(_perfMaxUnpricedRows, metrics.UnpricedRows);
        _perfMaxMissingPriceRows = Math.Max(_perfMaxMissingPriceRows, metrics.MissingPriceRows);
        _perfMaxNeedsGemLevelRows = Math.Max(_perfMaxNeedsGemLevelRows, metrics.NeedsGemLevelRows);
        _perfMaxUnknownRows = Math.Max(_perfMaxUnknownRows, metrics.UnknownRows);
        _perfMaxMissingVisibleRows = Math.Max(_perfMaxMissingVisibleRows, metrics.MissingVisibleRows);
        _perfMaxSuppressedOcrRows = Math.Max(_perfMaxSuppressedOcrRows, metrics.SuppressedOcrRows);

        if (!string.IsNullOrWhiteSpace(metrics.FailureSummary))
        {
            foreach (var sample in metrics.FailureSummary.Split('|', StringSplitOptions.RemoveEmptyEntries))
                AddFailureSample(_perfFailureSamples, sample, "");
        }

        profile.AddMetric("viewPriceFailureCycles", _perfPriceFailureCycles);
        profile.AddMetric("viewMaxUnpricedRows", _perfMaxUnpricedRows);
        profile.AddMetric("viewMaxMissingPriceRows", _perfMaxMissingPriceRows);
        profile.AddMetric("viewMaxNeedsGemLevelRows", _perfMaxNeedsGemLevelRows);
        profile.AddMetric("viewMaxUnknownRows", _perfMaxUnknownRows);
        profile.AddMetric("viewMaxMissingVisibleRows", _perfMaxMissingVisibleRows);
        profile.AddMetric("viewMaxSuppressedOcrRows", _perfMaxSuppressedOcrRows);
        if (_perfFailureSamples.Count > 0)
            profile.AddMetric("viewPriceFailureSamples", string.Join("|", _perfFailureSamples));
    }

    private static void AddFailureSample(List<string> samples, string reason, string name)
    {
        if (samples.Count >= 8) return;
        var cleanReason = CompactMetricToken(reason);
        var cleanName = CompactMetricToken(name);
        var sample = string.IsNullOrWhiteSpace(cleanName) ? cleanReason : $"{cleanReason}:{cleanName}";
        if (!samples.Contains(sample, StringComparer.Ordinal))
            samples.Add(sample);
    }

    private static string CompactMetricToken(string value)
    {
        var clean = Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", "-");
        clean = Regex.Replace(clean, @"[^a-z0-9_.:-]+", "");
        return clean.Length <= 80 ? clean : clean[..80];
    }

    private void AddGateMetrics(ScanProfile profile, RuneshapeRowDetection detection, double panelScore, bool panelLooksOpen)
    {
        profile.AddMetric("panelLooksOpen", panelLooksOpen);
        profile.AddMetric("panelScore", panelScore);
        profile.AddMetric("rowConfidence", detection.Confidence);
        profile.AddMetric("rowPitch", detection.RowPitch);
        profile.AddMetric("usableRows", detection.HasUsableRows);
    }

    private void EnsurePerformanceView(
        string signature,
        string reason,
        RuneshapeRowDetection detection,
        double panelScore)
    {
        if (_perfViewActive && _perfViewSignature == signature)
            return;

        if (_perfViewActive)
            CompletePerformanceView(reason, detection.Rows.Count, panelScore);

        _perfViewActive = true;
        _perfViewId++;
        _perfViewSignature = signature;
        _perfViewStartReason = reason;
        _perfViewStartedAtUtc = DateTime.UtcNow;
        _perfViewStartedTick = Stopwatch.GetTimestamp();
        _perfFirstOcrTick = null;
        _perfFirstDisplayTick = null;
        _perfFirstPricedTick = null;
        _perfFirstOcrSource = "";
        _perfCycles = 0;
        _perfOcrAttempts = 0;
        _perfCacheSkips = 0;
        _perfFailedBackoffs = 0;
        _perfLoadingCycles = 0;
        _perfChangedCycles = 0;
        _perfFallbacks = 0;
        _perfPriceFailureCycles = 0;
        _perfMaxRowCandidates = detection.Rows.Count;
        _perfMaxDisplayRows = 0;
        _perfMaxPricedRows = 0;
        _perfMaxUnpricedRows = 0;
        _perfMaxMissingPriceRows = 0;
        _perfMaxNeedsGemLevelRows = 0;
        _perfMaxUnknownRows = 0;
        _perfMaxMissingVisibleRows = 0;
        _perfMaxSuppressedOcrRows = 0;
        _perfFailureSamples.Clear();
        _perfCaptureMs = 0;
        _perfRowDetectionMs = 0;
        _perfGateHashMs = 0;
        _perfOcrMs = 0;
        _perfPriceMergeMs = 0;
        _perfOverlayUpdateMs = 0;

        ScanProfile.WriteLifecycle("view-start", new Dictionary<string, object?>
        {
            ["viewId"] = _perfViewId,
            ["reason"] = reason,
            ["startedAtUtc"] = _perfViewStartedAtUtc,
            ["rowCandidates"] = detection.Rows.Count,
            ["rowConfidence"] = detection.Confidence,
            ["rowPitch"] = detection.RowPitch,
            ["panelScore"] = panelScore,
            ["signature"] = ShortSignature(signature)
        });
    }

    private void AddPerformanceCycleMetrics(
        ScanProfile profile,
        string action,
        RuneshapeRowDetection detection,
        double panelScore,
        bool? changed,
        bool cacheHit = false,
        bool failedBackoff = false)
    {
        if (!_perfViewActive)
            return;

        _perfCycles++;
        if (changed == true) _perfChangedCycles++;
        if (cacheHit) _perfCacheSkips++;
        if (failedBackoff) _perfFailedBackoffs++;
        _perfMaxRowCandidates = Math.Max(_perfMaxRowCandidates, detection.Rows.Count);
        AddPerformanceDurations(profile);
        AddPerformanceProfileMetrics(profile, action, detection, panelScore);
    }

    private void AddPerformanceOcrMetrics(
        ScanProfile profile,
        RuneshapeRowDetection detection,
        double panelScore,
        bool? changed,
        ScanOnceResult result)
    {
        if (!_perfViewActive)
            return;

        var now = Stopwatch.GetTimestamp();
        _perfCycles++;
        _perfOcrAttempts++;
        if (changed == true) _perfChangedCycles++;
        if (result.HasLoadingRows) _perfLoadingCycles++;
        if (result.UsedFallback) _perfFallbacks++;
        _perfFirstOcrTick ??= now;
        if (result.DisplayRows > 0) _perfFirstDisplayTick ??= now;
        if (result.PricedRows > 0) _perfFirstPricedTick ??= now;
        if (string.IsNullOrEmpty(_perfFirstOcrSource) && !string.IsNullOrEmpty(result.OcrSource))
            _perfFirstOcrSource = result.OcrSource;
        _perfMaxRowCandidates = Math.Max(_perfMaxRowCandidates, detection.Rows.Count);
        _perfMaxDisplayRows = Math.Max(_perfMaxDisplayRows, result.DisplayRows);
        _perfMaxPricedRows = Math.Max(_perfMaxPricedRows, result.PricedRows);
        AddPerformanceDurations(profile);
        AddPerformanceProfileMetrics(profile, "ocr", detection, panelScore);
        profile.AddMetric("displayRows", result.DisplayRows);
        profile.AddMetric("loadingRows", result.HasLoadingRows);
        profile.AddMetric("usedFallback", result.UsedFallback);
        if (result.PriceMetrics is { } priceMetrics)
            AddPerformancePriceMetrics(profile, priceMetrics);
        profile.AddMetric("viewFirstOcrMs", ElapsedOrNull(_perfFirstOcrTick));
        profile.AddMetric("viewFirstDisplayMs", ElapsedOrNull(_perfFirstDisplayTick));
        profile.AddMetric("viewFirstPricedMs", ElapsedOrNull(_perfFirstPricedTick));
    }

    private void AddPerformanceDurations(ScanProfile profile)
    {
        _perfCaptureMs += profile.StageMilliseconds("capture");
        _perfRowDetectionMs += profile.StageMilliseconds("rowDetection");
        _perfGateHashMs += profile.StageMilliseconds("gateHash");
        _perfOcrMs += profile.StageMilliseconds("ocr");
        _perfPriceMergeMs += profile.StageMilliseconds("priceMerge");
        _perfOverlayUpdateMs += profile.StageMilliseconds("overlayUpdate");
    }

    private void AddPerformanceProfileMetrics(
        ScanProfile profile,
        string action,
        RuneshapeRowDetection detection,
        double panelScore)
    {
        profile.AddMetric("viewId", _perfViewId);
        profile.AddMetric("viewAction", action);
        profile.AddMetric("viewAgeMs", ElapsedMs(_perfViewStartedTick, Stopwatch.GetTimestamp()));
        profile.AddMetric("viewCycles", _perfCycles);
        profile.AddMetric("viewOcrAttempts", _perfOcrAttempts);
        profile.AddMetric("viewCacheSkips", _perfCacheSkips);
        profile.AddMetric("viewFailedBackoffs", _perfFailedBackoffs);
        profile.AddMetric("viewLoadingCycles", _perfLoadingCycles);
        profile.AddMetric("viewChangedCycles", _perfChangedCycles);
        profile.AddMetric("viewFallbacks", _perfFallbacks);
        profile.AddMetric("viewPriceFailureCycles", _perfPriceFailureCycles);
        profile.AddMetric("viewMaxUnpricedRows", _perfMaxUnpricedRows);
        profile.AddMetric("viewMaxMissingPriceRows", _perfMaxMissingPriceRows);
        profile.AddMetric("viewMaxNeedsGemLevelRows", _perfMaxNeedsGemLevelRows);
        profile.AddMetric("viewMaxUnknownRows", _perfMaxUnknownRows);
        profile.AddMetric("viewMaxMissingVisibleRows", _perfMaxMissingVisibleRows);
        profile.AddMetric("viewMaxSuppressedOcrRows", _perfMaxSuppressedOcrRows);
        if (_perfFailureSamples.Count > 0)
            profile.AddMetric("viewPriceFailureSamples", string.Join("|", _perfFailureSamples));
        profile.AddMetric("viewStartReason", _perfViewStartReason);
        profile.AddMetric("viewSignature", ShortSignature(_perfViewSignature));
        profile.AddMetric("panelScore", panelScore);
        profile.AddMetric("rowConfidence", detection.Confidence);
        profile.AddMetric("rowPitch", detection.RowPitch);
    }

    private void CompletePerformanceView(string reason, int rowCandidates, double panelScore)
    {
        if (!_perfViewActive)
            return;

        var now = Stopwatch.GetTimestamp();
        ScanProfile.WriteLifecycle("view-complete", new Dictionary<string, object?>
        {
            ["viewId"] = _perfViewId,
            ["reason"] = reason,
            ["startReason"] = _perfViewStartReason,
            ["durationMs"] = ElapsedMs(_perfViewStartedTick, now),
            ["firstOcrMs"] = ElapsedOrNull(_perfFirstOcrTick),
            ["firstDisplayMs"] = ElapsedOrNull(_perfFirstDisplayTick),
            ["firstPricedMs"] = ElapsedOrNull(_perfFirstPricedTick),
            ["cycles"] = _perfCycles,
            ["ocrAttempts"] = _perfOcrAttempts,
            ["cacheSkips"] = _perfCacheSkips,
            ["failedBackoffs"] = _perfFailedBackoffs,
            ["loadingCycles"] = _perfLoadingCycles,
            ["changedCycles"] = _perfChangedCycles,
            ["fallbacks"] = _perfFallbacks,
            ["priceFailureCycles"] = _perfPriceFailureCycles,
            ["maxRowCandidates"] = Math.Max(_perfMaxRowCandidates, rowCandidates),
            ["maxDisplayRows"] = _perfMaxDisplayRows,
            ["maxPricedRows"] = _perfMaxPricedRows,
            ["maxUnpricedRows"] = _perfMaxUnpricedRows,
            ["maxMissingPriceRows"] = _perfMaxMissingPriceRows,
            ["maxNeedsGemLevelRows"] = _perfMaxNeedsGemLevelRows,
            ["maxUnknownRows"] = _perfMaxUnknownRows,
            ["maxMissingVisibleRows"] = _perfMaxMissingVisibleRows,
            ["maxSuppressedOcrRows"] = _perfMaxSuppressedOcrRows,
            ["priceFailureSamples"] = _perfFailureSamples.Count == 0 ? null : string.Join("|", _perfFailureSamples),
            ["firstOcrSource"] = _perfFirstOcrSource,
            ["captureMs"] = _perfCaptureMs,
            ["rowDetectionMs"] = _perfRowDetectionMs,
            ["gateHashMs"] = _perfGateHashMs,
            ["ocrMs"] = _perfOcrMs,
            ["priceMergeMs"] = _perfPriceMergeMs,
            ["overlayUpdateMs"] = _perfOverlayUpdateMs,
            ["panelScore"] = panelScore,
            ["signature"] = ShortSignature(_perfViewSignature)
        });

        _perfViewActive = false;
        _perfViewSignature = "";
    }

    private double? ElapsedOrNull(long? tick) =>
        tick is { } value ? ElapsedMs(_perfViewStartedTick, value) : null;

    private static double ElapsedMs(long startTick, long endTick) =>
        (endTick - startTick) * 1000.0 / Stopwatch.Frequency;

    private static string ShortSignature(string signature)
    {
        if (signature.Length <= 18) return signature;
        return signature[..18];
    }

    private void RestoreCachedOverlayIfNeeded(Rectangle captureRegion)
    {
        if (_lastDisplayRows.Count == 0)
        {
            if (_watchPanelVisible)
                PriceOverlayManager.ForceTopmost();
            return;
        }

        PriceOverlayManager.EnsureVisible(captureRegion, _config.OverlayXOffset, _icons);
        PriceOverlayManager.UpdateState(_lastDisplayRows, true, false);
        PriceOverlayManager.ForceTopmost();
        _watchPanelVisible = true;
    }

    private void LogWatchState(string state)
    {
        if (state == _lastWatchState) return;
        _lastWatchState = state;
        Log(state);
        App.LogApp(state);
    }

    private bool ShouldWriteWaitingProfile()
    {
        var now = DateTime.UtcNow;
        if (now - _lastWaitingProfileAtUtc < TimeSpan.FromMilliseconds(WaitingProfileLogIntervalMs))
            return false;

        _lastWaitingProfileAtUtc = now;
        return true;
    }

    private void EnterWaitingForPoe(string state, int intervalMs = ForegroundPollIntervalMs, bool preserveReadCache = false)
    {
        _nextWatchIntervalMs = intervalMs;
        if (_watchPanelVisible || state != _lastWatchState)
            PriceOverlayManager.HideNow();
        _watchPanelVisible = false;
        if (!preserveReadCache)
        {
            ResetReadState();
            _lastWatchHash = null;
            _lastOcrHash = null;
            _lastFailedOcrHash = null;
            _lastOcrPriceFetchedAt = null;
        }
        _closedGateTicks = 0;
        LogWatchState(state);
    }

    private OcrScanner GetScanner(string tessdataDir)
    {
        if (_scanner is not null && _scannerTessdataDir == tessdataDir)
            return _scanner;

        _scanner?.Dispose();
        _scanner = new OcrScanner(tessdataDir, Log, App.DebugMode);
        _scannerTessdataDir = tessdataDir;
        return _scanner;
    }

    private static bool LooksLikeRuneshapePanel(Bitmap bmp, out double score)
    {
        var samples = 0;
        var parchment = 0;
        var stepX = Math.Max(8, bmp.Width / 64);
        var stepY = Math.Max(8, bmp.Height / 64);

        for (var y = 0; y < bmp.Height; y += stepY)
        {
            for (var x = 0; x < bmp.Width; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                samples++;

                var warm = c.R >= 85 && c.R <= 230 &&
                           c.G >= 75 && c.G <= 215 &&
                           c.B >= 45 && c.B <= 185 &&
                           c.R >= c.B + 10 &&
                           Math.Abs(c.R - c.G) <= 70;
                var notTooDark = (c.R + c.G + c.B) / 3 >= 70;
                if (warm && notTooDark)
                    parchment++;
            }
        }

        score = samples == 0 ? 0 : (double)parchment / samples;
        return score >= 0.24;
    }

    private static string CaptureHash(Bitmap bmp)
    {
        const int cellsX = 16;
        const int cellsY = 4;
        Span<byte> bytes = stackalloc byte[cellsX * cellsY];
        var index = 0;

        for (var cy = 0; cy < cellsY; cy++)
        {
            for (var cx = 0; cx < cellsX; cx++)
            {
                var left = cx * bmp.Width / cellsX;
                var top = cy * bmp.Height / cellsY;
                var right = Math.Max(left + 1, (cx + 1) * bmp.Width / cellsX);
                var bottom = Math.Max(top + 1, (cy + 1) * bmp.Height / cellsY);
                long sum = 0;
                var count = 0;

                var stepX = Math.Max(1, (right - left) / 4);
                var stepY = Math.Max(1, (bottom - top) / 4);
                for (var y = top; y < bottom; y += stepY)
                {
                    for (var x = left; x < right; x += stepX)
                    {
                        var c = bmp.GetPixel(x, y);
                        sum += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                        count++;
                    }
                }
                bytes[index++] = (byte)(sum / Math.Max(1, count));
            }
        }

        return Convert.ToHexString(bytes);
    }

    internal static string PanelSignatureForTests(Bitmap bmp, RuneshapeRowDetection detection) =>
        PanelSignature(bmp, detection);

    private static string PanelSignature(Bitmap bmp, RuneshapeRowDetection detection)
    {
        if (detection.Rows.Count == 0)
            return "capture:" + CaptureHash(bmp);

        var left = Math.Clamp((int)(bmp.Width * OcrScanner.IconColumnFraction), 0, Math.Max(0, bmp.Width - 1));
        var rightTrim = Math.Clamp((int)(bmp.Width * OcrScanner.RightTrimFraction), 0, bmp.Width - left - 1);
        var right = Math.Max(left + 1, bmp.Width - rightTrim);

        var parts = new List<string> { "rows", Math.Min(255, detection.Rows.Count).ToString("X2") };
        foreach (var row in detection.Rows.Take(16))
        {
            var top = Math.Clamp(row.Top + 2, 0, Math.Max(0, bmp.Height - 1));
            var bottom = Math.Clamp(row.Bottom - 2, top + 1, bmp.Height);
            parts.Add($"{row.CenterY:X4}{(row.Bottom - row.Top):X3}{RowContentHash(bmp, left, right, top, bottom):X16}");
        }

        return string.Join(":", parts);
    }

    private static ulong RowContentHash(Bitmap bmp, int left, int right, int top, int bottom)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        void Add(byte value)
        {
            hash ^= value;
            hash *= prime;
        }

        Add((byte)Math.Clamp((right - left) / 4, 0, 255));
        Add((byte)Math.Clamp((bottom - top) / 2, 0, 255));
        var stepX = Math.Max(1, (right - left) / 96);
        var stepY = Math.Max(1, (bottom - top) / 18);
        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = left; x < right; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                var lum = (byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000);
                Add((byte)(lum / 8));
            }
        }

        return hash;
    }

    private bool TryResolveCaptureRegion(out Rectangle region, out bool autoRegion, out string diagnostic)
    {
        region = Rectangle.Empty;
        autoRegion = false;
        diagnostic = "";

        if (PoeWindowLocator.TryGetForegroundClientRect(out var clientRect, out diagnostic))
        {
            region = RuneshapeRegionProfiles.Resolve(clientRect);
            autoRegion = true;
            diagnostic = $"{diagnostic}; capture={region}";
            return region.Width > 0 && region.Height > 0;
        }

        if (string.IsNullOrWhiteSpace(diagnostic))
            diagnostic = "no PoE2 window";
        return false;
    }

    private IReadOnlyList<PriceRow> BuildPriceRows(IReadOnlyList<OcrRow> ocrRows, string ocrSource)
    {
        var rows = BuildPriceRowsCore(ocrRows, _prices.Prices, _lastPositions, (ocr, priceRow) => LogUnmatched(ocr, priceRow, ocrSource), out var newPositions);
        _lastPositions = newPositions;
        return rows;
    }

    internal static IReadOnlyList<PriceRow> BuildPriceRowsForTests(
        IReadOnlyList<OcrRow> ocrRows,
        IReadOnlyDictionary<string, PriceEntry> snapshot) =>
        BuildPriceRowsCore(ocrRows, snapshot, new Dictionary<string, int>(), null, out _);

    private static IReadOnlyList<PriceRow> BuildPriceRowsCore(
        IReadOnlyList<OcrRow> ocrRows,
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        IReadOnlyDictionary<string, int> lastPositions,
        Action<OcrRow, PriceRow>? logUnmatched,
        out Dictionary<string, int> newPositions)
    {
        var rows = new List<PriceRow>(ocrRows.Count);
        newPositions = new Dictionary<string, int>(ocrRows.Count);

        foreach (var row in ocrRows)
        {
            if (row.NormalizedName.Contains("runeshape"))
                continue;

            int stableY = row.CenterY;
            if (lastPositions.TryGetValue(row.NormalizedName, out int prevY) &&
                Math.Abs(prevY - row.CenterY) < 5)
                stableY = prevY;
            newPositions[row.NormalizedName] = stableY;

            // Uncut gems (skill / spirit / support) are priced PER LEVEL, and adjacent levels differ
            // several-fold (e.g. spirit gem L18 ≈ 0.027 div vs L19 ≈ 0.143 div). The only things that
            // distinguish one gem line from another are the TYPE word and the LEVEL number, so we pin
            // both EXACTLY and deliberately skip the prefix/fuzzy fallbacks here: a single-character OCR
            // slip on the digit (or skill↔spirit) would otherwise lock a confidently-wrong, multiples-off
            // price. If the type or level can't be read cleanly, the row shows '?' until a clean read
            // arrives — better than guessing a neighbouring level.
            if (TryResolveGemKey(row.NormalizedName, out var gemKey))
            {
                if (gemKey is not null && snapshot.TryGetValue(gemKey, out var gemEntry))
                    rows.Add(new PriceRow(stableY, row.RawText, gemEntry.DivineValue, gemEntry.ExaltedValue,
                        true, row.Multiplier, gemKey, true));
                else
                {
                    // Recognised as an uncut gem but type+level didn't pin to a known price → '?', never fuzzy.
                    var unpriced = new PriceRow(stableY, row.RawText, 0m, 0m, false, row.Multiplier, row.NormalizedName,
                        UnpricedReason: UnpricedReason.NeedsGemLevel);
                    logUnmatched?.Invoke(row, unpriced);
                    rows.Add(unpriced);
                }
                continue;
            }

            if (row.NormalizedName.Contains("random") && row.NormalizedName.Contains("currency"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, "random currency", true, MemeKind.Mirror));
                continue;
            }
            if (LooksLikeRandomUniqueReward(row.NormalizedName))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, "random unique", true, MemeKind.Headhunter));
                continue;
            }

            if (TryResolvePrice(snapshot, row.NormalizedName, out var matchedKey, out var entry, out var exact))
                rows.Add(new PriceRow(stableY, row.RawText, entry.DivineValue, entry.ExaltedValue, true, row.Multiplier, matchedKey, exact));
            else
            {
                if (!LooksLikeRewardName(row.NormalizedName))
                    continue;

                var unpriced = new PriceRow(stableY, row.RawText, 0m, 0m, false, row.Multiplier, row.NormalizedName,
                    UnpricedReason: UnpricedReason.MissingPrice);
                logUnmatched?.Invoke(row, unpriced);
                rows.Add(unpriced);
            }
        }
        return rows;
    }

    private void LogUnmatched(OcrRow source, PriceRow row, string ocrSource)
    {
        var key = $"{row.UnpricedReason}|{source.NormalizedName}";
        var now = DateTime.UtcNow;
        if (_lastUnmatchedLogByName.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromMinutes(2))
            return;

        _lastUnmatchedLogByName[key] = now;
        var line =
            $"[{DateTime.Now:HH:mm:ss.fff}] reason={row.UnpricedReason} source={ocrSource} y={source.CenterY} mult={source.Multiplier} prices={_prices.ItemCount} " +
            $"norm='{EscapeForLog(source.NormalizedName)}' raw='{EscapeForLog(source.RawText)}'";
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "unmatched_log.txt"), line + "\n"); } catch { }
    }

    private void LogFeedback(
        string source,
        string ocrSource,
        RuneshapeRowDetection rowDetection,
        IReadOnlyList<OcrRow> ocrRows,
        IReadOnlyList<PriceRow> rows,
        IReadOnlyList<PriceRow> pricedRows)
    {
        var needsFeedback =
            rowDetection.Rows.Count > ocrRows.Count ||
            rows.Count == 0 ||
            pricedRows.Count < rows.Count ||
            ocrRows.Any(row => !TryResolvePrice(_prices.Prices, row.NormalizedName, out _, out _, out _) &&
                               !TryResolveGemKey(row.NormalizedName, out _));
        if (!needsFeedback && source != "check now")
            return;

        var lines = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss.fff}] source={source} ocrSource={ocrSource} rowCandidates={rowDetection.Rows.Count} ocrRows={ocrRows.Count} displayRows={rows.Count} pricedRows={pricedRows.Count} prices={_prices.ItemCount} needs={FeedbackNeeds(rowDetection, ocrRows, rows, pricedRows)}"
        };

        if (rowDetection.Rows.Count > ocrRows.Count)
            lines.Add($"  action=ocr-missed-visible-row detail='collect support bundle; inspect capture_region.png, row_probe.png, ocr_rows.json' missingApprox={rowDetection.Rows.Count - ocrRows.Count}");

        foreach (var row in ocrRows)
        {
            var outcome = ResolveFeedbackOutcome(row, out var key);
            lines.Add($"  ocr y={row.CenterY} mult={row.Multiplier} outcome={outcome} key='{EscapeForLog(key)}' norm='{EscapeForLog(row.NormalizedName)}' raw='{EscapeForLog(row.RawText)}'");
        }

        foreach (var row in rows.Where(r => !r.HasPrice))
            lines.Add($"  display y={row.CenterY} reason={row.UnpricedReason} action='report visible item text if this should be priced' name='{EscapeForLog(row.Name)}' raw='{EscapeForLog(row.OcrText)}'");

        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "feedback_log.txt"), string.Join("\n", lines) + "\n"); } catch { }
    }

    private string ResolveFeedbackOutcome(OcrRow row, out string key)
    {
        if (TryResolveGemKey(row.NormalizedName, out var gemKey))
        {
            key = gemKey ?? "";
            return gemKey is null ? "needs-gem-level" : _prices.Prices.ContainsKey(gemKey) ? "matched-gem" : "missing-gem-price";
        }

        if (TryResolvePrice(_prices.Prices, row.NormalizedName, out var matchedKey, out _, out _))
        {
            key = matchedKey;
            return "matched";
        }

        key = "";
        return LooksLikeRewardName(row.NormalizedName) ? "missing-price-or-normalization" : "suppressed-non-reward";
    }

    private static string FeedbackNeeds(
        RuneshapeRowDetection rowDetection,
        IReadOnlyList<OcrRow> ocrRows,
        IReadOnlyList<PriceRow> rows,
        IReadOnlyList<PriceRow> pricedRows)
    {
        var needs = new List<string>();
        if (rowDetection.Rows.Count > ocrRows.Count) needs.Add("ocr-missed-visible-rows");
        if (rows.Count == 0 && ocrRows.Count > 0) needs.Add("ocr-not-classified-as-reward");
        if (pricedRows.Count < rows.Count) needs.Add("missing-price-match");
        if (needs.Count == 0) needs.Add("none");
        return string.Join(",", needs);
    }

    private static string EscapeForLog(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("'", "''");

    // Resolve the OCR'd name to a price key: exact → prefix → fuzzy → conservative unique suffix.
    // The variants are deliberately small and OCR-shaped: label stripping ("Support:") and a few
    // repeated glyph confusions from real support bundles.
    internal static bool TryResolvePrice(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        string normalizedName,
        out string matchedKey,
        out PriceEntry entry,
        out bool exact)
    {
        matchedKey = normalizedName;
        entry = default!;
        exact = false;

        var (primary, all) = PriceNameCandidates(normalizedName, snapshot);

        // A direct dictionary hit on any full-name candidate is the most trustworthy result, so try
        // it on ALL of them before any fuzzy/prefix fallback. An OCR correction turns "exalted orbl"
        // into the exact key "exalted orb"; without this the raw form fuzzy-matches first (exact=false)
        // and the row needs a second confirming read before it prices — so a freshly-seen row flashes
        // blank. Restricted to full-name candidates so a truncated anchor/tier substring (e.g. "orb of
        // augmentation" carved out of "greater orb of augmentation") can't outrank a fuzzy match to the
        // correct, more specific item.
        foreach (var candidate in primary)
        {
            if (snapshot.TryGetValue(candidate, out var directEntry))
            {
                entry = directEntry;
                matchedKey = candidate;
                exact = true;
                return true;
            }
        }

        foreach (var candidate in all)
        {
            if (snapshot.TryGetValue(candidate, out var directEntry))
            {
                entry = directEntry;
                matchedKey = candidate;
                exact = true;
                return true;
            }

            if (candidate.Length >= 10 &&
                snapshot.Keys.Where(k => k.StartsWith(candidate, StringComparison.Ordinal))
                             .MinBy(k => k.Length) is { } prefixKey)
            {
                matchedKey = prefixKey;
                entry = snapshot[prefixKey];
                return true;
            }

            if (candidate.Length >= 6 && BestFuzzy(snapshot, candidate) is { } fuzzy)
            {
                matchedKey = fuzzy;
                entry = snapshot[fuzzy];
                return true;
            }

            if (TryUniqueSuffixMatch(snapshot, candidate, out var suffixKey))
            {
                matchedKey = suffixKey;
                entry = snapshot[suffixKey];
                return true;
            }
        }

        return false;
    }

    private static (IReadOnlyList<string> Primary, IReadOnlyList<string> All) PriceNameCandidates(
        string normalizedName,
        IReadOnlyDictionary<string, PriceEntry> snapshot)
    {
        var seen = new HashSet<string>();
        void Add(List<string> list, string value)
        {
            value = Regex.Replace(value, @"\s+", " ").Trim();
            if (value.Length > 0 && seen.Add(value)) list.Add(value);
        }

        var candidates = new List<string>();
        Add(candidates, normalizedName);
        Add(candidates, StripDisplayLabels(normalizedName));

        for (var i = 0; i < candidates.Count; i++)
            Add(candidates, ApplyCommonOcrCorrections(candidates[i]));

        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var possessive in PossessivePriceKeyCandidates(candidates[i], snapshot))
                Add(candidates, possessive);
        }

        var primary = candidates.ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var anchored in RewardAnchorCandidates(candidates[i]))
                Add(candidates, anchored);
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var shorthand in TieredOrbCandidates(candidates[i]))
                Add(candidates, shorthand);
        }

        return (primary, candidates);
    }

    private static IEnumerable<string> PossessivePriceKeyCandidates(
        string name,
        IReadOnlyDictionary<string, PriceEntry> snapshot)
    {
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) yield break;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 5 || !token.EndsWith('s'))
                continue;

            var candidateTokens = tokens.ToList();
            candidateTokens[i] = token[..^1];
            candidateTokens.Insert(i + 1, "s");
            var candidate = string.Join(" ", candidateTokens);
            if (snapshot.ContainsKey(candidate))
                yield return candidate;
        }
    }

    private static string StripDisplayLabels(string normalizedName)
    {
        var s = Regex.Replace(normalizedName, @"^x(?=[a-z])", "");
        s = Regex.Replace(s, @"^(?:skill|support|spirit|lineage)(?:\s+gem)?\s+", "");
        return s.Trim();
    }

    private static IEnumerable<string> RewardAnchorCandidates(string name)
    {
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!RewardAnchorWords.Contains(tokens[i]))
                continue;

            var candidate = string.Join(" ", tokens.Skip(i));
            if (candidate.Length >= 6)
                yield return candidate;
        }
    }

    private static string ApplyCommonOcrCorrections(string name)
    {
        var replacements = new Dictionary<string, string>
        {
            ["pivine"] = "divine",
            ["diving"] = "divine",
            ["orej"] = "orb",
            ["oer"] = "orb",
            ["orh"] = "orb",
            ["orbl"] = "orb",
            ["qrp"] = "orb",
            ["qrpj"] = "orb",
            ["qrbj"] = "orb",
            ["vexalteti"] = "exalted",
            ["exalteti"] = "exalted",
            ["exaltecl"] = "exalted",
            ["ixexalted"] = "exalted",
            ["rerel"] = "regal",
            ["allo"] = "alloy",
            ["alloj"] = "alloy",
            ["aionh"] = "alloy",
            ["glgssblower"] = "glassblower",
            ["gemcjgtter"] = "gemcutter",
            ["jewelers"] = "jeweller s",
            ["jewellers"] = "jeweller s",
            ["jewellefs"] = "jeweller s",
            ["jeweler"] = "jeweller",
            ["oorb"] = "orb",
            ["spjrit"] = "spirit",
            ["geni"] = "gem",
            ["uncul"] = "uncut",
            ["biacksmith"] = "blacksmith",
            ["blacksmilh"] = "blacksmith",
            ["blacksmth"] = "blacksmith",
            ["whetstonc"] = "whetstone",
            ["whetstane"] = "whetstone",
            ["wheistone"] = "whetstone",
            ["augmentatiom"] = "augmentation",
            ["transmutatiom"] = "transmutation",
            ["transute"] = "transmutation",
            ["transmute"] = "transmutation",
            ["transmut"] = "transmutation",
            ["transmule"] = "transmutation",
            ["transmutatjon"] = "transmutation",
            ["transmulation"] = "transmutation",
            // Observed in a support bundle: "Macterwark Riine" → "Masterwork Rune". 4 edits over
            // 15 chars scores 0.75 — below the fuzzy floor — so it needs an explicit correction
            // rather than a looser threshold (which would cross-match the 100+ similar rune names).
            ["macterwark"] = "masterwork",
            ["riine"] = "rune",
            ["rnne"] = "rune"
        };

        var s = name;
        foreach (var (from, to) in replacements)
            s = Regex.Replace(s, $@"\b{Regex.Escape(from)}\b", to);
        s = Regex.Replace(s, @"\b(blacksmith|arcanist|armourer|artificer|glassblower|gemcutter|jeweller)s\b", "$1 s");
        s = Regex.Replace(s, @"\balloy\s+j\b", "alloy");
        s = Regex.Replace(s, @"\bor\s+b\b", "orb");
        s = Regex.Replace(s, @"\bblacksmiths\s+whetstone\b", "blacksmith s whetstone");
        s = Regex.Replace(s, @"\bblacksmith\s+whetstone\b", "blacksmith s whetstone");
        s = Regex.Replace(s, @"\bwhet\s+stone\b", "whetstone");
        s = Regex.Replace(s, @"\bmaster\s+work\b", "masterwork");
        return s;
    }

    private static IEnumerable<string> TieredOrbCandidates(string name)
    {
        var tiered = Regex.Match(name, @"\b(?<tier>lesser|greater|perfect)\s+(?:(?:orb)\s+)?(?:of\s+)?(?<kind>augmentation|transmutation)\b");
        if (!tiered.Success) yield break;

        yield return $"{tiered.Groups["tier"].Value} orb of {tiered.Groups["kind"].Value}";
    }

    private static bool TryUniqueSuffixMatch(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        string name,
        out string key)
    {
        key = "";
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var anchor = tokens.LastOrDefault(t => t.Length >= 6 && !SuffixStopWords.Contains(t));
        if (anchor is null) return false;

        var matches = snapshot.Keys
            .Where(k => k == anchor || k.EndsWith(" " + anchor, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        if (matches.Count != 1) return false;

        key = matches[0];
        return true;
    }

    private static readonly HashSet<string> SuffixStopWords = new(StringComparer.Ordinal)
    {
        "currency", "support", "spirit", "skill", "greater", "lesser", "ancient"
    };

    private static readonly HashSet<string> RewardAnchorWords = new(StringComparer.Ordinal)
    {
        "orb", "exalted", "divine", "chaos", "regal", "alchemy", "runic", "perfect",
        "greater", "lesser", "uncut", "support", "skill", "spirit", "lineage"
    };

    // Minimum character-similarity (1 - editDistance/maxLen) for a fuzzy price match. The percentage
    // gate is paired with a small absolute edit cap so long names cannot drift by many letters just
    // because their ratio still looks decent.
    private const double FuzzyThreshold = 0.84;

    // Closest price key to an OCR'd name by Levenshtein similarity, or null if nothing clears
    // FuzzyThreshold. Only candidates within the same small edit window are considered (cheaper, and
    // a large length gap is never a near-match).
    private static string? BestFuzzy(IReadOnlyDictionary<string, PriceEntry> snapshot, string name)
    {
        string? best = null;
        double bestScore = FuzzyThreshold;   // must strictly exceed the threshold to win
        var maxEdits = MaxFuzzyEdits(name.Length);
        foreach (var key in snapshot.Keys)
        {
            if (Math.Abs(key.Length - name.Length) > maxEdits) continue;
            int dist = Levenshtein(name, key);
            if (dist > Math.Max(maxEdits, MaxFuzzyEdits(key.Length))) continue;
            double score = 1.0 - (double)dist / Math.Max(name.Length, key.Length);
            if (score > bestScore) { bestScore = score; best = key; }
        }
        return best;
    }

    internal static bool FuzzyPriceKeyAcceptsForTests(string name, string key)
    {
        var dist = Levenshtein(name, key);
        if (Math.Abs(key.Length - name.Length) > MaxFuzzyEdits(name.Length)) return false;
        if (dist > Math.Max(MaxFuzzyEdits(name.Length), MaxFuzzyEdits(key.Length))) return false;
        var score = 1.0 - (double)dist / Math.Max(name.Length, key.Length);
        return score > FuzzyThreshold;
    }

    private static int MaxFuzzyEdits(int length) => length switch
    {
        < 10 => 1,
        < 24 => 2,
        _ => 3
    };

    // Detect an uncut gem and pin its identity. Only uncut gems take this guarded path; lineage
    // support gems such as "Ahn's Citadel" must fall through to normal matching. When the level can't
    // be read, `key` is null so the caller shows "Lv?" rather than guessing an adjacent level.
    internal static bool TryResolveGemKey(string normalizedName, out string? key)
    {
        key = null;
        var compact = Regex.Replace(normalizedName, @"[^a-z0-9]", "");
        if (!ContainsApprox(compact, "uncut", 1)) return false;

        var type = ResolveGemType(normalizedName, compact);
        if (type is null) return false;

        var lvl = Regex.Match(normalizedName, @"\b(?:level|leve[il1]?|lvl)\s*(\d{1,2})\b");
        if (lvl.Success) key = $"uncut {type} gem level {lvl.Groups[1].Value}";
        return true;
    }

    private static string? ResolveGemType(string normalizedName, string compact)
    {
        var type = Regex.Match(normalizedName, @"\b(skill|spirit|support)\b");
        if (type.Success) return type.Groups[1].Value;
        if (ContainsApprox(compact, "spirit", 1)) return "spirit";
        if (ContainsApprox(compact, "support", 1)) return "support";
        if (ContainsApprox(compact, "skill", 1)) return "skill";
        return null;
    }

    private static bool ContainsApprox(string text, string target, int maxDistance)
    {
        if (text.Contains(target, StringComparison.Ordinal)) return true;
        var min = Math.Max(1, target.Length - maxDistance);
        var max = target.Length + maxDistance;
        for (var length = min; length <= max; length++)
        {
            for (var i = 0; i <= text.Length - length; i++)
            {
                var slice = text.Substring(i, length);
                if (Levenshtein(slice, target) <= maxDistance)
                    return true;
            }
        }
        return false;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    // One display row per screen position. A slot locks onto a price once the same item name
    // is read on two consecutive passes, then stays fixed (noise can't dislodge it). Rows that
    // are still unpriced keep showing the latest attempt and get re-read every pass, so an early
    // misread no longer freezes a row — a later correct read upgrades it.
    private sealed class RowSlot
    {
        public int Y;                    // stable display position (first-seen)
        public PriceRow Latest = null!;  // most recent read (shown, as unpriced, until locked)
        public bool Locked;              // a confirmed price is pinned
        public PriceRow LockedRow = null!;
        public string? PendingName;      // candidate price name awaiting a second confirming read
        public int PendingCount;
        public string? MissingName;      // unpriced OCR candidate awaiting a second confirming read
        public int MissingCount;
        public int Unseen;               // consecutive passes this slot wasn't matched
    }

    internal IReadOnlyList<PriceRow> MergeReadsForTests(IReadOnlyList<PriceRow> reads, bool evictUnmatchedImmediately = false) =>
        MergeReads(_rowSlots, reads, evictUnmatchedImmediately);

    private IReadOnlyList<PriceRow> MergeReads(List<RowSlot> slots, IReadOnlyList<PriceRow> reads, bool evictUnmatchedImmediately)
    {
        const int Tolerance = 20;   // px: how far a read can move and still be the same row
        const int Confirm = 2;      // matching fuzzy/prefix reads before a row locks (exact: 1)
        const int EvictAfter = 3;   // passes a slot can go unmatched before it's dropped

        // Panel-switch detection: the user opened a different panel without the overlay closing.
        // Locked rows are otherwise sticky (a miss never unlocks them), so they'd keep showing the
        // previous panel's prices. If two or more locked positions now read a *different* priced
        // item, the content changed — drop the stale locks so the new panel takes over at once.
        int changedPositions = 0;
        foreach (var read in reads)
        {
            if (!read.HasPrice) continue;
            var locked = slots.FirstOrDefault(s => s.Locked && Math.Abs(s.Y - read.CenterY) <= Tolerance);
            if (locked is not null && locked.LockedRow.Name != read.Name) changedPositions++;
        }
        if (changedPositions >= 2)
        {
            Log($"panel switch detected ({changedPositions} rows changed) — resetting prices");
            slots.Clear();
        }
        else if (reads.Count > 0 && slots.Count >= reads.Count + 2)
        {
            Log($"panel row count changed {slots.Count} -> {reads.Count} — resetting stale rows");
            slots.Clear();
        }

        var matched = new HashSet<RowSlot>();
        foreach (var read in reads)
        {
            RowSlot? slot = null;
            int best = int.MaxValue;
            foreach (var s in slots)
            {
                if (matched.Contains(s)) continue;
                int d = Math.Abs(s.Y - read.CenterY);
                if (d <= Tolerance && d < best) { best = d; slot = s; }
            }
            if (slot is null)
            {
                slot = new RowSlot { Y = read.CenterY };
                slots.Add(slot);
            }
            matched.Add(slot);
            slot.Unseen = 0;
            slot.Latest = read;

            if (read.HasPrice)
            {
                if (slot.Locked && read.ExactMatch && slot.LockedRow.Name != read.Name)
                {
                    Log($"exact row change y={slot.Y} '{slot.LockedRow.Name}' -> '{read.Name}'");
                    slot.Locked = true;
                    slot.LockedRow = read with { CenterY = slot.Y };
                    slot.PendingName = null;
                    slot.PendingCount = 0;
                    continue;
                }

                if (slot.Locked && slot.LockedRow.Name != read.Name)
                {
                    if (slot.PendingName == read.Name) slot.PendingCount++;
                    else { slot.PendingName = read.Name; slot.PendingCount = 1; }

                    if (slot.PendingCount >= Confirm)
                    {
                        Log($"row lock replaced y={slot.Y} '{slot.LockedRow.Name}' -> '{read.Name}'");
                        slot.LockedRow = read with { CenterY = slot.Y };
                        slot.PendingName = null;
                        slot.PendingCount = 0;
                    }
                    continue;
                }

                if (slot.PendingName == read.Name) slot.PendingCount++;
                else { slot.PendingName = read.Name; slot.PendingCount = 1; }

                // Exact dictionary matches are trustworthy enough to lock immediately; only the
                // uncertain fuzzy/prefix matches need a second confirming read.
                int needed = read.ExactMatch ? 1 : Confirm;
                if (slot.PendingCount >= needed)
                {
                    if (!slot.Locked || slot.LockedRow.Name != read.Name)
                        Log($"locked y={slot.Y} '{read.Name}'");
                    slot.Locked = true;
                    slot.LockedRow = read with { CenterY = slot.Y };
                }
            }
            else
            {
                if (slot.Locked && read.Name != slot.LockedRow.Name)
                {
                    if (slot.MissingName == read.Name) slot.MissingCount++;
                    else { slot.MissingName = read.Name; slot.MissingCount = 1; }

                    if (slot.MissingCount >= Confirm)
                    {
                        Log($"row lock invalidated y={slot.Y} '{slot.LockedRow.Name}' -> unread '{read.Name}'");
                        slot.Locked = false;
                        slot.LockedRow = null!;
                    }
                }
                else
                {
                    slot.PendingName = null;
                    slot.PendingCount = 0;
                }

                if (read.UnpricedReason == UnpricedReason.MissingPrice)
                {
                    if (slot.MissingName == read.Name) slot.MissingCount++;
                    else { slot.MissingName = read.Name; slot.MissingCount = 1; }
                }
                else
                {
                    slot.MissingName = null;
                    slot.MissingCount = 0;
                }
            }
        }

        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (matched.Contains(slots[i])) continue;
            if (evictUnmatchedImmediately)
            {
                Log($"evicting stale row y={slots[i].Y} '{slots[i].LockedRow?.Name ?? slots[i].Latest?.Name ?? "(unknown)"}' after changed capture");
                slots.RemoveAt(i);
                continue;
            }

            if (++slots[i].Unseen > EvictAfter) slots.RemoveAt(i);
        }

        var display = new List<PriceRow>(slots.Count);
        foreach (var s in slots.OrderBy(s => s.Y))
        {
            if (s.Locked)
            {
                display.Add(s.LockedRow);
                continue;
            }

            var latest = s.Latest with { CenterY = s.Y, HasPrice = false, DivineValue = 0m, ExaltedValue = 0m };
            if (s.Latest.HasPrice || (latest.UnpricedReason == UnpricedReason.MissingPrice && s.MissingCount < Confirm))
                latest = latest with { UnpricedReason = UnpricedReason.Loading };
            display.Add(latest);
        }
        return display;
    }

    private static bool LooksLikeRewardName(string normalizedName)
    {
        if (normalizedName.Length < 5) return false;
        if (normalizedName.Contains("path of exile") ||
            normalizedName.Contains("remus autodetect") ||
            normalizedName.Contains("increased rarity") ||
            normalizedName.Contains("items dropped"))
            return false;

        var tokens = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;

        foreach (var token in tokens)
        {
            if (RewardWords.Contains(token)) return true;
        }

        return false;
    }

    private static bool LooksLikeRandomUniqueReward(string normalizedName)
    {
        return normalizedName.Contains("unique") &&
               (normalizedName.Contains("random") ||
                normalizedName.Contains("rare") ||
                normalizedName.Contains("item") ||
                normalizedName.Contains("belt"));
    }

    private static readonly HashSet<string> RewardWords = new(StringComparer.Ordinal)
    {
        "orb", "rune", "runic", "ward", "warding", "currency", "gem", "support", "skill",
        "spirit", "uncut", "jeweller", "jewellers", "jewelers", "jeweler", "essence",
        "breach", "delirium", "simulacrum", "ritual", "expedition", "verisium", "soul",
        "core", "idol", "fragment", "splinter", "shard", "distilled", "transmutation",
        "augmentation", "exalted", "divine", "chaos", "regal", "alchemy", "chance",
        "glassblower", "bauble", "gemcutter", "perfect", "greater", "lesser", "random",
        "unique", "blacksmith", "whetstone", "arcanist", "etcher", "armourer", "scrap",
        "artificer"
    };

    public void Dispose()
    {
        try
        {
            _watchCts?.Cancel();
            _watchTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        _watchCts?.Dispose();
        _scanner?.Dispose();
        _checkNowLock.Dispose();
        PriceOverlayManager.Hide();
    }
}
