using System.Drawing;

namespace PoeAncientsPriceHelper;

internal sealed record RuneshapeRegionProfile(int OffsetX, int OffsetY, int Width, int Height);

internal static class RuneshapeRegionProfiles
{
    private static readonly Dictionary<(int Width, int Height), RuneshapeRegionProfile> Profiles = new()
    {
        [(1600, 900)] = new(43, 128, 414, 447),
        [(1920, 1080)] = new(52, 154, 497, 536),
        [(2560, 1440)] = new(69, 205, 663, 715),
        [(3440, 1440)] = new(69, 205, 663, 715),
        [(3840, 2160)] = new(104, 308, 994, 1072),
    };

    public static Rectangle Resolve(Rectangle clientRect)
    {
        var profile = ResolveProfile(clientRect.Width, clientRect.Height);
        var region = new Rectangle(
            clientRect.Left + profile.OffsetX,
            clientRect.Top + profile.OffsetY,
            profile.Width,
            profile.Height);

        return Rectangle.Intersect(clientRect, region);
    }

    internal static RuneshapeRegionProfile ResolveProfile(int width, int height)
    {
        if (Profiles.TryGetValue((width, height), out var exact))
            return exact;

        var targetPixels = (long)width * height;
        var ordered = Profiles
            .Select(kvp => new
            {
                Size = kvp.Key,
                Profile = kvp.Value,
                Pixels = (long)kvp.Key.Width * kvp.Key.Height,
            })
            .OrderBy(p => p.Pixels)
            .ToList();

        var lower = ordered.LastOrDefault(p => p.Pixels <= targetPixels);
        var upper = ordered.FirstOrDefault(p => p.Pixels >= targetPixels);

        if (lower is null) return upper!.Profile;
        if (upper is null || lower.Size == upper.Size) return lower.Profile;

        double tx = upper.Size.Width == lower.Size.Width
            ? 0
            : (double)(width - lower.Size.Width) / (upper.Size.Width - lower.Size.Width);
        double ty = upper.Size.Height == lower.Size.Height
            ? 0
            : (double)(height - lower.Size.Height) / (upper.Size.Height - lower.Size.Height);
        tx = Math.Clamp(tx, 0, 1);
        ty = Math.Clamp(ty, 0, 1);

        return new RuneshapeRegionProfile(
            Lerp(lower.Profile.OffsetX, upper.Profile.OffsetX, tx),
            Lerp(lower.Profile.OffsetY, upper.Profile.OffsetY, ty),
            Lerp(lower.Profile.Width, upper.Profile.Width, tx),
            Lerp(lower.Profile.Height, upper.Profile.Height, ty));
    }

    private static int Lerp(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);
}
