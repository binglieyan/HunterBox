using HunterBox.Application.Common;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Paks;

/// <summary>
/// 进程级内容指纹缓存：跨多次启用/禁用/清理复用整文件哈希，同一 pak 只做一次整文件哈希。
/// </summary>
/// <remarks>
/// <para>
/// pak 内容指纹（MD5）要读完整文件；批量启用/禁用会反复比较同一批 pak
/// （<c>pak_mods</c> 里已部署的文件 + 各 MOD 的源文件）。缓存若放在每次新建的
/// <see cref="PakMountService"/> 上，会在每个 MOD 上重算一遍（P-01）。
/// 组合根持有本类为进程级单例，连续多次单独启用/禁用共享结果。
/// </para>
/// <para>
/// 容量上限 <see cref="DefaultMaxEntries"/>，满时淘汰最久未用项；
/// <see cref="GetFingerprint"/> 与 <see cref="Remember"/> 都受同一上限约束。
/// 命中前先比对文件的「字节数 + 最后写入时间」：文件被改写后自动重算，绝不返回过期指纹。
/// 内部字典由锁保护：进程级共享时多个操作并发命中也不会破坏状态，
/// 但同一路径的首次计算仍可能重复（结果一致，仅多一次读盘）。
/// </para>
/// </remarks>
public sealed class PakFingerprintCache : IContentFingerprint
{
    /// <summary>进程级缓存的默认条目上限；达到上限时淘汰最久未用项，避免内存无界增长。</summary>
    internal const int DefaultMaxEntries = 4096;

    private readonly IContentFingerprint _inner;
    private readonly IFileSystem _fileSystem;
    private readonly int _maxEntries;

    /// <summary>缓存条目索引与最近使用顺序（锁内维护）。</summary>
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _order = [];

    /// <summary>包装一个真正计算指纹的实现。</summary>
    /// <param name="inner">底层指纹实现（通常是整文件哈希）。</param>
    /// <param name="fileSystem">文件系统端口（读取长度与最后写入时间）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public PakFingerprintCache(IContentFingerprint inner, IFileSystem fileSystem)
        : this(inner, fileSystem, DefaultMaxEntries)
    {
    }

    /// <summary>用指定容量装配缓存（测试用：便于在少量条目上验证淘汰策略）。</summary>
    /// <param name="inner">底层指纹实现。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="maxEntries">条目上限，必须大于 0。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> 不大于 0。</exception>
    internal PakFingerprintCache(IContentFingerprint inner, IFileSystem fileSystem, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        _inner = inner;
        _fileSystem = fileSystem;
        _maxEntries = maxEntries;
    }

    /// <summary>当前缓存条目数（诊断用途，供测试断言容量上限）。</summary>
    internal int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public string GetFingerprint(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (!_fileSystem.FileExists(fullPath))
        {
            // 不自行改写「文件不存在」的异常语义：交给底层实现抛它原本的错误。
            Remove(fullPath);
            return _inner.GetFingerprint(fullPath);
        }

        long length = _fileSystem.GetFileLength(fullPath);
        DateTime lastWriteTimeUtc = _fileSystem.GetFileLastWriteTimeUtc(fullPath);
        lock (_gate)
        {
            if (_entries.TryGetValue(fullPath, out LinkedListNode<CacheEntry>? cached)
                && cached.Value.Length == length
                && cached.Value.LastWriteTimeUtc == lastWriteTimeUtc)
            {
                // 命中即刷新最近使用顺序。
                _order.Remove(cached);
                _order.AddFirst(cached);
                return cached.Value.Fingerprint;
            }
        }

        string fingerprint = _inner.GetFingerprint(fullPath);
        Store(fullPath, length, lastWriteTimeUtc, fingerprint);
        return fingerprint;
    }

    /// <summary>登记已知内容指纹（例如刚由同一来源复制的文件），避免下一次操作重新整文件读取。</summary>
    /// <param name="fullPath">文件绝对路径。</param>
    /// <param name="fingerprint">与底层指纹实现一致的已知值。</param>
    /// <exception cref="ArgumentException"><paramref name="fullPath"/> 为空或空白。</exception>
    public void Remember(string fullPath, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!_fileSystem.FileExists(fullPath))
        {
            Remove(fullPath);
            return;
        }

        // Remember 与 GetFingerprint 共用同一上限与淘汰策略。
        Store(
            fullPath,
            _fileSystem.GetFileLength(fullPath),
            _fileSystem.GetFileLastWriteTimeUtc(fullPath),
            fingerprint);
    }

    /// <summary>
    /// 丢弃某路径的缓存值：文件被本服务改写/删除后调用。
    /// </summary>
    /// <remarks>
    /// 「长度 + 最后写入时间」已经能挡住绝大多数改写，但同一批次里「删除后又被重新编号写入同一路径」时，
    /// 新文件可能恰好保留相同长度与时间戳（例如拷贝自同一来源）；显式失效保证不会用旧内容参与比较。
    /// </remarks>
    /// <param name="fullPath">文件绝对路径。</param>
    public void Invalidate(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        Remove(fullPath);
    }

    /// <summary>写入/更新条目并把容量裁剪到上限（淘汰最久未用项）。</summary>
    /// <param name="fullPath">文件绝对路径。</param>
    /// <param name="length">文件长度。</param>
    /// <param name="lastWriteTimeUtc">最后写入时间（UTC）。</param>
    /// <param name="fingerprint">内容指纹。</param>
    private void Store(string fullPath, long length, DateTime lastWriteTimeUtc, string fingerprint)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(fullPath, out LinkedListNode<CacheEntry>? existing))
            {
                _order.Remove(existing);
            }

            _entries[fullPath] = _order.AddFirst(new CacheEntry(fullPath, length, lastWriteTimeUtc, fingerprint));
            while (_entries.Count > _maxEntries && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Path);
            }
        }
    }

    /// <summary>移除指定路径的缓存条目。</summary>
    /// <param name="fullPath">文件绝对路径。</param>
    private void Remove(string fullPath)
    {
        lock (_gate)
        {
            if (_entries.Remove(fullPath, out LinkedListNode<CacheEntry>? node))
            {
                _order.Remove(node);
            }
        }
    }

    private sealed record CacheEntry(string Path, long Length, DateTime LastWriteTimeUtc, string Fingerprint);
}
