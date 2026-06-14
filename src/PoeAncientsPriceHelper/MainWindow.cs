using System.Drawing;
using System.Net.Http;
using System.Windows.Forms;

namespace PoeAncientsPriceHelper;

public sealed class MainWindow : Form
{
    private readonly ComboBox _leagueBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _versionLabel = new();

    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _oneShotEngine;
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
        Text = "Not Alone, Exile";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(360, 170);
        BackColor = Color.FromArgb(13, 15, 18);
        ForeColor = Color.FromArgb(232, 226, 210);
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
            Text = "Not Alone, Exile",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
            Location = new Point(22, 20),
            ForeColor = Color.FromArgb(242, 226, 188),
        };
        Controls.Add(title);

        var beta = new Label
        {
            Text = "beta",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
            Location = new Point(252, 25),
            Padding = new Padding(8, 3, 8, 3),
            BackColor = Color.FromArgb(58, 42, 22),
            ForeColor = Color.FromArgb(255, 205, 120),
        };
        Controls.Add(beta);

        AddFieldLabel("League", 24, 76);
        _leagueBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _leagueBox.Location = new Point(92, 72);
        _leagueBox.Size = new Size(230, 25);
        _leagueBox.FlatStyle = FlatStyle.Flat;
        _leagueBox.DisplayMember = nameof(LeagueChoice.Label);
        _leagueBox.ValueMember = nameof(LeagueChoice.ApiName);
        _leagueBox.SelectedIndexChanged += async (_, _) => await LeagueChangedAsync();
        Controls.Add(_leagueBox);

        _statusLabel.Location = new Point(24, 114);
        _statusLabel.Size = new Size(312, 20);
        _statusLabel.ForeColor = Color.FromArgb(139, 147, 156);
        Controls.Add(_statusLabel);

        _versionLabel.Location = new Point(24, 140);
        _versionLabel.Size = new Size(312, 18);
        _versionLabel.ForeColor = Color.FromArgb(83, 91, 101);
        Controls.Add(_versionLabel);
    }

    private void AddFieldLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = Color.FromArgb(147, 134, 112),
        });
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
        _loading = false;
    }

    private async Task StartupAsync()
    {
        _statusLabel.Text = "Preparing price cache...";

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
    }

    private void OnPricesUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)UpdateStatusLabel);
    }

    private void UpdateStatusLabel()
    {
        if (_repo is null) return;
        _statusLabel.Text = _repo.LastFetchError is { Length: > 0 } error
            ? error
            : $"{_repo.ItemCount} prices cached. Watching PoE 2.";
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
                "Not Alone, Exile",
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
            Text = "Not Alone, Exile",
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
