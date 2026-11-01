namespace Commander4K.Core.Web;

/// <summary>
/// At most <c>limit</c> events per sliding window of <c>windowMilliseconds</c> (timestamps from <see cref="Environment.TickCount64"/>).
/// Not thread-safe: web preview messages arrive on the UI thread.
/// </summary>
public sealed class RateWindow(int limit, long windowMilliseconds)
{
    private readonly Queue<long> _recent = new(limit);

    /// <summary>True when one more event fits now (and counts it); false when it should be dropped.</summary>
    public bool TryTake(long now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() >= windowMilliseconds)
        {
            _recent.Dequeue();
        }

        if (_recent.Count >= limit)
        {
            return false;
        }

        _recent.Enqueue(now);
        return true;
    }
}
