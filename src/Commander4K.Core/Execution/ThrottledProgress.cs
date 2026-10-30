using System.Diagnostics;

namespace Commander4K.Core.Execution;

/// <summary>
/// Thread-safe <see cref="IProgress{T}"/> that forwards at most one report per interval (30 Hz by default) and keeps
/// the latest value so <see cref="Flush"/> can deliver the final state. Allocation-free for struct payloads.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1.0 / 30);

    private readonly Action<T> _handler;
    private readonly long _intervalTicks;
    private readonly Lock _gate = new();
    private long _lastForwarded;
    private T _latest = default!;
    private bool _hasUnforwarded;

    public ThrottledProgress(Action<T> handler, TimeSpan? interval = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _intervalTicks = (long)((interval ?? DefaultInterval).TotalSeconds * Stopwatch.Frequency);
        _lastForwarded = Stopwatch.GetTimestamp() - _intervalTicks;
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _latest = value;
            var now = Stopwatch.GetTimestamp();
            if (now - _lastForwarded < _intervalTicks)
            {
                _hasUnforwarded = true;
                return;
            }

            _lastForwarded = now;
            _hasUnforwarded = false;
        }

        _handler(value);
    }

    /// <summary>Forwards the latest value if it was suppressed by throttling.</summary>
    public void Flush()
    {
        T value;
        lock (_gate)
        {
            if (!_hasUnforwarded)
            {
                return;
            }

            value = _latest;
            _hasUnforwarded = false;
            _lastForwarded = Stopwatch.GetTimestamp();
        }

        _handler(value);
    }
}
