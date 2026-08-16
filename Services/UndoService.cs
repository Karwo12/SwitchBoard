namespace SwitchBoard.Services;

public sealed class UndoService<T>(int capacity = 75, TimeSpan? coalescingWindow = null)
{
    private readonly List<Entry> _entries = [];
    private readonly TimeSpan _window = coalescingWindow ?? TimeSpan.FromMilliseconds(1200);

    public bool CanUndo => _entries.Count > 0;
    public int Count => _entries.Count;

    public void Record(T state, string key, bool allowCoalescing = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (allowCoalescing && _entries.Count > 0 && _entries[^1].Key == key && now - _entries[^1].RecordedAt <= _window)
        {
            _entries[^1] = _entries[^1] with { RecordedAt = now };
            return;
        }
        _entries.Add(new Entry(state, key, now));
        if (_entries.Count > capacity) _entries.RemoveAt(0);
    }

    public bool TryUndo(out T? state)
    {
        if (_entries.Count == 0) { state = default; return false; }
        var index = _entries.Count - 1;
        state = _entries[index].State;
        _entries.RemoveAt(index);
        return true;
    }

    private sealed record Entry(T State, string Key, DateTimeOffset RecordedAt);
}
