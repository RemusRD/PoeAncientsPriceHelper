using System.Diagnostics;
using System.Globalization;

namespace PoeAncientsPriceHelper;

// Lightweight background CPU sampler for P0 baseline measurement. Samples the current process's
// total processor time once per second and appends a line to cpu_log.txt, tagged with whether the
// Runeshape panel is currently visible. Sampling TotalProcessorTime once a second is effectively
// free; this is the cheapest way to get an idle-vs-active CPU baseline alongside the game.
internal sealed class CpuSampler : IDisposable
{
    public const string LogFileName = "cpu_log.txt";

    private readonly Func<bool> _panelVisible;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private TimeSpan _lastCpuTime;
    private long _lastTick;
    private bool _hasLast;

    public CpuSampler(Func<bool> panelVisible)
    {
        _panelVisible = panelVisible;
        try
        {
            _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            _lastTick = Stopwatch.GetTimestamp();
            _hasLast = true;
        }
        catch { }
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                Sample();
        }
        catch (OperationCanceledException) { }
        catch { /* sampling must never disturb the app */ }
    }

    private void Sample()
    {
        try
        {
            var cpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            var tick = Stopwatch.GetTimestamp();
            double cpuPctMachine = 0;
            double cpuPctOneCore = 0;
            if (_hasLast)
            {
                var consumedMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                var wallMs = ElapsedMs(_lastTick, tick);
                if (wallMs > 0)
                {
                    cpuPctOneCore = consumedMs / wallMs * 100.0;
                    var cores = Math.Max(1, Environment.ProcessorCount);
                    cpuPctMachine = cpuPctOneCore / cores;
                }
            }
            _lastCpuTime = cpuTime;
            _lastTick = tick;
            _hasLast = true;

            var workingSetMb = Environment.WorkingSet / (1024 * 1024);
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:HH:mm:ss.fff}] panelVisible={1} cpuPctMachine={2:0.000} cpuPctOneCore={3:0.0} workingSetMb={4}",
                DateTime.Now,
                _panelVisible() ? "True" : "False",
                cpuPctMachine,
                cpuPctOneCore,
                workingSetMb);
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, LogFileName), line + "\n");
        }
        catch { }
    }

    private static double ElapsedMs(long startTick, long endTick) =>
        (endTick - startTick) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _task.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }
}
