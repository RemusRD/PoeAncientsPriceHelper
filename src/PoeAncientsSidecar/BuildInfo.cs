namespace PoeAncientsPriceHelper;

internal static class BuildInfo
{
    public const string Channel = "not-alone-beta";
    public static string Stamp => typeof(BuildInfo).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(attr => attr.Key == "BuildStamp")
        ?.Value is { Length: > 0 } stamp
            ? stamp
            : "dev";

    public static string Display => $"{Channel} {Stamp}";
}
