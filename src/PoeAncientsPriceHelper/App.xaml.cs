using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using SharpHook;
using SharpHook.Data;

namespace PoeAncientsPriceHelper;

public partial class App : System.Windows.Application
{
    internal static bool DebugMode { get; private set; }
    private static MainWindow? _controlPanel;
    private TaskPoolGlobalHook? _hook;
    private bool _leftCtrlDown;
    private static HotkeyModifiers _modifiers;

    // The currently-bound check hotkey, matched on every key event. MainWindow pushes the persisted
    // value once config is loaded; until then the default keeps working.
    private static HotkeyBinding _checkNowKey = HotkeyBinding.DefaultCheckNow;
    internal static void SetCheckNowKey(HotkeyBinding key) => _checkNowKey = key;

    // Single-instance guard. Held for the lifetime of the process; a second launch fails to
    // create it, focuses the already-running window, and exits. Without this, every extra launch
    // is a full second app that also receives the global debug hotkey and paints its own overlay —
    // which is how testers ended up seeing two or three calibration boxes at once.
    private static Mutex? _instanceMutex;
    private const string InstanceMutexName = @"Global\RuneshapePriceHelper.SingleInstance";

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    private static Action<HotkeyBinding, string?>? _pendingHotkeyCapture;

    internal static void BeginHotkeyCapture(Action<HotkeyBinding, string?> callback) =>
        _pendingHotkeyCapture = callback;

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
            // The form can be closing while a global hook callback arrives.
        }
    }

    // Hide the overlay immediately when the in-game panel is closed.
    private static void DismissOverlay()
    {
        PriceOverlayManager.HideNow();
        MarshalToControlPanel(form => form.DismissOverlayFromInput());
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
        //   RuneshapePriceHelper.exe --ocr-test <imagePath>
        if (e.Args.Length >= 2 && e.Args[0] == "--ocr-test")
        {
            RunOcrTest(e.Args[1]);
            Environment.Exit(0);
            return;
        }

        // Bridge-friendly live diagnostics:
        //   RuneshapePriceHelper.exe --collect-support --debug
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

        if (DebugMode) Console.WriteLine("[Debug] Runeshape Price Helper starting");

        ResetSessionLogs();
        LogApp($"startup build={BuildInfo.Display} debug={DebugMode}");

        _controlPanel = new MainWindow();
        _controlPanel.Show();

        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += (_, ev) =>
        {
            UpdateModifier(ev.Data.KeyCode, down: true);
            // ESC closes the in-game panel — hide the overlay the instant the key goes down.
            if (ev.Data.KeyCode == KeyCode.VcEscape) DismissOverlay();
            else if (ev.Data.KeyCode is KeyCode.VcLeftControl) _leftCtrlDown = true;
        };
        _hook.KeyReleased += (_, ev) =>
        {
            var code = ev.Data.KeyCode;
            // Act on release (not press) so holding a key can't auto-repeat-fire many times.
            var binding = new HotkeyBinding(code, _modifiers);
            if (_pendingHotkeyCapture is { } capture)
            {
                _pendingHotkeyCapture = null;
                string? error = null;
                if (code == KeyCode.VcEscape)
                    error = "Hotkey unchanged";
                else if (HotkeyBinding.IsReserved(binding))
                    error = "That key is reserved";
                MarshalToControlPanel(_ => capture(binding, error));
                UpdateModifier(code, down: false);
                return;
            }
            LogHotkey($"released {code} modifiers={_modifiers} binding={HotkeyBinding.Display(binding)} check={HotkeyBinding.Display(_checkNowKey)}");
            if (binding == _checkNowKey) InvokeCheckNow();
            else if (code is KeyCode.VcLeftControl) _leftCtrlDown = false;
            UpdateModifier(code, down: false);
        };
        // Left-Ctrl + left click (the in-game "purchase" gesture) also dismisses the overlay.
        _hook.MousePressed += (_, ev) =>
        {
            if (ev.Data.Button == MouseButton.Button1 && _leftCtrlDown) DismissOverlay();
        };
        _ = _hook.RunAsync().ContinueWith(
            t => LogCrash("GlobalHook.RunAsync", t.Exception?.GetBaseException() ?? t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
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

    private static void UpdateModifier(KeyCode code, bool down)
    {
        var flag = code switch
        {
            KeyCode.VcLeftControl or KeyCode.VcRightControl => HotkeyModifiers.Ctrl,
            KeyCode.VcLeftShift or KeyCode.VcRightShift => HotkeyModifiers.Shift,
            KeyCode.VcLeftAlt or KeyCode.VcRightAlt => HotkeyModifiers.Alt,
            _ => HotkeyModifiers.None,
        };
        if (flag == HotkeyModifiers.None) return;
        _modifiers = down ? _modifiers | flag : _modifiers & ~flag;
    }

    private static void InvokeCheckNow() =>
        MarshalToControlPanel(form => form.CheckNowAsync());

    private static void LogHotkey(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "hotkey_log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    private static void ResetSessionLogs()
    {
        foreach (var name in new[] { "scan_log.txt", ScanProfile.LogFileName, ScanProfile.LifecycleLogFileName, "overlay_log.txt", "hotkey_log.txt", "feedback_log.txt", "app_log.txt" })
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
        _hook?.Dispose();
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
