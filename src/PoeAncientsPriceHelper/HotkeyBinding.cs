using SharpHook.Data;

namespace PoeAncientsPriceHelper;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

internal readonly record struct HotkeyBinding(KeyCode Key, HotkeyModifiers Modifiers)
{
    public static readonly HotkeyBinding Default = new(KeyCode.VcPageUp, HotkeyModifiers.None);
    public static readonly HotkeyBinding DefaultCheckNow = new(KeyCode.VcPageUp, HotkeyModifiers.None);

    public enum Action { CheckNow }

    public static readonly IReadOnlyList<KeyCode> ReservedKeys =
    [
        KeyCode.VcEscape,
    ];

    public static bool IsReserved(HotkeyBinding binding) =>
        ReservedKeys.Contains(binding.Key) || IsModifierKey(binding.Key);

    public static bool IsReserved(KeyCode key) => ReservedKeys.Contains(key) || IsModifierKey(key);

    public static bool IsModifierKey(KeyCode key) => key is
        KeyCode.VcLeftControl or KeyCode.VcRightControl or
        KeyCode.VcLeftShift or KeyCode.VcRightShift or
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt;

    public static string ToStorage(HotkeyBinding binding)
    {
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(binding.Key.ToString());
        return string.Join("+", parts);
    }

    public static HotkeyBinding Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Default;

        // Backward compatible with old config values like "VcF5".
        if (!stored.Contains('+', StringComparison.Ordinal))
            return TryParseKey(stored, out var legacyKey)
                ? new HotkeyBinding(legacyKey, HotkeyModifiers.None)
                : Default;

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        KeyCode? key = null;
        foreach (var raw in stored.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Ctrl;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                default:
                    if (TryParseKey(raw, out var parsedKey))
                        key = parsedKey;
                    else
                        return Default;
                    break;
            }
        }

        return key is { } k && !IsReserved(new HotkeyBinding(k, modifiers))
            ? new HotkeyBinding(k, modifiers)
            : Default;
    }

    public static string Display(HotkeyBinding binding)
    {
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(DisplayKey(binding.Key));
        return string.Join("+", parts);
    }

    public static string Display(KeyCode key) => Display(new HotkeyBinding(key, HotkeyModifiers.None));

    private static string DisplayKey(KeyCode key)
    {
        var name = key.ToString();
        return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
    }

    private static bool TryParseKey(string raw, out KeyCode key)
    {
        if (Enum.TryParse<KeyCode>(raw, ignoreCase: false, out key) && Enum.IsDefined(key))
            return true;
        var prefixed = raw.StartsWith("Vc", StringComparison.Ordinal) ? raw : "Vc" + raw;
        return Enum.TryParse<KeyCode>(prefixed, ignoreCase: false, out key) && Enum.IsDefined(key);
    }
}
