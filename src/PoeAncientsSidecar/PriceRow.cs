namespace PoeAncientsPriceHelper;

internal enum MemeKind { None, Mirror, Headhunter }
internal enum UnpricedReason { Unknown, Loading, MissingPrice, NeedsGemLevel }

internal sealed record PriceRow(
    int CenterY,
    string OcrText,
    decimal DivineValue,
    decimal ExaltedValue,
    bool HasPrice,
    int Multiplier = 1,
    string Name = "",
    bool ExactMatch = false,
    MemeKind Meme = MemeKind.None,
    UnpricedReason UnpricedReason = UnpricedReason.Unknown);
