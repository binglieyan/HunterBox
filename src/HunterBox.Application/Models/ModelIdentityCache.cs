using HunterBox.Application.Common;
using HunterBox.Domain.Games;

namespace HunterBox.Application.Models;

/// <summary>模型明细缓存条目：写入时的内容戳与检测到的模型行。</summary>
/// <param name="Stamp">内容戳（与 <see cref="ModelTextPrefetchService.IdentityStamp"/> 同源）。</param>
/// <param name="Details">模型明细行。</param>
public sealed record ModelDetailsCacheEntry(string Stamp, IReadOnlyList<ModelDetail> Details);

/// <summary>「已用模型身份」缓存条目：写入时的内容戳与该 MOD 占用的完整身份。</summary>
/// <param name="Stamp">内容戳；命中时必须与当前戳一致才复用。</param>
/// <param name="UsedIds">完整模型身份集合。</param>
public sealed record ModelUsedIdsCacheEntry(string Stamp, IReadOnlyList<string> UsedIds);

/// <summary>身份缓存的一次性快照：后台准备读取用，避免分别物化两份字典。</summary>
/// <param name="Details">明细缓存（键含游戏 ID）。</param>
/// <param name="UsedIds">已用身份缓存（键含游戏 ID）。</param>
public sealed record ModelIdentitySnapshot(
    IReadOnlyDictionary<(string GameId, int ModId), ModelDetailsCacheEntry> Details,
    IReadOnlyDictionary<(string GameId, int ModId), ModelUsedIdsCacheEntry> UsedIds);

/// <summary>
/// 模型编辑器的身份缓存：明细行与「已用身份」按 <c>(游戏, MOD)</c> 隔离，容量有上限。
/// </summary>
/// <remarks>
/// <para>
/// 线程安全：所有读写都在内部锁内完成，UI 线程与后台准备可以共用同一实例；
/// 后台准备建议使用 <see cref="SnapshotAll"/> 一次物化两份只读字典。
/// 两个缓存跨游戏共享全局容量上限，切走的游戏条目保留，但超大库仍会按 LRU 淘汰最久未用项。
/// </para>
/// <para>
/// 内容戳语义：明细与已用身份都保存写入时的内容戳，
/// 调用方打开编辑器或预热失效前会先计算当前戳，只有戳一致才复用缓存；
/// 外部替换 pak 或散装目录增删/改名后不会继续使用旧身份。
/// </para>
/// </remarks>
public sealed class ModelIdentityCache
{
    /// <summary>「已用身份」容量上限：跨游戏保留时也不让常驻内存无限增长。</summary>
    public const int MaxUsedIdCapacity = 10_000;

    private readonly object _gate = new();
    private readonly LruCache<(string GameId, int ModId), ModelDetailsCacheEntry> _details;
    private readonly LruCache<(string GameId, int ModId), ModelUsedIdsCacheEntry> _usedIds;

    /// <summary>创建身份缓存。</summary>
    /// <param name="detailsCapacity">明细缓存容量，必须大于 0。</param>
    /// <param name="usedIdCapacity">已用身份缓存初始容量，必须大于 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一容量不大于 0。</exception>
    public ModelIdentityCache(int detailsCapacity, int usedIdCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(detailsCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(usedIdCapacity, 1);
        _details = new LruCache<(string GameId, int ModId), ModelDetailsCacheEntry>(detailsCapacity);
        _usedIds = new LruCache<(string GameId, int ModId), ModelUsedIdsCacheEntry>(usedIdCapacity);
    }

    /// <summary>按当前 MOD 数上调「已用身份」容量（至少 1024，至多 <see cref="MaxUsedIdCapacity"/>）。</summary>
    /// <param name="modCount">当前游戏的 MOD 数。</param>
    public void EnsureUsedIdsCapacity(int modCount)
    {
        lock (_gate)
        {
            _usedIds.EnsureCapacity(Math.Clamp(modCount * 2, 1024, MaxUsedIdCapacity));
        }
    }

    /// <summary>写入明细缓存。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="modId">MOD ID。</param>
    /// <param name="stamp">本次扫描使用的内容戳。</param>
    /// <param name="details">明细行。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    public void StoreDetails(string gameId, int modId, string stamp, IReadOnlyList<ModelDetail> details)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(details);
        (string GameId, int ModId) key = Key(gameId, modId);
        lock (_gate)
        {
            _details.Set(key, new ModelDetailsCacheEntry(stamp, details));
        }
    }

    /// <summary>写入「已用身份」缓存。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="modId">MOD ID。</param>
    /// <param name="stamp">本次扫描使用的内容戳。</param>
    /// <param name="usedIds">完整模型身份集合。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    public void StoreUsedIds(string gameId, int modId, string stamp, IReadOnlyList<string> usedIds)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(usedIds);
        (string GameId, int ModId) key = Key(gameId, modId);
        lock (_gate)
        {
            _usedIds.Set(key, new ModelUsedIdsCacheEntry(stamp, usedIds));
        }
    }

    /// <summary>移除某个 MOD 在全部游戏下的两条缓存（MOD 删除/改写后的粗粒度失效）。</summary>
    /// <param name="modId">MOD ID。</param>
    public void Remove(int modId)
    {
        lock (_gate)
        {
            _details.RemoveWhere(key => key.ModId == modId);
            _usedIds.RemoveWhere(key => key.ModId == modId);
        }
    }

    /// <summary>移除指定游戏下的一批 MOD 缓存（预热发现内容戳变化时调用）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="modIds">失效的 MOD ID。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="modIds"/> 为 <see langword="null"/>。</exception>
    public void Remove(string gameId, IReadOnlyList<int> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        string game = GameProfile.NormalizeId(gameId);
        HashSet<int> targets = [.. modIds];
        lock (_gate)
        {
            _details.RemoveWhere(key => IsSameGame(key.GameId, game) && targets.Contains(key.ModId));
            _usedIds.RemoveWhere(key => IsSameGame(key.GameId, game) && targets.Contains(key.ModId));
        }
    }

    /// <summary>按当前游戏存活 MOD 裁剪缓存；其它游戏条目保留。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="aliveIds">当前游戏仍存在的 MOD ID。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="aliveIds"/> 为 <see langword="null"/>。</exception>
    public void Prune(string gameId, IReadOnlySet<int> aliveIds)
    {
        ArgumentNullException.ThrowIfNull(aliveIds);
        string game = GameProfile.NormalizeId(gameId);
        lock (_gate)
        {
            _details.RemoveWhere(key => IsSameGame(key.GameId, game) && !aliveIds.Contains(key.ModId));
            _usedIds.RemoveWhere(key => IsSameGame(key.GameId, game) && !aliveIds.Contains(key.ModId));
        }
    }

    /// <summary>一次性物化两份缓存快照，供后台准备读取。</summary>
    /// <returns>明细与已用身份的只读字典副本。</returns>
    public ModelIdentitySnapshot SnapshotAll()
    {
        lock (_gate)
        {
            return new ModelIdentitySnapshot(_details.SnapshotDictionary(), _usedIds.SnapshotDictionary());
        }
    }

    /// <summary>内容戳是否一致：明细与已用身份的命中判定共用这一处比较语义。</summary>
    /// <param name="stored">缓存条目写入时的戳。</param>
    /// <param name="current">本次计算出的戳。</param>
    /// <returns>一致时返回 <see langword="true"/>。</returns>
    public static bool StampMatches(string? stored, string current) =>
        string.Equals(stored, current, StringComparison.Ordinal);

    /// <summary>比较键里的游戏 ID 与规范化后的游戏是否一致；键写入时已规范化，无需再转换。</summary>
    /// <param name="keyGame">键里的游戏 ID。</param>
    /// <param name="game">规范化后的游戏 ID。</param>
    /// <returns>同一游戏时返回 <see langword="true"/>。</returns>
    private static bool IsSameGame(string keyGame, string game) =>
        string.Equals(keyGame, game, StringComparison.Ordinal);

    /// <summary>构造规范化键。</summary>
    private static (string GameId, int ModId) Key(string gameId, int modId) =>
        (GameProfile.NormalizeId(gameId), modId);
}
