using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoeAncientsPriceHelper;

// The overlay seam: replaces the old static PriceOverlayManager. Instead of painting a WPF window,
// each call becomes a newline-delimited JSON event on stdout that the Electron shell renders.
// All stdout writes are serialized through _gate so background scan threads can't interleave JSON.
internal static class OverlayJson
{
    private static readonly object _gate = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    private static string? _lastShowKey;
    private static string? _lastVisibleStateKey;
    private static bool _isHidden = true;

    public static void WriteLine(object payload)
    {
        var line = JsonSerializer.Serialize(payload, _json);
        WriteSerializedLine(line);
    }

    private static void WriteSerializedLine(string line)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    public static void EnsureVisible(Rectangle regionRect, int xOffset)
    {
        var showKey = $"{regionRect.X},{regionRect.Y},{regionRect.Width},{regionRect.Height},{xOffset}";
        if (!_isHidden && showKey == _lastShowKey)
            return;

        _lastShowKey = showKey;
        _isHidden = false;
        WriteLine(new
        {
            @event = "overlayShow",
            region = new { x = regionRect.X, y = regionRect.Y, w = regionRect.Width, h = regionRect.Height },
            xOffset
        });
    }

    public static void UpdateState(
        IReadOnlyList<PriceRow> rows,
        bool panelOpen,
        bool reading,
        IReadOnlyList<RuneshapeRow>? debugRows = null,
        bool debugLayout = false)
    {
        var rowPayload = rows.Select(r => new
        {
            centerY = r.CenterY,
            name = r.Name,
            label = PriceLabels.BuildLabel(r),
            divineValue = r.DivineValue,
            exaltedValue = r.ExaltedValue,
            hasPrice = r.HasPrice,
            multiplier = r.Multiplier,
            meme = r.Meme,
            reason = r.UnpricedReason
        }).ToArray();
        var debugPayload = debugRows?.Select((r, i) => new
        {
            index = i + 1,
            top = r.Top,
            bottom = r.Bottom,
            centerY = r.CenterY,
            textCenterY = r.TextCenterY,
            kind = r.Kind,
            visibility = r.Visibility
        }).ToArray();

        // Keep the live overlay stable: tiny detector-only jitter should not force a repaint when
        // the priced rows the user sees have not changed.
        var visibleStateKey = JsonSerializer.Serialize(new
        {
            build = BuildInfo.Display,
            debugLayout,
            rows = rowPayload,
            panelOpen,
            reading
        }, _json);
        if (visibleStateKey == _lastVisibleStateKey)
            return;

        _lastVisibleStateKey = visibleStateKey;
        _isHidden = false;
        WriteLine(new
        {
            @event = "overlayRows",
            build = BuildInfo.Display,
            debugLayout,
            rows = rowPayload,
            debugRows = debugPayload,
            panelOpen,
            reading
        });
    }

    public static void ForceTopmost() => WriteLine(new { @event = "overlayTopmost" });

    public static void Hide()
    {
        if (_isHidden)
            return;

        ResetCacheForTests();
        WriteLine(new { @event = "overlayHide" });
    }

    public static void HideNow() => Hide();

    internal static void ResetCacheForTests()
    {
        _lastShowKey = null;
        _lastVisibleStateKey = null;
        _isHidden = true;
    }
}
