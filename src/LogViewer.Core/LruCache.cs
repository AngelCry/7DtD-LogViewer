namespace LogViewer.Core;

/// <summary>
/// Потокобезопасный LRU-кэш: словарь для поиска и связный список для порядка
/// использования (в начале — самые свежие элементы).
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly object _gate = new();

    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_capacity == 0)
            {
                return;
            }

            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                node.Value.Value = value;
                _order.Remove(node);
                _order.AddFirst(node);
                return;
            }

            if (_map.Count >= _capacity)
            {
                LinkedListNode<Entry> last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }

            LinkedListNode<Entry> added = _order.AddFirst(new Entry(key, value));
            _map.Add(key, added);
        }
    }

    public void Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                return;
            }

            _order.Remove(node);
            _map.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    private sealed class Entry(TKey key, TValue value)
    {
        public TKey Key { get; } = key;

        public TValue Value { get; set; } = value;
    }
}
