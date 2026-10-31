using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.App.Composition;

/// <summary>
/// Runs <see cref="BackgroundTickLoop"/> on its own thread for every external plugin that is approved, declares
/// <c>contributes.background</c> and holds the <c>background</c> permission. The thread blocks on the loop; nothing
/// here touches the UI thread.
/// </summary>
public sealed class PluginBackgroundService(Func<bool> enabled, AuditLog audit)
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _stop;
    private Thread? _thread;
    private List<(ExternalPlugin Plugin, Action<string> Handler)> _wired = [];

    /// <summary>Stops the previous loop, then ticks <paramref name="plugins"/> (already filtered to approved ones).</summary>
    public void Restart(IEnumerable<ExternalPlugin> plugins)
    {
        Stop();
        var loop = new BackgroundTickLoop(TimeProvider.System, enabled);
        loop.TickFailed += message =>
        {
            var split = message.IndexOf(": ", StringComparison.Ordinal);
            audit.Record(split > 0 ? message[..split] : "background", "background.tick", split > 0 ? message[(split + 2)..] : message, AuditOutcome.Failed);
        };
        var any = false;
        foreach (var plugin in plugins)
        {
            if (plugin.Manifest.Contributes?.Background is not { } background || !plugin.Manifest.Allows(PluginPermissions.Background))
            {
                continue;
            }

            TimeSpan interval;
            try
            {
                interval = FolderConfig.ParseRefresh(background.Interval) ?? BackgroundTickLoop.MinInterval;
            }
            catch (FormatException)
            {
                continue;
            }

            loop.Register(plugin.Manifest.Id, interval, plugin.TickAsync);
            plugin.WakeRequested += loop.Wake;
            _wired.Add((plugin, loop.Wake));
            any = true;
        }

        if (!any)
        {
            return;
        }

        var stop = new CancellationTokenSource();
        _stop = stop;
        _thread = new Thread(() => Run(loop, stop.Token)) { IsBackground = true, Name = "C4K plugin background" };
        _thread.Start();
    }

    /// <summary>Cancels the loop and waits at most two seconds for the thread.</summary>
    public void Stop()
    {
        foreach (var (plugin, handler) in _wired)
        {
            plugin.WakeRequested -= handler;
        }

        _wired = [];
        var stop = _stop;
        var thread = _thread;
        _stop = null;
        _thread = null;
        if (stop is null)
        {
            return;
        }

        try
        {
            stop.Cancel();
            thread?.Join(JoinTimeout);
        }
        finally
        {
            stop.Dispose();
        }
    }

    private static void Run(BackgroundTickLoop loop, CancellationToken stop)
    {
        try
        {
            loop.RunAsync(stop).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            // Shutdown, or a tick that failed after the loop already reported it.
        }
    }
}
