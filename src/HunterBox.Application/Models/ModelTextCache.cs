using HunterBox.Application.Common;
using HunterBox.Domain.Games;

namespace HunterBox.Application.Models;

/// <summary>
/// 模型备注文本缓存（线程安全）：按「游戏 + MOD」隔离的文本、文件清单签名与根层 pak 内容戳。
/// </summary>
/// <remarks>
/// <para>UI 线程与后台预热共用同一实例；所有读写都在 <see cref="_gate"/> 内完成，持锁期间只做内存操作。</para>
/// <para>
/// 键为 <c>(gameId, modId)</c>：不同游戏各自保留条目，旧游戏的预热结果不会覆盖新游戏缓存，
/// 新游戏也不会被旧条目挡住。辅助字段全部收进缓存值，LRU 淘汰时自然清理。
/// </para>
/// <para>抽出到 Application 是为了让缓存策略与并发约束可以被单元测试覆盖。</para>
/// </remarks>
public sealed class ModelTextCache
{
    private readonly object _gate = new();

    /// <summary>(游戏稳定 ID, MOD ID) → 缓存条目。</summary>
    private readonly LruCache<(string GameId, int ModId), ModelTextCacheEntry> _entries;

    /// <summary>缓存版本：条目实际变化时递增，用于跳过无变化的写盘。</summary>
    /// <remarks>
    /// 只由 <see cref="Store"/>、<see cref="Remove(int)"/>、<see cref="DropInvalidated"/> 与 <see cref="Prune"/> 递增；
    /// <see cref="Load"/> 装载的是磁盘上已有的内容，不需要回写，因此不递增。
    /// </remarks>
    private long _version;

    /// <summary>创建缓存。</summary>
    /// <param name="initialCapacity">初始容量，必须大于 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">容量不大于 0。</exception>
    public ModelTextCache(int initialCapacity)
    {
        _entries = new LruCache<(string GameId, int ModId), ModelTextCacheEntry>(initialCapacity);
    }

    /// <summary>按 MOD 数上调容量（不超过 <see cref="ModelTextPrefetchService.MaxTextCacheCapacity"/>）。</summary>
    /// <param name="modCount">当前游戏的 MOD 数。</param>
    public void EnsureCapacity(int modCount)
    {
        lock (_gate)
        {
            _entries.EnsureCapacity(ModelTextPrefetchService.TextCacheCapacity(modCount));
        }
    }

    /// <summary>查询文本缓存；签名不同或游戏不一致时视为未命中。</summary>
    /// <param name="modId">MOD ID。</param>
    /// <param name="signature">文本签名（描述 + 文件清单摘要）。</param>
    /// <param name="gameId">MOD 所属游戏稳定 ID；为空时视为未命中。</param>
    /// <param name="text">命中的文本；未命中时为空串。</param>
    /// <returns>命中时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signature"/> 或 <paramref name="gameId"/> 为 <see langword="null"/>。</exception>
    public bool TryGet(int modId, string signature, string gameId, out string text)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(gameId);

        lock (_gate)
        {
            if (TryGetEntryLocked(modId, gameId, out ModelTextCacheEntry entry)
                && string.Equals(entry.Signature, signature, StringComparison.Ordinal))
            {
                text = entry.Text;
                return true;
            }

            text = string.Empty;
            return false;
        }
    }

    /// <summary>读取某 MOD 的根层 pak 内容戳。</summary>
    /// <param name="modId">MOD ID。</param>
    /// <param name="gameId">MOD 所属游戏稳定 ID。</param>
    /// <param name="pakStamp">命中的戳；未命中时为空串。</param>
    /// <returns>存在戳时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gameId"/> 为 <see langword="null"/>。</exception>
    public bool TryGetPakStamp(int modId, string gameId, out string pakStamp)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        lock (_gate)
        {
            if (TryGetEntryLocked(modId, gameId, out ModelTextCacheEntry entry) && entry.PakStamp.Length > 0)
            {
                pakStamp = entry.PakStamp;
                return true;
            }

            pakStamp = string.Empty;
            return false;
        }
    }

    /// <summary>批量查询文本（一次加锁）；未命中的位置写 <see langword="null"/>。</summary>
    /// <remarks>回填列表时避免逐行加锁：整表一次查询；结果写入调用方缓冲，不在查询内分配 O(N) 数组。</remarks>
    /// <param name="requests">查询项（MOD ID、签名、游戏稳定 ID）。</param>
    /// <param name="results">与请求等长的结果缓冲；未命中为 <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="requests"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="results"/> 长度与请求不一致。</exception>
    public void TryGetMany(
        IReadOnlyList<(int ModId, string Signature, string GameId)> requests,
        Span<string?> results)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (results.Length != requests.Count)
        {
            throw new ArgumentException("结果缓冲长度必须与请求数一致", nameof(results));
        }

        lock (_gate)
        {
            for (int index = 0; index < requests.Count; index++)
            {
                (int modId, string signature, string gameId) = requests[index];
                if (TryGetEntryLocked(modId, gameId, out ModelTextCacheEntry entry)
                    && string.Equals(entry.Signature, signature, StringComparison.Ordinal))
                {
                    results[index] = entry.Text;
                }
                else
                {
                    results[index] = null;
                }
            }
        }
    }

    /// <summary>锁内按（游戏, MOD）取条目；空游戏 ID 视为未命中（三处查询共用）。</summary>
    /// <param name="modId">MOD ID。</param>
    /// <param name="gameId">MOD 所属游戏稳定 ID。</param>
    /// <param name="entry">命中的条目；未命中时无意义。</param>
    /// <returns>命中时返回 <see langword="true"/>。</returns>
    private bool TryGetEntryLocked(int modId, string gameId, out ModelTextCacheEntry entry)
    {
        if (gameId.Length > 0 && _entries.TryGetValue((_normalizedGameIds.Normalize(gameId), modId), out entry))
        {
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>移除某个 MOD 在全部游戏下的缓存（MOD 删除/改写后的粗粒度失效）。</summary>
    /// <param name="modId">MOD ID。</param>
    public void Remove(int modId)
    {
        lock (_gate)
        {
            if (_entries.RemoveWhere(key => key.ModId == modId))
            {
                _version++;
            }
        }
    }

    /// <summary>丢弃后台预热发现的失效项；只丢弃指定游戏的条目。</summary>
    /// <param name="ids">失效的 MOD ID。</param>
    /// <param name="gameId">预热所属游戏稳定 ID。</param>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> 或 <paramref name="gameId"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public void DropInvalidated(IReadOnlyList<int> ids, string gameId)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        lock (_gate)
        {
            string normalizedGame = _normalizedGameIds.Normalize(gameId);
            HashSet<int> targets = [.. ids];
            if (_entries.RemoveWhere(key => string.Equals(key.GameId, normalizedGame, StringComparison.Ordinal)
                && targets.Contains(key.ModId)))
            {
                _version++;
            }
        }
    }

    /// <summary>按当前游戏存活 MOD 裁剪缓存；其它游戏的条目保留。</summary>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <param name="aliveIds">当前游戏仍存在的 MOD ID。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="aliveIds"/> 为 <see langword="null"/>。</exception>
    public void Prune(string gameId, IReadOnlySet<int> aliveIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(aliveIds);

        lock (_gate)
        {
            string normalizedGame = _normalizedGameIds.Normalize(gameId);
            // 单次遍历收集待删键，无淘汰时不分配淘汰列表。
            List<(string GameId, int ModId)>? dead = null;
            foreach ((string keyGame, int modId) key in _entries.Keys)
            {
                if (CacheKeyPruning.IsDead(key, normalizedGame, aliveIds))
                {
                    (dead ??= []).Add(key);
                }
            }

            if (dead is null)
            {
                return;
            }

            foreach ((string keyGame, int modId) key in dead)
            {
                _entries.Remove(key);
            }

            _version++;
        }
    }

    /// <summary>写入预热结果；只覆盖同一游戏的条目，其它游戏缓存保持不变。</summary>
    /// <param name="gameId">预热所属游戏稳定 ID。</param>
    /// <param name="entries">文本项（含根层 pak 内容戳）。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <see langword="null"/>。</exception>
    public void Store(
        string gameId,
        IReadOnlyList<(int Id, string Signature, string Text, string PakStamp)> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(entries);

        // 先在锁外归一全部条目（null 文本按空串），不把字节统计/截断放在 UI 锁内。
        string normalizedGame;
        lock (_gate)
        {
            normalizedGame = _normalizedGameIds.Normalize(gameId);
        }

        ModelTextCacheEntry[] prepared = new ModelTextCacheEntry[entries.Count];
        for (int index = 0; index < entries.Count; index++)
        {
            (int id, string signature, string text, string pakStamp) = entries[index];
            prepared[index] = ModelTextPrefetchService.NormalizeEntry(
                new ModelTextCacheEntry(
                    id,
                    signature,
                    text ?? string.Empty,
                    pakStamp ?? string.Empty,
                    normalizedGame));
        }

        lock (_gate)
        {
            // 首次启动时预热结果可能多于当前容量（装载前只有初始容量），先按总数扩容再写入，
            // 避免 LRU 把刚算好的条目直接淘汰。
            _entries.EnsureCapacity(ModelTextPrefetchService.TextCacheCapacity(_entries.Count + entries.Count));
            bool changed = false;
            foreach (ModelTextCacheEntry entry in prepared)
            {
                if (!_entries.TryGetValue((normalizedGame, entry.ModId), out ModelTextCacheEntry existing)
                    || !existing.Equals(entry))
                {
                    changed = true;
                }

                _entries.Set((normalizedGame, entry.ModId), entry);
            }

            if (changed)
            {
                _version++;
            }
        }
    }

    /// <summary>装载跨会话缓存；缺少游戏标记或游戏 ID 非法的旧条目直接跳过。</summary>
    /// <param name="entries">持久化条目。</param>
    /// <param name="warn">丢弃条目时的告警回调；为 <see langword="null"/> 时不记录。回调在锁外执行。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <see langword="null"/>。</exception>
    public void Load(IReadOnlyList<ModelTextCacheEntry> entries, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // 规范化与截断都在锁外完成；未变化的条目复用原对象，避免逐条 with 分配。
        List<string> invalid = [];
        List<ModelTextCacheEntry> usable = new(entries.Count);
        foreach (ModelTextCacheEntry entry in entries)
        {
            // 加载侧统一归一（null 字符串、文本截断、签名/戳/游戏 ID 上界），
            // 任意 IModelTextCacheStore 实现返回脏数据都不会把 NRE 带到查询路径。
            ModelTextCacheEntry safe = ModelTextPrefetchService.NormalizeEntry(entry);
            if (string.IsNullOrEmpty(safe.GameId))
            {
                continue;
            }

            string game;
            try
            {
                game = GameProfile.NormalizeId(safe.GameId);
            }
            catch (ArgumentException)
            {
                // 单条脏游戏 ID 只丢弃该条目，不能让整份缓存装载失败。
                invalid.Add($"模型文本缓存条目 {safe.ModId} 的游戏 ID 非法，已丢弃：{safe.GameId}");
                continue;
            }

            usable.Add(string.Equals(safe.GameId, game, StringComparison.Ordinal)
                ? safe
                : safe with { GameId = game });
        }

        lock (_gate)
        {
            _entries.EnsureCapacity(ModelTextPrefetchService.TextCacheCapacity(usable.Count));
            foreach (ModelTextCacheEntry entry in usable)
            {
                _entries.Set((entry.GameId, entry.ModId), entry);
            }
        }

        if (warn is not null)
        {
            foreach (string message in invalid)
            {
                warn(message);
            }
        }
    }

    /// <summary>物化写盘快照，并带回产生快照时的版本号。</summary>
    /// <remarks>锁内只复制条目引用（不逐项投影），锁外不再做额外转换。</remarks>
    /// <returns>条目快照与版本。</returns>
    public ModelTextCacheSnapshot SnapshotWithVersion()
    {
        lock (_gate)
        {
            return new ModelTextCacheSnapshot(_entries.SnapshotValues(), _version);
        }
    }

    /// <summary>游戏 ID → 小写形式的共享规范化缓存；只在 <see cref="_gate"/> 内访问。</summary>
    private readonly GameIdNormalizationCache _normalizedGameIds = new();
}
