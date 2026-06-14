using System.Diagnostics;
using System.Globalization;

namespace PoeAncientsPriceHelper;

internal sealed class ScanProfile
{
    public const string LogFileName = "scan_profile_log.txt";
    public const string LifecycleLogFileName = "scan_lifecycle_log.txt";

    private const long MaxLogBytes = 64 * 1024;
    private static readonly object FileLock = new();

    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _durations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _metrics = new(StringComparer.Ordinal);
    private readonly string _source;

    private ScanProfile(string source) => _source = source;

    public static ScanProfile Start(string source) => new(source);

    public double TotalMilliseconds => _total.Elapsed.TotalMilliseconds;

    public double StageMilliseconds(string stage)
    {
        _durations.TryGetValue(stage, out var milliseconds);
        return milliseconds;
    }

    public void AddMetric(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key) || value is null) return;
        _metrics[key] = value;
    }

    public T Measure<T>(string stage, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            Add(stage, sw.Elapsed.TotalMilliseconds);
        }
    }

    public void Measure(string stage, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            Add(stage, sw.Elapsed.TotalMilliseconds);
        }
    }

    public void Write(
        string status,
        bool autoRegion,
        int rowCandidates,
        bool? changed,
        int ocrRows,
        int pricedRows,
        string ocrSource = "",
        string activity = "",
        int intervalMs = 0)
    {
        _total.Stop();
        var parts = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss.fff}]",
            $"source={_source}",
            $"status={status}",
            $"auto={autoRegion}",
            $"rowCandidates={rowCandidates}"
        };
        if (changed is not null)
            parts.Add($"changed={changed.Value}");
        if (!string.IsNullOrWhiteSpace(ocrSource))
            parts.Add($"ocrSource={ocrSource}");
        if (!string.IsNullOrWhiteSpace(activity))
            parts.Add($"activity=\"{activity}\"");
        if (intervalMs > 0)
            parts.Add($"intervalMs={intervalMs}");
        foreach (var (key, value) in _metrics)
        {
            if (value is null) continue;
            parts.Add($"{key}={FormatValue(value)}");
        }

        parts.Add($"ocrRows={ocrRows}");
        parts.Add($"pricedRows={pricedRows}");
        AddStage(parts, "capture");
        AddStage(parts, "rowDetection");
        AddStage(parts, "gateHash");
        AddStage(parts, "ocr");
        AddStage(parts, "priceMerge");
        AddStage(parts, "overlayUpdate");
        parts.Add($"totalMs={Format(_total.Elapsed.TotalMilliseconds)}");

        AppendLine(LogFileName, string.Join(" ", parts));
    }

    public static void WriteLifecycle(string eventName, IReadOnlyDictionary<string, object?> metrics)
    {
        var parts = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss.fff}]",
            $"event={eventName}"
        };
        foreach (var (key, value) in metrics)
        {
            if (value is null) continue;
            parts.Add($"{key}={FormatValue(value)}");
        }

        AppendLine(LifecycleLogFileName, string.Join(" ", parts));
    }

    private static void AppendLine(string fileName, string line)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        lock (FileLock)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                    File.WriteAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {fileName} truncated\n");
                File.AppendAllText(path, line + "\n");
            }
            catch
            {
                // Profiling must never disturb the scan loop.
            }
        }
    }

    private void Add(string stage, double milliseconds)
    {
        _durations.TryGetValue(stage, out var previous);
        _durations[stage] = previous + milliseconds;
    }

    private void AddStage(List<string> parts, string stage)
    {
        _durations.TryGetValue(stage, out var milliseconds);
        parts.Add($"{stage}Ms={Format(milliseconds)}");
    }

    private static string Format(double milliseconds) => milliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatValue(object value) => value switch
    {
        double d => d.ToString("0.0", CultureInfo.InvariantCulture),
        float f => f.ToString("0.0", CultureInfo.InvariantCulture),
        decimal d => d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        _ => QuoteIfNeeded(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
