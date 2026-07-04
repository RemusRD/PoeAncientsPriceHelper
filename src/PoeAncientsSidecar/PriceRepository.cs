using System.Collections.ObjectModel;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PoeAncientsPriceHelper;

// DivineValue  = price in divine orbs (primaryValue from API)
// ExaltedValue = DivineValue * core.rates.exalted (computed, for display when < 1 divine)
internal sealed record PriceEntry(decimal DivineValue, decimal ExaltedValue);

internal sealed class PriceRepository : IDisposable
{
    private readonly HttpClient _http;
    private volatile IReadOnlyDictionary<string, PriceEntry> _prices =
        new ReadOnlyDictionary<string, PriceEntry>(new Dictionary<string, PriceEntry>());
    private volatile IReadOnlyDictionary<string, int> _lastTypeCounts =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    private System.Threading.Timer? _timer;
    private string? _lastFetchError;
    // Cancelled on Dispose so a fetch in flight at shutdown (or one stuck behind the HttpClient
    // timeout) is abandoned cleanly instead of running on against a disposed client.
    private readonly CancellationTokenSource _cts = new();
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(30);

    public IReadOnlyDictionary<string, PriceEntry> Prices => _prices;
    public DateTime? LastFetchedAt { get; private set; }
    public DateTimeOffset? LastPoeNinjaSnapshotAt { get; private set; }
    public string? LastFetchError => _lastFetchError;
    public int ItemCount => _prices.Count;
    public IReadOnlyDictionary<string, int> LastTypeCounts => _lastTypeCounts;
    public TimeSpan RefreshInterval => AutoRefreshInterval;

    // Raised after every successful fetch (initial + each 30-min background refresh) so the UI can
    // refresh its "last fetch" label — which otherwise stays frozen at the startup time. Fires on a
    // thread-pool thread; subscribers must marshal to the UI thread.
    public event Action? PricesUpdated;

    // UncutGems shares the exact same response shape as the others: a root items[] maps each
    // line id (e.g. "uncut-spirit-gem-19") to a display name that already carries the level
    // ("Uncut Spirit Gem (Level 19)"), which NormalizeName reduces to "uncut spirit gem level 19" —
    // the same string the OCR produces. So no special parsing is needed; matching safety (pinning
    // the gem type + level) lives in ScanEngine.BuildPriceRows.
    private static readonly string[] ExchangeTypes =
    [
        "Currency",
        "Fragments",
        "Abyss",
        "UncutGems",
        "LineageSupportGems",
        "Essences",
        "SoulCores",
        "Idols",
        "Runes",
        "Ritual",
        "Expedition",
        "Delirium",
        "Breach",
        "Verisium"
    ];
    internal static IReadOnlyList<string> ExchangeTypesForTests => ExchangeTypes;

    public PriceRepository(HttpClient http) => _http = http;

    public async Task InitialFetchAsync(AppConfig config)
    {
        await FetchAndMergeAsync(config, _cts.Token);
    }

    public void StartAutoRefresh(AppConfig config)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Task.Run(() => FetchAndMergeAsync(config, _cts.Token)),
            null, AutoRefreshInterval, AutoRefreshInterval);
    }

    private async Task FetchAndMergeAsync(AppConfig config, CancellationToken ct)
    {
        try
        {
            var dict = new Dictionary<string, PriceEntry>();
            var typeCounts = new Dictionary<string, int>();
            var snapshotTimes = new List<DateTimeOffset>();
            var successfulTypes = 0;
            var failedTypes = new List<string>();
            foreach (var type in ExchangeTypes)
            {
                var result = await FetchTypeAsync(config.LeagueName, type, ct);
                if (result.UpstreamOk)
                    successfulTypes++;
                else
                    failedTypes.Add(type);
                if (result.SnapshotAt is { } snapshotAt)
                    snapshotTimes.Add(snapshotAt);
                typeCounts[type] = result.Entries.Count;
                foreach (var (name, entry) in result.Entries)
                    dict[name] = entry;
            }
            ApplyCustomOverride(dict, config.CustomPricesPath);

            if (failedTypes.Count > 0 && _prices.Count > 0)
            {
                _lastFetchError = $"poe.ninja partial refresh failed for {string.Join(", ", failedTypes)}; keeping previous cache";
                Log(_lastFetchError);
                PricesUpdated?.Invoke();
                return;
            }

            _prices = new ReadOnlyDictionary<string, PriceEntry>(dict);
            _lastTypeCounts = new ReadOnlyDictionary<string, int>(typeCounts);
            LastFetchedAt = DateTime.Now;
            LastPoeNinjaSnapshotAt = snapshotTimes.Count > 0 ? snapshotTimes.Min() : null;
            _lastFetchError = null;
            Log($"fetch ok total={dict.Count} upstreamSnapshot={LastPoeNinjaSnapshotAt?.ToLocalTime():HH:mm:ss} types={string.Join(", ", typeCounts.Select(kv => $"{kv.Key}:{kv.Value}"))}");
            PricesUpdated?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — abandon this cycle quietly. (A timeout, by contrast, is not
            // cancellation-requested, so it falls through to the log below.)
        }
        catch (Exception ex)
        {
            Log($"fetch failed: {ex.Message}");
        }
    }

    private async Task<FetchTypeResult> FetchTypeAsync(string league, string type, CancellationToken ct)
    {
        var slug = league.Replace(" ", "").ToLowerInvariant();
        var typeSlug = type.ToLowerInvariant();
        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={type}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer",
            $"https://poe.ninja/poe2/economy/{slug}/{typeSlug}");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"{type}: HTTP {(int)resp.StatusCode}");
            return new FetchTypeResult([], null, false);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        return new FetchTypeResult(ParseResponse(json), EstimateSnapshotAt(resp), true);
    }

    // poe.ninja sits behind Cloudflare and exposes HTTP Date + Age. That is not a perfect
    // "market snapshot" timestamp, but it is the best available signal for how old the cached API
    // object was when we downloaded it. The UI labels it as the upstream cache time, not a trade time.
    private static DateTimeOffset? EstimateSnapshotAt(HttpResponseMessage resp)
    {
        if (resp.Headers.Date is not { } date)
            return null;

        return date - (resp.Headers.Age ?? TimeSpan.Zero);
    }

    // API shape (exchange/current/overview):
    //   items[]   → { id, name }             — display name lookup
    //   lines[]   → { id, primaryValue }     — price in the league's PRIMARY currency
    //   core.primary  → "divine" | "exalted" — which currency primaryValue is denominated in
    //   core.rates    → { exalted, divine, chaos } — how many of each currency equal 1 primary
    // The primary currency differs by league: Softcore prices in divines, Hardcore prices in
    // exalted (divine is too valuable there). So derive both divine- and exalted-denominated
    // values from primaryValue via the rates, rather than assuming primaryValue is divines.
    private static Dictionary<string, PriceEntry> ParseResponse(string json)
    {
        var result = new Dictionary<string, PriceEntry>();
        try
        {
            var obj = JObject.Parse(json);

            // id → display name
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj["items"] is JArray itemsArr)
                foreach (var item in itemsArr)
                {
                    var id = item["id"]?.Value<string>();
                    var name = item["name"]?.Value<string>();
                    if (id is not null && name is not null) nameMap[id] = name;
                }

            // rates[x] = how many x equal 1 unit of the primary currency. When the primary IS
            // divine/exalted, its own rate is implicitly 1 (and absent from the rates object).
            var core = obj["core"];
            var primary = core?["primary"]?.Value<string>() ?? "divine";
            var rates = core?["rates"];
            var divinePerPrimary = primary == "divine" ? 1m : rates?["divine"]?.Value<decimal>() ?? 0m;
            var exaltedPerPrimary = primary == "exalted" ? 1m : rates?["exalted"]?.Value<decimal>() ?? 1m;

            if (obj["lines"] is not JArray lines) return result;
            foreach (var line in lines)
            {
                var id = line["id"]?.Value<string>();
                if (id is null || !nameMap.TryGetValue(id, out var name)) continue;
                var primaryValue = line["primaryValue"]?.Value<decimal>() ?? 0m;
                var divineValue = primaryValue * divinePerPrimary;
                var exaltedValue = Math.Round(primaryValue * exaltedPerPrimary, 1);
                var key = NormalizeName(name);
                if (!string.IsNullOrEmpty(key))
                    result[key] = new PriceEntry(divineValue, exaltedValue);
            }
        }
        catch (Exception ex)
        {
            Log($"parse failed: {ex.Message}");
        }
        return result;
    }

    private static void ApplyCustomOverride(Dictionary<string, PriceEntry> dict, string path)
    {
        try
        {
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(fullPath)) return;
            var json = File.ReadAllText(fullPath);
            var overrides = JsonConvert.DeserializeObject<Dictionary<string, CustomPriceEntry>>(json);
            if (overrides is null) return;
            foreach (var (rawKey, entry) in overrides)
            {
                var key = NormalizeName(rawKey);
                if (!string.IsNullOrEmpty(key))
                    dict[key] = new PriceEntry(entry.DivineValue, entry.ExaltedValue);
            }
        }
        catch (Exception ex)
        {
            Log($"custom override failed: {ex.Message}");
        }
    }

    internal static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        s = s.Replace("ﬁ", "fi").Replace("ﬂ", "fl").Replace('_', ' ');
        s = Regex.Replace(s, @"[^\w\s]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "price_log.txt"), line + "\n"); } catch { }
        if (App.DebugMode) Console.Error.WriteLine($"[PriceRepository] {message}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
        _cts.Dispose();
    }

    private sealed class CustomPriceEntry
    {
        public decimal DivineValue { get; set; }
        public decimal ExaltedValue { get; set; }
    }

    private sealed record FetchTypeResult(Dictionary<string, PriceEntry> Entries, DateTimeOffset? SnapshotAt, bool UpstreamOk);
}
