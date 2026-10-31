using System.Collections.Concurrent;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Dashboard;

/// <summary>What a card shows now.</summary>
/// <param name="Card">The last card read successfully (kept when a later read fails).</param>
/// <param name="Error">Why the last read failed; <c>null</c> after a success.</param>
public sealed record CardState(int Index, PreviewCard? Card, bool Loading, string? Error);

/// <summary>
/// Reads the cards of a visible dashboard: each on its own, at most a few at a time, each read limited in time, and
/// again on its own interval. A read that hangs (a dead SSH host, an API that never answers, a hung share) only turns
/// its own card into an error; nothing waits for it. <see cref="StopAsync"/> ends everything at once.
/// </summary>
public sealed class CardRefreshLoop : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, Slot> _slots = [];
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private volatile bool _stopped;

    public CardRefreshLoop(int maxConcurrent = 4, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        _gate = new SemaphoreSlim(maxConcurrent);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A card started or finished reading. Raised on a pool thread; the UI marshals.</summary>
    public event Action<CardState>? Updated;

    public string TimeoutMessage => Loc.T("Przekroczono czas ({0} s).", (int)_timeout.TotalSeconds);

    /// <param name="refresh">How often to read again; <c>null</c> = once (and on <see cref="RefreshNow"/>).</param>
    public void Add(int index, Func<PreviewCard?, CancellationToken, Task<PreviewCard>> load, TimeSpan? refresh) =>
        _slots[index] = new Slot(index, load, refresh);

    public void Start()
    {
        foreach (var slot in _slots.Values)
        {
            Track(LoadAsync(slot));
            if (slot.Refresh is { } interval)
            {
                Track(TickAsync(slot, interval));
            }
        }
    }

    /// <summary>Reads the card again now; ignored while it is already reading.</summary>
    public void RefreshNow(int index)
    {
        if (!_stopped && _slots.TryGetValue(index, out var slot))
        {
            Track(LoadAsync(slot));
        }
    }

    /// <summary>Cancels every read and timer; no <see cref="Updated"/> is raised once this returns.</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_running.Keys).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _gate.Dispose();
    }

    private void Track(Task task)
    {
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task TickAsync(Slot slot, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref slot.Loading) == 0)
                {
                    Track(LoadAsync(slot));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync(Slot slot)
    {
        if (_stopped || Interlocked.CompareExchange(ref slot.Loading, 1, 0) != 0)
        {
            return;
        }

        // A read abandoned after its timeout may still hold a thread (a hung share, a blocked socket): no new read
        // starts until it ends, so a short refresh cannot pile up stuck reads.
        if (slot.Outstanding is { IsCompleted: false })
        {
            Volatile.Write(ref slot.Loading, 0);
            return;
        }

        try
        {
            Raise(slot, loading: true);
            try
            {
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(_timeout);
                var read = Task.Run(() => ReadAsync(slot.Load, slot.Card, limit.Token), CancellationToken.None);
                slot.Outstanding = read;
                Outcome outcome;
                try
                {
                    // WaitAsync abandons a read that ignores its token instead of waiting for it.
                    outcome = await read.WaitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = new Outcome(null, _stop.IsCancellationRequested ? null : TimeoutMessage);
                }

                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                if (outcome.Card is { } card)
                {
                    slot.Card = card;
                    slot.Error = null;
                }
                else
                {
                    slot.Error = outcome.Error ?? TimeoutMessage;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref slot.Loading, 0);
            Raise(slot, loading: false);
        }
    }

    /// <summary>Every failure of a read ends here, inside our code, so none leaves the pool lambda.</summary>
    private static async Task<Outcome> ReadAsync(Func<PreviewCard?, CancellationToken, Task<PreviewCard>> load, PreviewCard? previous, CancellationToken cancellationToken)
    {
        try
        {
            return new Outcome(await load(previous, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(null, null);
        }
        catch (Exception ex)
        {
            return new Outcome(null, ex.Message);
        }
    }

    private void Raise(Slot slot, bool loading)
    {
        if (!_stopped)
        {
            Updated?.Invoke(new CardState(slot.Index, slot.Card, loading, slot.Error));
        }
    }

    private readonly record struct Outcome(PreviewCard? Card, string? Error);

    private sealed class Slot(int index, Func<PreviewCard?, CancellationToken, Task<PreviewCard>> load, TimeSpan? refresh)
    {
        public int Loading;

        public int Index { get; } = index;

        public Func<PreviewCard?, CancellationToken, Task<PreviewCard>> Load { get; } = load;

        public TimeSpan? Refresh { get; } = refresh;

        public PreviewCard? Card { get; set; }

        public string? Error { get; set; }

        /// <summary>The last read, which may outlive its timeout.</summary>
        public Task? Outstanding { get; set; }
    }
}
