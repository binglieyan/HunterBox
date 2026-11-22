using HunterBox.Application.Common;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Models;

/// <summary>
/// 模型身份的会话内内容戳缓存：同一记录实例且未超过 TTL 时复用，避免每次打开编辑器全库 stat。
/// </summary>
/// <remarks>
/// <para>
/// 记录被替换（安装/重装/改写会整体替换 <see cref="ModRecord.RelativeFiles"/>）或调用方精确失效时重算；
/// 会话内外部修改由绝对 TTL 兜底（默认 10 分钟，按 MOD 分散 3–5 分钟，命中不续期）；
/// 目标 MOD 每次打开由调用方强制重算。
/// 打开编辑器时过期条目先复用缓存值，由调用方用 <see cref="ModelStampRefresh"/> 在时间预算内轮换刷新，
/// 其余在后续打开中补齐，避免空闲后一次全库 stat。
/// </para>
/// <para>
/// <c>unreadable:</c> 结果缓存 30 秒退避（目标 MOD 的强制刷新也受退避约束）：
/// 退避期内不重复扫描/写日志，到期后立即重试。写入不可读结果时按 MOD 递增版本并替换旧条目，
/// 在途计算不会把旧可读戳写回。
/// 失效按 MOD ID 精确进行；裁剪删除条目或作废已删除 MOD 的在途计算时都会递增版本，
/// 并标记在途键，保证可读/不可读结果都不会写回。
/// </para>
/// <para>
/// 线程安全：打开编辑器在后台线程读取，预热/操作在 UI 线程失效，内部用锁保护；计算委托在锁外执行，
/// 避免慢盘读取阻塞失效调用。缓存条目按游戏裁剪，删除的 MOD 不会长期占用内存。
/// </para>
/// </remarks>
public sealed class ModelIdentityStampCache
{
    /// <summary>默认缓存有效期：10 分钟内复用戳，超时后重新 stat 以感知外部变化。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    /// <summary>按 MOD 分散的最小额外有效期：同批写入的条目至少错开 3 分钟。</summary>
    public static readonly TimeSpan MinJitter = TimeSpan.FromMinutes(3);

    /// <summary>按 MOD 分散的最大额外有效期：实际有效期落在 TTL + 3–5 分钟。</summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(5);

    /// <summary>不可读结果的退避时间：期间不重复扫描/写日志，到期后立即重试。</summary>
    public static readonly TimeSpan UnreadableBackoff = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<(string GameId, int ModId), Entry> _entries = [];
    private readonly Dictionary<(string GameId, int ModId), long> _modVersions = [];

    /// <summary>在途计算计数：同一键并发计算时只有最后一个结束才能清理版本键。</summary>
    private readonly Dictionary<(string GameId, int ModId), int> _inFlight = [];

    /// <summary>
    /// 裁剪时已删除键的删除代数：计算开始时记录，完成时若代数变化则不写回。
    /// 用代数而不是布尔集合，删除后立即重装同 ID 时新计算仍可正常写回。
    /// </summary>
    private readonly Dictionary<(string GameId, int ModId), long> _deletedEpoch = [];

    /// <summary>原始游戏 ID → 小写形式（只在 <see cref="_gate"/> 内访问）。</summary>
    private readonly GameIdNormalizationCache _normalizedGameIds = new();

    private readonly IClock _clock;
    private readonly TimeSpan _ttl;

    /// <summary>创建戳缓存。</summary>
    /// <param name="clock">时钟端口；为 <see langword="null"/> 时使用系统时钟。</param>
    /// <param name="ttl">缓存有效期；为 <see langword="null"/> 时使用 <see cref="DefaultTtl"/>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ttl"/> 不大于 0。</exception>
    public ModelIdentityStampCache(IClock? clock = null, TimeSpan? ttl = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _ttl = ttl ?? DefaultTtl;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_ttl, TimeSpan.Zero);
    }

    /// <summary>让某个 MOD 在全部游戏下的戳失效（改写/重装后的精确失效）。</summary>
    /// <param name="modId">MOD ID。</param>
    public void Invalidate(int modId)
    {
        lock (_gate)
        {
            // 单次遍历收集条目键与版本键，无目标时不分配列表。
            List<(string GameId, int ModId)>? removed = null;
            foreach ((string GameId, int ModId) key in _entries.Keys)
            {
                if (key.ModId == modId)
                {
                    (removed ??= []).Add(key);
                }
            }

            List<(string GameId, int ModId)>? versions = null;
            foreach ((string GameId, int ModId) key in _modVersions.Keys)
            {
                if (key.ModId == modId)
                {
                    (versions ??= []).Add(key);
                }
            }

            foreach ((string GameId, int ModId) key in removed ?? [])
            {
                _entries.Remove(key);
            }

            foreach ((string GameId, int ModId) key in versions ?? [])
            {
                // 在途计算继续递增版本以作废旧戳；没有在途计算时直接删除版本键，不留残余。
                if (_inFlight.ContainsKey(key))
                {
                    Bump(key);
                }
                else
                {
                    _modVersions.Remove(key);
                }
            }

            // 在途计算可能还没有版本键（首次计算）：同样递增，阻止它写回失效前的旧戳。
            foreach ((string GameId, int ModId) key in _inFlight.Keys)
            {
                if (key.ModId == modId && !_modVersions.ContainsKey(key))
                {
                    Bump(key);
                }
            }
        }
    }

    /// <summary>让指定游戏下的一批 MOD 戳失效；空列表不触发任何清理。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="modIds">失效的 MOD ID；为空时直接返回。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="modIds"/> 为 <see langword="null"/>。</exception>
    public void Invalidate(string gameId, IReadOnlyList<int> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        if (modIds.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            string game = _normalizedGameIds.Normalize(gameId);
            foreach (int modId in modIds)
            {
                (string GameId, int ModId) key = (game, modId);
                _entries.Remove(key);
                // 按目标 MOD 直接处理版本键，不再复制整份版本表。
                if (_inFlight.ContainsKey(key))
                {
                    Bump(key);
                }
                else
                {
                    _modVersions.Remove(key);
                }
            }
        }
    }

    /// <summary>按当前游戏存活 MOD 裁剪缓存；其它游戏条目保留（与身份缓存一致）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="aliveIds">当前游戏仍存在的 MOD ID。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="aliveIds"/> 为 <see langword="null"/>。</exception>
    public void Prune(string gameId, IReadOnlySet<int> aliveIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(aliveIds);
        lock (_gate)
        {
            string game = _normalizedGameIds.Normalize(gameId);
            // 单次遍历收集待删键，无淘汰时不分配列表。
            List<(string GameId, int ModId)>? dead = null;
            foreach ((string keyGame, int modId) key in _entries.Keys)
            {
                if (CacheKeyPruning.IsDead(key, game, aliveIds))
                {
                    (dead ??= []).Add(key);
                }
            }

            if (dead is not null)
            {
                foreach ((string GameId, int ModId) key in dead)
                {
                    _entries.Remove(key);
                    // 没有在途计算时删除版本键；有在途时由下面统一递增一次。
                    if (!_inFlight.ContainsKey(key))
                    {
                        _modVersions.Remove(key);
                    }
                }
            }

            // 只在途键统一递增一次，同时提升删除代数；
            // 计算完成后发现代数变化就不再写回，删除后立即重装同 ID 的新计算仍可写入。
            foreach ((string GameId, int ModId) key in _inFlight.Keys)
            {
                if (CacheKeyPruning.IsDead(key, game, aliveIds))
                {
                    _deletedEpoch[key] = DeletedEpochOf(key) + 1;
                    Bump(key);
                }
            }
        }
    }

    /// <summary>
    /// 取记录的内容戳：同一实例且未超过条目绝对 TTL 时复用缓存，否则调用 <paramref name="compute"/> 重新计算。
    /// </summary>
    /// <remarks>
    /// 命中不续期（绝对 TTL），并按 MOD ID 分散 3–5 分钟；
    /// <c>unreadable:</c> 结果缓存 30 秒退避（目标 MOD 的强制刷新也受约束）。
    /// </remarks>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="mod">目标 MOD 记录。</param>
    /// <param name="compute">实际计算内容戳的委托（在锁外执行）。</param>
    /// <param name="forceRefresh">为 <see langword="true"/> 时跳过正常 TTL 直接重算（目标 MOD）。</param>
    /// <returns>内容戳。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    public string GetOrCompute(
        string gameId,
        ModRecord mod,
        Func<string> compute,
        bool forceRefresh = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(compute);

        (string GameId, int ModId) key;
        DateTimeOffset now = _clock.UtcNow;
        long version;
        long deletedEpoch;
        lock (_gate)
        {
            key = (_normalizedGameIds.Normalize(gameId), mod.Id);
            if (_entries.TryGetValue(key, out Entry entry) && ReferenceEquals(entry.Mod, mod))
            {
                bool unreadable = ModelTextPrefetchService.IsUnreadableStamp(entry.Stamp);
                if (unreadable)
                {
                    // 不可读退避优先于强制刷新；退避到期后必须重算，不能落入正常 TTL 分支。
                    if (now - entry.CachedAt < UnreadableBackoff)
                    {
                        return entry.Stamp;
                    }
                }
                else if (!forceRefresh && now - entry.CachedAt < TtlFor(mod))
                {
                    return entry.Stamp;
                }
            }

            version = VersionOf(key);
            deletedEpoch = DeletedEpochOf(key);
            AddInFlight(key);
        }

        try
        {
            string stamp = compute();
            DateTimeOffset storedAt = _clock.UtcNow;
            bool unreadable = ModelTextPrefetchService.IsUnreadableStamp(stamp);
            lock (_gate)
            {
                // 删除代数变化说明计算期间 MOD 被裁剪；可读/不可读结果都不写回。
                bool deleted = DeletedEpochOf(key) != deletedEpoch;
                if (unreadable)
                {
                    // 写入不可读结果并按 MOD 递增版本，阻止在途计算写回旧可读戳。
                    if (!deleted)
                    {
                        _entries[key] = new Entry(mod, stamp, storedAt);
                        Bump(key);
                    }

                    return stamp;
                }

                // 计算期间被精确失效过就不写入，避免把失效前开始的旧戳重新缓存。
                if (!deleted && version == VersionOf(key))
                {
                    _entries[key] = new Entry(mod, stamp, storedAt);
                }

                return stamp;
            }
        }
        finally
        {
            lock (_gate)
            {
                ReleaseInFlight(key);
                if (!_entries.ContainsKey(key) && !_inFlight.ContainsKey(key))
                {
                    // 只有最后一个在途计算结束且没有条目时才清版本键，避免并发同键提前清理。
                    _modVersions.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// 读取缓存戳并报告是否已过期（不触发重算）：调用方可以先复用旧值，再按预算轮换刷新。
    /// </summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="mod">目标 MOD 记录。</param>
    /// <param name="stamp">命中的内容戳。</param>
    /// <param name="expired">已超过该条目的 TTL/退避窗口。</param>
    /// <returns>存在同一记录实例的缓存时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public bool TryGetCached(
        string gameId,
        ModRecord mod,
        out string stamp,
        out bool expired)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(mod);

        (string GameId, int ModId) key;
        lock (_gate)
        {
            key = (_normalizedGameIds.Normalize(gameId), mod.Id);
            if (_entries.TryGetValue(key, out Entry entry) && ReferenceEquals(entry.Mod, mod))
            {
                stamp = entry.Stamp;
                TimeSpan window = ModelTextPrefetchService.IsUnreadableStamp(entry.Stamp)
                    ? UnreadableBackoff
                    : TtlFor(mod);
                expired = _clock.UtcNow - entry.CachedAt >= window;
                return true;
            }
        }

        stamp = string.Empty;
        expired = false;
        return false;
    }

    /// <summary>条目的绝对有效期：基础 TTL 加按 MOD ID 确定性分散的 3–5 分钟抖动。</summary>
    /// <param name="mod">目标 MOD。</param>
    /// <returns>该条目的有效期。</returns>
    private TimeSpan TtlFor(ModRecord mod)
    {
        int range = (int)(MaxJitter - MinJitter).TotalSeconds;
        int extra = (int)((uint)(mod.Id * 37) % (uint)(range + 1));
        return _ttl + MinJitter + TimeSpan.FromSeconds(extra);
    }

    /// <summary>按 MOD 键递增版本；只在锁内调用。</summary>
    /// <param name="key">缓存键。</param>
    private void Bump((string GameId, int ModId) key) => _modVersions[key] = VersionOf(key) + 1;

    /// <summary>登记一次在途计算；同一键并发时只递增计数。</summary>
    /// <param name="key">缓存键。</param>
    private void AddInFlight((string GameId, int ModId) key) =>
        _inFlight[key] = _inFlight.TryGetValue(key, out int count) ? count + 1 : 1;

    /// <summary>结束一次在途计算；计数归零时移除键。</summary>
    /// <param name="key">缓存键。</param>
    private void ReleaseInFlight((string GameId, int ModId) key)
    {
        // finally 中绝不能让 KeyNotFound 掩盖计算本身的异常；
        // 正常状态下登记一定存在，假分支只是异常保真。
        if (!_inFlight.TryGetValue(key, out int count))
        {
            return;
        }

        if (count <= 1)
        {
            _inFlight.Remove(key);
            _deletedEpoch.Remove(key);
        }
        else
        {
            _inFlight[key] = count - 1;
        }
    }

    /// <summary>读取删除代数；从未被裁剪删除过的键为 0。</summary>
    /// <param name="key">缓存键。</param>
    /// <returns>当前删除代数。</returns>
    private long DeletedEpochOf((string GameId, int ModId) key) =>
        _deletedEpoch.TryGetValue(key, out long epoch) ? epoch : 0;

    /// <summary>读取键版本；从未失效过的键为 0。</summary>
    /// <param name="key">缓存键。</param>
    /// <returns>当前版本号。</returns>
    private long VersionOf((string GameId, int ModId) key) =>
        _modVersions.TryGetValue(key, out long version) ? version : 0;

    /// <summary>缓存条目：记录实例、内容戳与写入时刻。</summary>
    /// <param name="Mod">记录实例。</param>
    /// <param name="Stamp">内容戳。</param>
    /// <param name="CachedAt">写入时刻（TTL 判定）。</param>
    private readonly record struct Entry(ModRecord Mod, string Stamp, DateTimeOffset CachedAt);
}
