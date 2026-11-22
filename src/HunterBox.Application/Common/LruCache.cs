namespace HunterBox.Application.Common;

/// <summary>
/// 简单容量上限 LRU 缓存：命中会刷新最近使用顺序，超出容量淘汰最久未用项。
/// </summary>
/// <remarks>
/// 不是线程安全实现。跨线程读取必须由调用方加外部锁：快照方法只保证返回物化副本，
/// 不保证并发 <see cref="Set"/> 期间枚举内部链表是安全的（仍可能抛「集合已修改」）。
/// </remarks>
/// <typeparam name="TKey">键类型。</typeparam>
/// <typeparam name="TValue">值类型。</typeparam>
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = [];

    /// <summary>创建有容量上限的缓存。</summary>
    /// <param name="capacity">最大条目数，必须大于 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">容量不大于 0。</exception>
    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>全部键（枚举期间不要修改缓存）。</summary>
    public IEnumerable<TKey> Keys => _map.Keys;

    /// <summary>当前条目数（不超过容量）。</summary>
    public int Count => _map.Count;

    /// <summary>把容量上调到至少 <paramref name="capacity"/>（只增不减，已缓存项不受影响）。</summary>
    /// <param name="capacity">新的最小容量。</param>
    public void EnsureCapacity(int capacity)
    {
        if (capacity > _capacity)
        {
            _capacity = capacity;
        }
    }

    /// <summary>尝试读取；命中时把条目移到最近使用端。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <returns>命中时返回 <see langword="true"/>。</returns>
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>写入或更新；超出容量时淘汰最久未用项。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? existing))
        {
            existing.Value = (key, value);
            _order.Remove(existing);
            _order.AddFirst(existing);
            return;
        }

        LinkedListNode<(TKey Key, TValue Value)> node = _order.AddFirst((key, value));
        _map[key] = node;
        while (_map.Count > _capacity && _order.Last is not null)
        {
            LinkedListNode<(TKey Key, TValue Value)> last = _order.Last;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    /// <summary>移除指定键。</summary>
    /// <param name="key">键。</param>
    /// <returns>存在并移除时返回 <see langword="true"/>。</returns>
    public bool Remove(TKey key)
    {
        if (!_map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? node))
        {
            return false;
        }

        _map.Remove(key);
        _order.Remove(node);
        return true;
    }

    /// <summary>物化字典快照：只复制一次，供后台只读使用。</summary>
    /// <returns>键值字典副本。</returns>
    public IReadOnlyDictionary<TKey, TValue> SnapshotDictionary()
    {
        Dictionary<TKey, TValue> copy = new(_map.Count);
        foreach ((TKey key, TValue value) in _order)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>物化值快照：锁内只拷贝引用，不做逐项投影/委托调用。</summary>
    /// <returns>值数组（顺序为最近使用优先）。</returns>
    public TValue[] SnapshotValues()
    {
        TValue[] values = new TValue[_order.Count];
        int index = 0;
        foreach ((TKey Key, TValue Value) entry in _order)
        {
            values[index++] = entry.Value;
        }

        return values;
    }
}

/// <summary>LRU 缓存的批量剪枝辅助：按谓词一次收集键再移除，避免调用方重复写循环。</summary>
internal static class LruCachePruning
{
    /// <summary>移除键满足谓词的全部条目。</summary>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    /// <param name="cache">目标缓存。</param>
    /// <param name="predicate">键谓词。</param>
    /// <returns>至少移除一条时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> 或 <paramref name="predicate"/> 为 <see langword="null"/>。</exception>
    public static bool RemoveWhere<TKey, TValue>(this LruCache<TKey, TValue> cache, Func<TKey, bool> predicate)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(predicate);
        List<TKey> dead = [.. cache.Keys.Where(predicate)];
        bool removed = false;
        foreach (TKey key in dead)
        {
            removed |= cache.Remove(key);
        }

        return removed;
    }
}
