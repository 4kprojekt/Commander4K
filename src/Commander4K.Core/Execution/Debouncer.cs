namespace Commander4K.Core.Execution;

/// <summary>Coalesces bursts of <see cref="Trigger"/> calls into one callback after a quiet period.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly Timer _timer;
    private readonly TimeSpan _delay;

    public Debouncer(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _delay = delay;
        _timer = new Timer(_ => callback(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>(Re)starts the quiet period; safe to call from any thread.</summary>
    public void Trigger()
    {
        try
        {
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; nobody is listening any more.
        }
    }

    public void Dispose() => _timer.Dispose();
}
