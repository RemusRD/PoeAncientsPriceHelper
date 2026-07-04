using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

internal sealed class AppConfig
{
    public string LeagueName { get; set; } = "Runes of Aldur";
    // Leagues offered in the control panel. The value is sent verbatim as poe.ninja's league param.
    // [JsonIgnore]: this is app-defined, not user data. Persisting it makes Newtonsoft append the saved
    // list onto this default on load (ObjectCreationHandling.Auto), duplicating every entry.
    [JsonIgnore]
    public List<string> AvailableLeagues { get; set; } =
    [
        "Runes of Aldur",
        "HC Runes of Aldur",
        "Standard",
        "Hardcore"
    ];
    public int OverlayXOffset { get; set; } = 8;
    public bool WatchEnabled { get; set; } = true;
    public bool PriceCheckCorpusEnabled { get; set; } = false;
    public bool DebugLayoutEnabled { get; set; } = false;
    public string ReferencePixelColor { get; set; } = "#000000"; // kept for JSON backwards compat, unused
    public string CustomPricesPath { get; set; } = "custom_prices.json";
}
