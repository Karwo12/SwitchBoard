namespace SwitchBoard.Services;

public sealed class UndoService<T>(int capacity = 75, TimeSpan? coalescingWindow = null)
{
    private readonly List<Entry> _entries = [];
    private readonly List<Entry> _redoEntries = [];
    private readonly TimeSpan _window = coalescingWindow ?? TimeSpan.FromMilliseconds(1200);

    public bool CanUndo => _entries.Count > 0;
    public bool CanRedo => _redoEntries.Count > 0;
    public int Count => _entries.Count;

    public void Record(T state, string key, bool allowCoalescing = false)
    {
        _redoEntries.Clear();
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

    public bool TryUndo(T currentState, out T? state)
    {
        if (_entries.Count == 0) { state = default; return false; }
        var index = _entries.Count - 1;
        state = _entries[index].State;
        _entries.RemoveAt(index);
        _redoEntries.Add(new Entry(currentState, _entries.Count == 0 ? "redo" : _entries[^1].Key, DateTimeOffset.UtcNow));
        return true;
    }

    public bool TryRedo(T currentState, out T? state)
    {
        if (_redoEntries.Count == 0) { state = default; return false; }
        var index = _redoEntries.Count - 1;
        state = _redoEntries[index].State;
        _redoEntries.RemoveAt(index);
        _entries.Add(new Entry(currentState, "redo", DateTimeOffset.UtcNow));
        if (_entries.Count > capacity) _entries.RemoveAt(0);
        return true;
    }

    private sealed record Entry(T State, string Key, DateTimeOffset RecordedAt);
}
