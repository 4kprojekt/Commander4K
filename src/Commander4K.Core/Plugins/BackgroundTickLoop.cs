namespace Commander4K.Core.Plugins;

/// <summary>
/// Calls registered plugins on a timer. UI-free: time comes from a <see cref="TimeProvider"/>, so tests drive it
/// with a fake clock. One tick per plugin at a time; a tick that outlives <see cref="TickTimeout"/> is abandoned
/// (it may ignore its token) and the next one runs on schedule. Nothing a tick throws escapes <see cref="RunAsync"/>.
/// </summary>
public sealed class BackgroundTickLoop(TimeProvider time, Func<bool> enabled)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MoreDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisabledPoll = TimeSpan.FromSeconds(1);

    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Raised when a tick fails or times out (plugin id, message); for the audit log. Single argument by convention.</summary>
    public event Action<string>? TickFailed;

    /// <param name="tick">Returns true when the plugin reported more work. Register before <see cref="RunAsync"/>.</param>
    public void Register(string pluginId, TimeSpan interval, Func<CancellationToken, Task<bool>> tick)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(pluginId, interval < MinInterval ? MinInterval : interval, tick));
        }
    }

    /// <summary>Runs the plugin's next tick as soon as the current one (if any) ends.</summary>
    public void Wake(string pluginId)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries.Where(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))];
        }

        foreach (var entry in entries)
        {
            entry.Wake();
        }
    }

    /// <summary>The loop. Ends quietly when <paramref name="stop"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
        }

        try
        {
            await Task.WhenAll(entries.Select(e => RunEntryAsync(e, stop))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunEntryAsync(Entry entry, CancellationToken stop)
    {
        var due = time.GetUtcNow();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var wait = due - time.GetUtcNow();
                if (wait > TimeSpan.Zero && !entry.TakeWake())
                {
                    await entry.WaitAsync(wait, time, stop).ConfigureAwait(false);
                    continue;
                }

                if (!enabled())
                {
                    entry.TakeWake();
                    await Task.Delay(DisabledPoll, time, stop).ConfigureAwait(false);
                    continue;
                }

                var more = await TickOnceAsync(entry, stop).ConfigureAwait(false);
                due = time.GetUtcNow() + (more ? MoreDelay : entry.Interval);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> TickOnceAsync(Entry entry, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(TickTimeout);
        Task<bool> task;
        try
        {
            task = entry.Tick(limit.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(entry, ex.Message);
            return false;
        }

        // A tick that ignores its token is abandoned: the loop stops waiting, whatever it does later is observed and dropped.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(TickTimeout, time, stop).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            limit.Cancel();
            Report(entry, "timeout");
            return false;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(entry, ex.Message);
            return false;
        }
    }

    private void Report(Entry entry, string message)
    {
        try
        {
            TickFailed?.Invoke($"{entry.PluginId}: {message}");
        }
        catch (Exception)
        {
            // A broken log sink must not stop the loop.
        }
    }

    private sealed class Entry(string pluginId, TimeSpan interval, Func<CancellationToken, Task<bool>> tick)
    {
        private readonly SemaphoreSlim _wake = new(0, 1);

        public string PluginId { get; } = pluginId;

        public TimeSpan Interval { get; } = interval;

        public Func<CancellationToken, Task<bool>> Tick { get; } = tick;

        public void Wake()
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        public bool TakeWake() => _wake.Wait(0);

        /// <summary>Waits for the delay or a wake request, whichever comes first.</summary>
        public async Task WaitAsync(TimeSpan delay, TimeProvider time, CancellationToken stop)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var timer = Task.Delay(delay, time, cancel.Token);
            var woken = _wake.WaitAsync(cancel.Token);
            var first = await Task.WhenAny(timer, woken).ConfigureAwait(false);
            await cancel.CancelAsync().ConfigureAwait(false);
            _ = first;
            try
            {
                await Task.WhenAll(timer, woken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (woken.IsCompletedSuccessfully)
            {
                // The wake token was consumed: hand it back so the loop ticks at once.
                Wake();
            }

            stop.ThrowIfCancellationRequested();
        }
    }
}
