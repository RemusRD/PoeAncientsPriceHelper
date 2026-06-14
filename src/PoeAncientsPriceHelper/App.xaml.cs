using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace PoeAncientsPriceHelper;

public partial class App : System.Windows.Application
{
    internal static bool DebugMode { get; private set; }
    private static MainWindow? _controlPanel;

    // Single-instance guard. Held for the lifetime of the process; a second launch fails to
    // create it, focuses the already-running window, and exits.
    private static Mutex? _instanceMutex;
    private const string InstanceMutexName = @"Global\NotAloneExile.SingleInstance";

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    internal static void MarshalToControlPanel(Action<MainWindow> action)
    {
        var form = _controlPanel;
        if (form is null || form.IsDisposed) return;
        try
        {
            if (form.InvokeRequired) form.BeginInvoke(() => action(form));
            else action(form);
        }
        catch (InvalidOperationException)
        {
            // The form can be closing while a background callback arrives.
        }
    }

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);

    private static void AttachDebugConsole()
    {
        if (!AttachConsole(-1)) AllocConsole(); // attach to parent terminal, else open new window
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallCrashLogging();
        if (e.Args.Contains("--debug"))
        {
            DebugMode = true;
            AttachDebugConsole();
        }

        // Headless OCR repro: run the real OCR pipeline on a screenshot and print what it sees.
        //   NotAloneExile.exe --ocr-test <imagePath>
        if (e.Args.Length >= 2 && e.Args[0] == "--ocr-test")
        {
            RunOcrTest(e.Args[1]);
            Environment.Exit(0);
            return;
        }

        // Bridge-friendly live diagnostics:
        //   NotAloneExile.exe --collect-support --debug
        // Captures all monitors, probes the foreground PoE2 window, OCRs row strips, resolves rows
        // against the current poe.ninja cache, and writes diagnostics/latest-support-command.json.
        if (e.Args.Contains("--collect-support"))
        {
            RunCollectSupportCommand();
            Environment.Exit(0);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        base.OnStartup(e);

        // Refuse to start a second copy: only one instance owns the global hook + overlay.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            Shutdown();
            return;
        }

        if (DebugMode) Console.WriteLine("[Debug] Not Alone, Exile starting");

        ResetSessionLogs();
        LogApp($"startup build={BuildInfo.Display} debug={DebugMode}");

        _controlPanel = new MainWindow();
        _controlPanel.Show();
    }

    private void InstallCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            LogCrash("AppDomain.UnhandledException", ev.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ev) =>
            LogCrash("DispatcherUnhandledException", ev.Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", ev.Exception);
            ev.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var text = ex?.ToString() ?? "(non-Exception crash object)";
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash_log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{text}\n\n");
        }
        catch { }
    }

    private static void ResetSessionLogs()
    {
        foreach (var name in new[] { "scan_log.txt", ScanProfile.LogFileName, ScanProfile.LifecycleLogFileName, "overlay_log.txt", "feedback_log.txt", "app_log.txt" })
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name), ""); }
            catch { }
        }
    }

    internal static void LogApp(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "app_log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // Bring the already-running instance's window to the foreground so the user gets feedback that
    // the app is up (instead of "nothing happened, click again" — which spawned the extra copies).
    private static void FocusExistingInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch { /* best-effort focus; the guard still prevents the second instance */ }
    }

    private static void RunOcrTest(string imagePath)
    {
        var outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ocr_test.txt");
        var lines = new List<string>();
        void Out(string s) => lines.Add(s);
        try
        {
            Out($"[ocr-test] image='{imagePath}'");
            using var full = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(imagePath);
            Out($"[ocr-test] image size {full.Width}x{full.Height}");

            var rect = new System.Drawing.Rectangle(0, 0, full.Width, full.Height);
            using var region = new System.Drawing.Bitmap(rect.Width, rect.Height);
            using (var g = System.Drawing.Graphics.FromImage(region))
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height), rect, System.Drawing.GraphicsUnit.Pixel);

            var tessdata = System.IO.Path.Combine(AppContext.BaseDirectory, "tessdata");
            using var scanner = new OcrScanner(tessdata, Out, debug: true);   // --ocr-test wants the dump
            var detector = new RuneshapeRowDetector();
            var detection = detector.Detect(region);
            Out($"[ocr-test] row candidates={detection.Rows.Count} usable={detection.HasUsableRows} confidence={detection.Confidence:0.000} pitch={detection.RowPitch}");
            if (detection.Rows.Count > 0)
            {
                Out("[ocr-test] detected rows:");
                foreach (var row in detection.Rows)
                    Out($"    top={row.Top} bottom={row.Bottom} center={row.CenterY} textCenter={row.TextCenterY}");
            }

            IReadOnlyList<OcrRow> rows;
            if (detection.ShouldUseRowStrips)
            {
                rows = scanner.ScanRows(region, detection.Rows);
                Out($"[ocr-test] row-strip OCR merged {rows.Count} rows");
                if (rows.Count == 0)
                {
                    Out("[ocr-test] row-strip OCR returned 0 rows; trying full region");
                    rows = scanner.Scan(region);
                }
            }
            else
            {
                Out(detection.HasRowCandidates
                    ? "[ocr-test] row candidates rejected by gate; trying full region"
                    : "[ocr-test] no row candidates; trying full region");
                rows = scanner.Scan(region);
            }
            Out($"[ocr-test] merged {rows.Count} rows:");
            foreach (var row in rows)
                Out($"    y={row.CenterY} mult={row.Multiplier} norm='{row.NormalizedName}' raw='{row.RawText}'");
        }
        catch (Exception ex)
        {
            Out($"[ocr-test] ERROR {ex}");
        }
        try { System.IO.File.WriteAllLines(outPath, lines); } catch { }
    }

    private static void RunCollectSupportCommand()
    {
        try
        {
            LogApp($"collect-support command starting build={BuildInfo.Display} debug={DebugMode}");
            var config = ConfigStore.Load();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var prices = new PriceRepository(http);
            prices.InitialFetchAsync(config).GetAwaiter().GetResult();

            var result = DiagnosticCollector.CollectAsync(config, prices).GetAwaiter().GetResult();
            WriteCollectSupportCommandStatus(new
            {
                ok = true,
                build = BuildInfo.Display,
                collectedAt = DateTime.Now.ToString("O"),
                result.FolderPath,
                result.ZipPath,
                prices = prices.ItemCount,
                priceTypes = prices.LastTypeCounts,
            });
            LogApp($"collect-support command ok zip={result.ZipPath}");
            if (DebugMode) Console.WriteLine($"[Diagnostics] {result.ZipPath}");
        }
        catch (Exception ex)
        {
            WriteCollectSupportCommandStatus(new
            {
                ok = false,
                build = BuildInfo.Display,
                collectedAt = DateTime.Now.ToString("O"),
                error = ex.ToString(),
            });
            LogCrash("collect-support command", ex);
            if (DebugMode) Console.Error.WriteLine(ex);
        }
    }

    private static void WriteCollectSupportCommandStatus(object status)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "latest-support-command.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(status, Newtonsoft.Json.Formatting.Indented));
        }
        catch { }
    }
}
