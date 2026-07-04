// Minimal shim so the relocated engine code (which referenced App.DebugMode / App.LogApp when it
// lived in the WPF host) compiles unchanged. The Electron shell owns the UI; the sidecar only needs
// these two surfaces for diagnostics.
namespace PoeAncientsPriceHelper;

internal static class App
{
    public static bool DebugMode;

    public static void LogApp(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "app_log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}
