using System.Text.Json;
using PoeAncientsPriceHelper;

// Headless engine sidecar. Speaks newline-delimited JSON over stdio:
//   events (stdout): { "event": "ready" | "config" | "status" | "overlayShow" | "overlayRows" | "overlayHide" | "overlayTopmost" | "error" }
//   cmds   (stdin):  { "cmd": "setLeague" | "shutdown", ... }
// Diagnostic logs (scan_log.txt, price_log.txt, app_log.txt) are written next to the exe; the JSON
// stream on stdout is the only thing the Electron shell ever parses.

SidecarHost.Run(args);

internal sealed class SidecarHost
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private AppConfig _config;
    private PriceRepository _repo = null!;
    private ScanEngine _engine = null!;

    private SidecarHost(AppConfig config) => _config = config;

    public static void Run(string[] args)
    {
        App.DebugMode = args.Contains("--debug");
        new SidecarHost(ConfigStore.Load()).Loop().GetAwaiter().GetResult();
    }

    private async Task Loop()
    {
        OverlayJson.WriteLine(new { @event = "ready", build = BuildInfo.Display });
        EmitConfig();
        await StartAsync();

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;

                switch (cmd)
                {
                    case "setLeague":
                        var league = root.TryGetProperty("league", out var l) ? l.GetString() : null;
                        if (!string.IsNullOrEmpty(league) && league != _config.LeagueName)
                        {
                            _config.LeagueName = league;
                            ConfigStore.Save(_config);
                            await RestartAsync();
                            EmitConfig();
                        }
                        break;

                    case "setPriceCheckCorpus":
                        if (root.TryGetProperty("enabled", out var enabled))
                        {
                            _config.PriceCheckCorpusEnabled = enabled.GetBoolean();
                            ConfigStore.Save(_config);
                            EmitConfig();
                            OverlayJson.WriteLine(new
                            {
                                @event = "status",
                                text = _config.PriceCheckCorpusEnabled
                                    ? "Pricecheck corpus capture enabled."
                                    : "Pricecheck corpus capture disabled."
                            });
                        }
                        break;

                    case "setDebugLayout":
                        if (root.TryGetProperty("enabled", out var debugEnabled))
                        {
                            _config.DebugLayoutEnabled = debugEnabled.GetBoolean();
                            ConfigStore.Save(_config);
                            EmitConfig();
                            OverlayJson.WriteLine(new
                            {
                                @event = "status",
                                text = _config.DebugLayoutEnabled
                                    ? "Debug layout overlay enabled."
                                    : "Debug layout overlay disabled."
                            });
                        }
                        break;

                    case "collectDiagnostics":
                        var captureId = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                        var reason = root.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                        _ = CollectDiagnosticsAsync(captureId, reason);
                        break;

                    case "shutdown":
                        _engine?.Dispose();
                        _repo?.Dispose();
                        _http.Dispose();
                        return;
                }
            }
            catch (Exception ex)
            {
                OverlayJson.WriteLine(new { @event = "error", message = ex.Message });
            }
        }
    }

    private async Task CollectDiagnosticsAsync(string? captureId, string? reason)
    {
        OverlayJson.WriteLine(new { @event = "status", text = "Collecting bug diagnostics..." });
        try
        {
            var result = await DiagnosticCollector.CollectAsync(_config, _repo, captureId, reason);
            OverlayJson.WriteLine(new
            {
                @event = "diagnosticBundle",
                captureId,
                reason,
                folderPath = result.FolderPath,
                zipPath = result.ZipPath
            });
            OverlayJson.WriteLine(new { @event = "status", text = $"Bug diagnostics saved: {Path.GetFileName(result.ZipPath)}" });
        }
        catch (Exception ex)
        {
            OverlayJson.WriteLine(new { @event = "error", message = $"Bug diagnostics failed: {ex.Message}" });
            OverlayJson.WriteLine(new { @event = "status", text = "Bug diagnostics failed.", error = true });
        }
    }

    private async Task StartAsync()
    {
        _repo = new PriceRepository(_http);
        _repo.PricesUpdated += EmitStatus;
        _engine = new ScanEngine(_config, _repo);
        OverlayJson.WriteLine(new { @event = "status", text = "Preparing price cache..." });
        await _repo.InitialFetchAsync(_config);
        _repo.StartAutoRefresh(_config);
        if (_config.WatchEnabled) _engine.StartWatcher();
        EmitStatus();
    }

    private async Task RestartAsync()
    {
        // League changed: drop the watcher's caches and re-fetch prices against the new league.
        OverlayJson.WriteLine(new { @event = "overlayHide" });
        _engine.Dispose();
        _engine = new ScanEngine(_config, _repo);
        OverlayJson.WriteLine(new { @event = "status", text = "Preparing price cache..." });
        await _repo.InitialFetchAsync(_config);
        _repo.StartAutoRefresh(_config);
        if (_config.WatchEnabled) _engine.StartWatcher();
        EmitStatus();
    }

    private void EmitConfig() =>
        OverlayJson.WriteLine(new
        {
            @event = "config",
            league = _config.LeagueName,
            availableLeagues = _config.AvailableLeagues,
            overlayXOffset = _config.OverlayXOffset,
            priceCheckCorpusEnabled = _config.PriceCheckCorpusEnabled,
            debugLayoutEnabled = _config.DebugLayoutEnabled,
            build = BuildInfo.Display
        });

    private void EmitStatus() =>
        OverlayJson.WriteLine(new
        {
            @event = "status",
            text = _repo.LastFetchError is { Length: > 0 } e ? e : $"{_repo.ItemCount} prices cached. Watching PoE 2.",
            prices = _repo.ItemCount,
            error = _repo.LastFetchError
        });
}
