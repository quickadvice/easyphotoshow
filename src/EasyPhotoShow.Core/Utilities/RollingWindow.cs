namespace EasyPhotoShow.Core.Utilities;

// Fixed-capacity FIFO that drops the oldest item when full. Used by the scan screen
// to display the last N exact-copy cards as Phase 2 results stream in. Generic so the
// queue + cap logic can be unit-tested in Core without dragging WPF view-model types
// into the test project.
//
// Not thread-safe — callers should marshal updates to a single thread (typically the
// UI thread via Dispatcher.Invoke).
public sealed class RollingWindow<T>
{
    private readonly Queue<T> _items;
    public int Capacity { get; }

    public RollingWindow(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    public int Count => _items.Count;

    // Adds an item. If the window is already at capacity, the oldest item is removed
    // and returned (caller may want to dispose/cleanup). Otherwise returns default(T).
    public T? Add(T item)
    {
        T? evicted = default;
        if (_items.Count >= Capacity)
            evicted = _items.Dequeue();
        _items.Enqueue(item);
        return evicted;
    }

    // Snapshot of the current contents in insertion order (oldest first).
    public IReadOnlyList<T> Snapshot() => _items.ToList();

    public void Clear() => _items.Clear();
}
