using System.Globalization;

namespace PoeAncientsPriceHelper;

internal static class PriceLabels
{
    internal static string BuildLabel(PriceRow row)
    {
        if (row.Meme == MemeKind.Mirror)
        {
            var count = Math.Max(1, row.Multiplier);
            return count == 1 ? "1 Mirror" : $"{count} Mirrors";
        }
        if (row.Meme == MemeKind.Headhunter) return "HH/Mageblood";

        var mult = Math.Max(1, row.Multiplier);
        var useDivine = row.DivineValue >= 1.0m;
        var unit = useDivine ? row.DivineValue : row.ExaltedValue;
        var total = unit * mult;
        var suffix = useDivine ? "d" : "ex";
        var totalText = $"{FormatAmount(total)}{suffix}";
        if (mult <= 1)
            return totalText;

        return $"{totalText} ({FormatAmount(unit)}{suffix} ea)";
    }

    internal static string FormatAmount(decimal value)
    {
        if (value <= 0m) return "0";

        var format = value >= 100m ? "0"
            : value >= 10m ? "0.#"
            : value >= 1m ? "0.##"
            : "0.###";
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        return text == "0" ? "<0.001" : text;
    }
}
