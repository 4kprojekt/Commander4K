namespace Commander4K.Core.Folders;

/// <summary>
/// The views opened one from another (a dashboard picked on a dashboard): Back returns to the previous one, the trail
/// is the breadcrumb. It is a navigation history, not a tree, so A → B → A is kept as three steps.
/// </summary>
public sealed class ViewHistory<T>(int limit = 50)
{
    private readonly List<T> _items = [];

    public T Current => _items.Count > 0 ? _items[^1] : throw new InvalidOperationException("The history is empty.");

    /// <summary>Oldest first; the last item is <see cref="Current"/>.</summary>
    public IReadOnlyList<T> Trail => _items;

    public bool CanGoBack => _items.Count > 1;

    public void Push(T item)
    {
        _items.Add(item);
        if (_items.Count > limit)
        {
            _items.RemoveAt(0);
        }
    }

    public void ReplaceCurrent(T item)
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("The history is empty.");
        }

        _items[^1] = item;
    }

    /// <returns>The new <see cref="Current"/>.</returns>
    public T Back()
    {
        if (!CanGoBack)
        {
            throw new InvalidOperationException("There is nothing to go back to.");
        }

        _items.RemoveAt(_items.Count - 1);
        return Current;
    }

    /// <summary>A breadcrumb click: that step becomes current and the later ones are dropped.</summary>
    public T JumpTo(int trailIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trailIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(trailIndex, _items.Count);
        _items.RemoveRange(trailIndex + 1, _items.Count - trailIndex - 1);
        return Current;
    }

    public void Clear() => _items.Clear();
}
