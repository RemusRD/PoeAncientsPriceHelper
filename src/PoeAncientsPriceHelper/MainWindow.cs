using System.Drawing;
using System.Net.Http;
using System.Windows.Forms;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace PoeAncientsPriceHelper;

public sealed class MainWindow : Form
{
    private readonly ComboBox _leagueBox = new();
    private readonly Label _hotkeyValue = new();
    private readonly Label _priceSnapshotLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _versionLabel = new();
    private readonly Button _changeHotkeyButton = new();
    private readonly Button _checkNowButton = new();
    private readonly Button _diagnosticsButton = new();
    private readonly Button _overlayTestButton = new();

    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _oneShotEngine;
    private bool _checkingNow;
    private bool _capturingHotkey;
    private bool _loading;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;

    private static readonly IReadOnlyList<LeagueChoice> LeagueChoices =
    [
        new("Aldur SC", "Runes of Aldur"),
        new("Aldur HC", "HC Runes of Aldur"),
        new("Standard SC", "Standard"),
        new("Standard HC", "Hardcore"),
    ];

    public MainWindow()
    {
        Text = "Runeshape Price Helper";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(640, 390);
        BackColor = Color.FromArgb(17, 19, 23);
        ForeColor = Color.FromArgb(225, 231, 239);
        Font = new Font("Segoe UI", 9F);

        BuildLayout();

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null)
            _versionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build} | {BuildInfo.Display}";

        Shown += async (_, _) => await OnShownAsync();
        Resize += OnResize;
        FormClosing += OnFormClosing;
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "Runeshape Price Helper",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            Location = new Point(22, 20),
            ForeColor = Color.FromArgb(246, 248, 252),
        };
        Controls.Add(title);

        var beta = new Label
        {
            Text = "beta",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
            Location = new Point(title.Right + 12, 27),
            Padding = new Padding(8, 3, 8, 3),
            BackColor = Color.FromArgb(70, 55, 25),
            ForeColor = Color.FromArgb(255, 205, 120),
        };
        Controls.Add(beta);

        AddFieldLabel("League", 22, 76);
        _leagueBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _leagueBox.Location = new Point(140, 72);
        _leagueBox.Size = new Size(220, 25);
        _leagueBox.FlatStyle = FlatStyle.Flat;
        _leagueBox.DisplayMember = nameof(LeagueChoice.Label);
        _leagueBox.ValueMember = nameof(LeagueChoice.ApiName);
        _leagueBox.SelectedIndexChanged += async (_, _) => await LeagueChangedAsync();
        Controls.Add(_leagueBox);

        AddFieldLabel("Hotkey", 22, 116);
        _hotkeyValue.Location = new Point(140, 116);
        _hotkeyValue.Size = new Size(220, 24);
        _hotkeyValue.Font = new Font("Consolas", 10F);
        _hotkeyValue.ForeColor = Color.FromArgb(220, 228, 238);
        Controls.Add(_hotkeyValue);

        ConfigureButton(_changeHotkeyButton, "Change", new Point(370, 110), new Size(92, 30), ChangeHotkey);
        Controls.Add(_changeHotkeyButton);

        _priceSnapshotLabel.Location = new Point(22, 164);
        _priceSnapshotLabel.Size = new Size(590, 34);
        _priceSnapshotLabel.ForeColor = Color.FromArgb(235, 190, 108);
        Controls.Add(_priceSnapshotLabel);

        _statusLabel.Location = new Point(22, 204);
        _statusLabel.Size = new Size(590, 42);
        _statusLabel.ForeColor = Color.FromArgb(160, 170, 184);
        Controls.Add(_statusLabel);

        ConfigureButton(_checkNowButton, "Check prices", new Point(22, 264), new Size(178, 38), CheckNowAsync);
        _checkNowButton.BackColor = Color.FromArgb(40, 66, 104);
        _checkNowButton.ForeColor = Color.FromArgb(220, 235, 255);
        Controls.Add(_checkNowButton);

        ConfigureButton(_diagnosticsButton, "Support bundle", new Point(214, 264), new Size(150, 38), CollectDiagnosticsAsync);
        Controls.Add(_diagnosticsButton);

        ConfigureButton(_overlayTestButton, "Test overlay", new Point(378, 264), new Size(128, 38), ShowOverlayTestFromButton);
        Controls.Add(_overlayTestButton);

        _versionLabel.Location = new Point(22, 344);
        _versionLabel.Size = new Size(590, 20);
        _versionLabel.ForeColor = Color.FromArgb(100, 110, 124);
        Controls.Add(_versionLabel);
    }

    private void AddFieldLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = Color.FromArgb(140, 150, 164),
        });
    }

    private static void ConfigureButton(Button button, string text, Point location, Size size, Action action)
    {
        button.Text = text;
        button.Location = location;
        button.Size = size;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(31, 36, 44);
        button.ForeColor = Color.FromArgb(225, 231, 239);
        button.FlatAppearance.BorderColor = Color.FromArgb(58, 68, 82);
        button.Click += (_, _) => action();
    }

    private async Task OnShownAsync()
    {
        _config = ConfigStore.Load();
        PopulateFields();
        await StartupAsync();
    }

    private void PopulateFields()
    {
        _loading = true;
        _leagueBox.DataSource = LeagueChoices.ToList();
        _leagueBox.SelectedItem = ChoiceFor(_config.LeagueName);

        var checkNow = HotkeyBinding.Parse(_config.CheckNowHotkey);
        _hotkeyValue.Text = HotkeyBinding.Display(checkNow);
        App.SetCheckNowKey(checkNow);
        _loading = false;
    }

    private async Task StartupAsync()
    {
        _statusLabel.Text = "Fetching prices from poe.ninja...";
        _priceSnapshotLabel.Text = "poe.ninja cache: loading";
        _checkNowButton.Enabled = false;

        _repo?.Dispose();
        _icons?.Dispose();
        _oneShotEngine?.Dispose();
        _oneShotEngine = null;

        _repo = new PriceRepository(_http);
        _repo.PricesUpdated += OnPricesUpdated;
        _icons = new IconCache(_http);

        await Task.WhenAll(
            _repo.InitialFetchAsync(_config),
            _icons.LoadAsync());

        _repo.StartAutoRefresh(_config);
        _oneShotEngine = new ScanEngine(_config, _repo, _icons);
        if (_config.WatchEnabled)
            _oneShotEngine.StartWatcher();

        UpdateStatusLabel();
        _checkNowButton.Enabled = true;
    }

    private void OnPricesUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)UpdateStatusLabel);
    }

    private void UpdateStatusLabel()
    {
        if (_repo is null) return;
        _priceSnapshotLabel.Text = PriceSnapshotText(_repo);
        var mode = _config.WatchEnabled ? "live watch every 1s" : "hotkey-only";
        _statusLabel.Text = $"{_repo.ItemCount} items loaded | {mode}";
    }

    private static string PriceSnapshotText(PriceRepository repo)
    {
        if (repo.LastFetchedAt is not { } fetchedAt)
            return "poe.ninja cache: not fetched yet";

        var text = $"poe.ninja cache: fetched {fetchedAt:HH:mm:ss}";
        if (repo.LastPoeNinjaSnapshotAt is { } snapshotAt)
        {
            var localSnapshot = snapshotAt.ToLocalTime();
            var age = DateTimeOffset.Now - localSnapshot;
            var ageText = age.TotalMinutes >= 1
                ? $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))}m old"
                : "fresh";
            text += $" | upstream {localSnapshot:HH:mm:ss} ({ageText})";
        }
        if (!string.IsNullOrWhiteSpace(repo.LastFetchError))
            text += $" | {repo.LastFetchError}";
        return text + $" | refresh every {repo.RefreshInterval.TotalMinutes:0}m";
    }

    internal async void CheckNowAsync()
    {
        if (_checkingNow || _repo is null || _icons is null) return;

        _checkingNow = true;
        var previousStatus = _statusLabel.Text;
        _checkNowButton.Enabled = false;
        _statusLabel.Text = "Checking visible panel...";

        try
        {
            var engine = _oneShotEngine ??= new ScanEngine(_config, _repo, _icons);
            var result = await engine.CheckNowAsync();
            _statusLabel.Text = result.Success
                ? $"{result.Message} | OCR rows {result.OcrRows}"
                : $"{result.Message} | support bundle can capture details";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = previousStatus;
            MessageBox.Show(
                this,
                $"Failed to check visible panel:\n{ex.Message}",
                "Runeshape Price Helper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _checkingNow = false;
            _checkNowButton.Enabled = true;
        }
    }

    internal void DismissOverlayFromInput() => _oneShotEngine?.DismissUntilPanelCloses();

    private void ChangeHotkey()
    {
        if (_capturingHotkey) return;

        _capturingHotkey = true;
        _changeHotkeyButton.Enabled = false;
        _hotkeyValue.Text = "Press a key...";
        _statusLabel.Text = "Press the key or key combo to use for price check. Esc cancels.";

        App.BeginHotkeyCapture((binding, error) =>
        {
            _capturingHotkey = false;
            _changeHotkeyButton.Enabled = true;

            if (error is not null)
            {
                _hotkeyValue.Text = HotkeyBinding.Display(HotkeyBinding.Parse(_config.CheckNowHotkey));
                _statusLabel.Text = error;
                return;
            }

            _config.CheckNowHotkey = HotkeyBinding.ToStorage(binding);
            ConfigStore.Save(_config);
            App.SetCheckNowKey(binding);
            _hotkeyValue.Text = HotkeyBinding.Display(binding);
            _statusLabel.Text = $"Price check hotkey set to {HotkeyBinding.Display(binding)}";
        });
    }

    private async void CollectDiagnosticsAsync()
    {
        _diagnosticsButton.Enabled = false;
        var previousStatus = _statusLabel.Text;
        _statusLabel.Text = "Collecting diagnostics bundle...";
        try
        {
            var result = await DiagnosticCollector.CollectAsync(_config, _repo);
            _statusLabel.Text = $"Diagnostics saved: {result.ZipPath}";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.FolderPath)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception openEx)
            {
                Console.Error.WriteLine($"[Diagnostics] failed to open folder: {openEx.Message}");
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = previousStatus;
            MessageBox.Show(
                this,
                $"Failed to collect diagnostics:\n{ex.Message}",
                "Runeshape Price Helper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _diagnosticsButton.Enabled = true;
        }
    }

    private void ShowOverlayTestFromButton()
    {
        if (_icons is null)
        {
            _statusLabel.Text = "Overlay test needs prices/icons to finish loading first";
            return;
        }

        var diagnostic = ShowOverlayTest();
        _statusLabel.Text = $"Overlay test shown | {diagnostic}";
    }

    private string ShowOverlayTest()
    {
        var icons = _icons ?? throw new InvalidOperationException("Icons are not loaded yet.");
        var region = ResolveOverlayTestRegion(out var diagnostic);
        var rows = new[]
        {
            new PriceRow(80, "Overlay test divine", 1.23m, 0m, true, 1, "overlay test divine", true),
            new PriceRow(130, "Overlay test stack", 0.25m, 12m, true, 4, "overlay test stack", true),
        };

        PriceOverlayManager.EnsureVisible(region, _config.OverlayXOffset, icons);
        PriceOverlayManager.UpdateState(rows, panelOpen: true, reading: false);
        PriceOverlayManager.ForceTopmost();
        return diagnostic;
    }

    private static DrawingRectangle ResolveOverlayTestRegion(out string diagnostic)
    {
        if (PoeWindowLocator.TryGetForegroundClientRect(out var clientRect, out diagnostic))
        {
            var region = RuneshapeRegionProfiles.Resolve(clientRect);
            diagnostic = $"PoE2 monitor {FormsScreen.FromRectangle(region).DeviceName}";
            return region;
        }

        var screen = FormsScreen.PrimaryScreen?.Bounds ?? new DrawingRectangle(0, 0, 1920, 1080);
        var fallback = new DrawingRectangle(
            screen.Left + 50,
            screen.Top + Math.Max(120, screen.Height / 6),
            Math.Min(700, Math.Max(420, screen.Width / 3)),
            Math.Min(500, Math.Max(260, screen.Height / 2)));
        diagnostic = "PoE2 not detected; primary screen fallback";
        return fallback;
    }

    private async Task LeagueChangedAsync()
    {
        if (_loading || _leagueBox.SelectedItem is not LeagueChoice choice || choice.ApiName == _config.LeagueName)
            return;

        _config.LeagueName = choice.ApiName;
        ConfigStore.Save(_config);
        await StartupAsync();
    }

    private static LeagueChoice ChoiceFor(string apiName) =>
        LeagueChoices.FirstOrDefault(x => string.Equals(x.ApiName, apiName, StringComparison.OrdinalIgnoreCase))
        ?? LeagueChoices[0];

    private void OnResize(object? sender, EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized) return;
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
        Hide();
        if (!_trayBalloonShown)
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "Runeshape Price Helper",
                "Still running. Double-click the tray icon to restore.",
                ToolTipIcon.Info);
            _trayBalloonShown = true;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "Runeshape Price Helper",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _trayIcon?.Dispose();
        _oneShotEngine?.Dispose();
        _repo?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private sealed record LeagueChoice(string Label, string ApiName);
}
