using System.Globalization;
using HunterBox.Application.Common;
using HunterBox.Domain.Layout;

namespace HunterBox.Application.Saves;

/// <summary>
/// 存档备份的目录存储：列出、创建槽、备份、收尾、重命名与删除（A-11）。
/// </summary>
/// <remarks>
/// 只负责 <c>Saves/&lt;gameId&gt;/&lt;备份&gt;</c> 这一层；还原与校验在 <see cref="SaveBackupRestorer"/>。
/// </remarks>
internal sealed class SaveBackupStore
{

    /// <summary>备份槽的「检查存在 + 创建」必须原子：同一进程内的并发备份共用这把锁。</summary>
    /// <remarks>
    /// 应用由 <c>SingleInstanceGuard</c> 保证单实例运行，因此进程内锁即可消除同秒并发的 TOCTOU；
    /// 仍保留「已存在则追加 <c>-N</c> 后缀」的兜底，兼容旧备份或外部程序占用了同名目录/文件。
    /// </remarks>
    private static readonly object SlotCreationGate = new();

    private readonly BoxPaths _paths;
    private readonly IClock _clock;
    private readonly IFileSystem _fileSystem;
    private readonly IBackupManifestStore _manifestStore;

    /// <summary>备份体积缓存条目（小型 struct：长度加两个可空时间，不装箱）。</summary>
    /// <param name="Size">备份字节数。</param>
    /// <param name="ManifestWriteUtc">清单写入时间；<see langword="null"/> 表示条目不是从清单得到的正缓存。</param>
    /// <param name="NegativeCachedAt">
    /// 负缓存写入时间；只有负缓存条目有值，正条目为 <see langword="null"/>，避免正条目写入无意义字段。
    /// </param>
    /// <remarks>
    /// 缺失键与负缓存分开表达，避免默认值被误判为负缓存；
    /// 负缓存有效期为 <see cref="NegativeSizeCacheLifetime"/>，写入清单、改名与删除会立即失效。
    /// </remarks>
    private readonly record struct SizeCacheEntry(
        long Size,
        DateTime? ManifestWriteUtc,
        DateTimeOffset? NegativeCachedAt);

    /// <summary>
    /// 进程级备份体积缓存：对话框每次打开都会新建 <see cref="SaveBackupStore"/>，
    /// 缓存必须跨实例共享才能让负缓存真正生效；写入/改名/删除时主动失效。
    /// 后台 <see cref="ListAsync"/> 与前台备份/收尾可能并发读写，所有访问经 <see cref="SizeCacheGate"/>
    /// 串行化，以严格 LRU 顺序在 O(1) 内完成淘汰。
    /// </summary>
    private static readonly object SizeCacheGate = new();

    /// <summary>键到 LRU 节点的索引，与 <see cref="SizeCacheOrder"/> 一一对应。</summary>
    private static readonly Dictionary<string, LinkedListNode<SizeCacheNode>> SizeCacheIndex =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>LRU 顺序：头部最近使用，尾部最久未使用；超限时从尾部淘汰。</summary>
    private static readonly LinkedList<SizeCacheNode> SizeCacheOrder = new();

    /// <summary>
    /// 按游戏目录分组的失效版本：一个游戏的写清单/改名/删除只让本游戏的扫描结果失效，
    /// 不再阻塞其它游戏缓存写入。
    /// </summary>
    private static readonly Dictionary<string, SizeCacheVersion> SizeCacheGameVersions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个游戏当前占用的缓存条目数，用于按游戏分组限额。</summary>
    private static readonly Dictionary<string, int> SizeCacheGameCounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 进程级缓存条目上限：超过后从 LRU 尾部淘汰一个条目，写入保持 O(1)，
    /// 长会话/多游戏/多路径下不会无界增长。
    /// </summary>
    internal const int MaxSizeCacheEntries = 512;

    /// <summary>
    /// 单个游戏的缓存条目上限：多游戏共享 <see cref="MaxSizeCacheEntries"/> 时，
    /// 任一游戏也不能挤占全部名额。
    /// </summary>
    internal const int MaxSizeCacheEntriesPerGame = 256;

    /// <summary>
    /// 缓存重置代次：<see cref="ResetSizeCache"/> 递增，用于让重置前开始、重置后才结束的扫描
    /// 无法把结果写回“新”缓存。
    /// </summary>
    private static long _sizeCacheEpoch;

    /// <summary>无清单负缓存的有效期：2 分钟内的连续刷新复用体积，跨越对话框重复打开。</summary>
    internal static readonly TimeSpan NegativeSizeCacheLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// LRU 节点：键与所属游戏固定，条目值可原地更新；节点只由缓存的 Set/Invalidate/Reset 在
    /// <see cref="SizeCacheGate"/> 内维护，淘汰或失效时同时从 <see cref="SizeCacheIndex"/> 与
    /// <see cref="SizeCacheOrder"/> 移除并扣减游戏计数。
    /// </summary>
    private sealed class SizeCacheNode
    {
        /// <summary>创建节点。</summary>
        /// <param name="key">归一化后的缓存键。</param>
        /// <param name="gameKey">所属游戏目录，用于分组限额与失效。</param>
        /// <param name="entry">缓存条目。</param>
        public SizeCacheNode(string key, string gameKey, SizeCacheEntry entry)
        {
            Key = key;
            GameKey = gameKey;
            Entry = entry;
        }

        /// <summary>归一化后的缓存键。</summary>
        public string Key { get; }

        /// <summary>所属游戏目录。</summary>
        public string GameKey { get; }

        /// <summary>缓存条目。</summary>
        public SizeCacheEntry Entry { get; set; }
    }

    /// <summary>按游戏分组的失效版本计数器（读取用 Volatile，递增用 Interlocked）。</summary>
    private sealed class SizeCacheVersion
    {
        private long _value;

        /// <summary>当前版本。</summary>
        public long Value => Volatile.Read(ref _value);

        /// <summary>递增版本。</summary>
        public void Increment() => Interlocked.Increment(ref _value);
    }

    /// <summary>
    /// 本实例的备份体积回退扫描次数（原子计数）：缓存是进程级的，因此计数只在当前实例累加，
    /// 新实例为 0 只说明它没有扫描，不代表缓存首次写入。
    /// </summary>
    private int _backupSizeDirectoryScanCount;

    /// <summary>本实例的备份体积回退扫描次数快照。</summary>
    internal int BackupSizeDirectoryScanCount => Volatile.Read(ref _backupSizeDirectoryScanCount);

    /// <summary>本实例真正读取哈希清单的次数（原子计数），供用例区分「读清单」与「扫目录」。</summary>
    private int _backupSizeManifestReadCount;

    /// <summary>本实例读取哈希清单的次数快照。</summary>
    internal int BackupSizeManifestReadCount => Volatile.Read(ref _backupSizeManifestReadCount);

    /// <summary>本实例对清单做元数据探测的次数，供用例断言成功路径只探测一次。</summary>
    private int _backupSizeMetadataProbeCount;

    /// <summary>本实例对清单做元数据探测的次数快照。</summary>
    internal int BackupSizeMetadataProbeCount => Volatile.Read(ref _backupSizeMetadataProbeCount);

    /// <summary>进程级体积缓存当前条目数，仅供回归测试断言容量上限。</summary>
    internal static int SizeCacheEntryCount
    {
        get
        {
            lock (SizeCacheGate)
            {
                return SizeCacheIndex.Count;
            }
        }
    }

    /// <summary>判断某个备份目录是否仍在缓存中，仅供回归测试断言 LRU 不误删最近写入项。</summary>
    /// <param name="directory">备份目录。</param>
    /// <returns>缓存中存在该键时返回 <see langword="true"/>。</returns>
    internal static bool SizeCacheContains(string directory)
    {
        string key = CacheKey(directory);
        lock (SizeCacheGate)
        {
            return SizeCacheIndex.ContainsKey(key);
        }
    }

    /// <summary>清空进程级体积缓存与分组状态，仅供回归测试隔离。</summary>
    /// <remarks>同时递增代次，重置前开始的扫描即使稍后结束也不能写回新缓存。</remarks>
    internal static void ResetSizeCache()
    {
        lock (SizeCacheGate)
        {
            Interlocked.Increment(ref _sizeCacheEpoch);
            SizeCacheIndex.Clear();
            SizeCacheOrder.Clear();
            SizeCacheGameCounts.Clear();
            SizeCacheGameVersions.Clear();
        }
    }

    /// <summary>装配存储。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="clock">可替换时钟。</param>
    /// <param name="fileSystem">文件系统端口（失败清理用）。</param>
    /// <param name="manifestStore">清单文本编解码端口。</param>
    internal SaveBackupStore(
        BoxPaths paths,
        IClock clock,
        IFileSystem fileSystem,
        IBackupManifestStore manifestStore)
    {
        _paths = paths;
        _clock = clock;
        _fileSystem = fileSystem;
        _manifestStore = manifestStore;
    }

    /// <summary>某款游戏的备份根目录。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns><c>&lt;Root&gt;/Saves/&lt;gameId&gt;</c>。</returns>
    internal string SavesRoot(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return Path.Combine(_paths.Root, "Saves", gameId);
    }

    /// <summary>列出已有备份，按时间倒序。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="cancellationToken">取消令牌：遍历备份目录时逐项检查（C-19）。</param>
    /// <returns>备份列表（含各备份大小）；根目录不存在时为空。</returns>
    internal IReadOnlyList<SaveBackup> List(string gameId, CancellationToken cancellationToken = default)
    {
        string root = SavesRoot(gameId);
        if (!_fileSystem.DirectoryExists(root))
        {
            return [];
        }

        List<SaveBackup> backups = [];
        DateTimeOffset now = _clock.UtcNow;
        foreach (string directory in _fileSystem.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            DateTimeOffset created = SaveBackupFormatter.TryParseTimestamp(name, out DateTimeOffset parsed)
                ? parsed
                : new DateTimeOffset(_fileSystem.GetDirectoryCreationTimeUtc(directory));

            backups.Add(new SaveBackup(name, directory, created)
            {
                // 一次 List 只取一次时钟，传给所有备份的体积缓存判定。
                SizeBytes = GetBackupSize(directory, now, cancellationToken),
            });
        }

        // 原地排序，避免 OrderByDescending 再复制一份列表。
        backups.Sort(static (a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return backups;
    }

    /// <summary>异步列出已有备份（体积统计可能扫目录，放到后台线程，避免卡 UI）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>备份列表。</returns>
    internal Task<IReadOnlyList<SaveBackup>> ListAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.Run(() => List(gameId, cancellationToken), cancellationToken);

    /// <summary>备份一个存档目录。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="saveDirectory">游戏存档目录（必须存在且至少包含一个文件）。</param>
    /// <returns>新建的备份。</returns>
    /// <exception cref="InvalidDataException">存档目录为空，没有可备份/可还原的文件。</exception>
    internal SaveBackup Backup(string gameId, string saveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        if (!_fileSystem.DirectoryExists(saveDirectory))
        {
            throw new DirectoryNotFoundException($"存档目录不存在: {saveDirectory}");
        }

        SaveBackup slot = CreateSlot(gameId, _clock.UtcNow.ToLocalTime());
        long totalBytes;
        try
        {
            // 复制与 SHA-256 合并为一次读盘，避免复制后再整目录读一遍（P-10）。
            (int copied, totalBytes) = CopyDirectoryWithManifest(saveDirectory, slot.Directory);
            if (copied == 0)
            {
                // 空备份没有任何可校验文件，生成清单后也无法还原；在备份阶段直接拒绝。
                throw new InvalidDataException($"存档目录为空，没有可备份的文件: {saveDirectory}");
            }
        }
        catch
        {
            // 失败时删掉半成品备份，绝不留下「看起来存在但内容不全」的备份。
            _fileSystem.TryDeleteDirectory(slot.Directory);
            // 复制阶段可能已写入清单与 0 字节正缓存；目录删除后同步失效，避免残留。
            InvalidateSizeCache(CacheKey(slot.Directory));
            throw;
        }

        // 复制时已得到每个文件的大小，直接用总和返回；不再为读体积而回读清单。
        return slot with { SizeBytes = totalBytes };
    }

    /// <summary>创建一个空备份目录（<c>Saves/&lt;gameId&gt;/&lt;时间戳&gt;</c>），供旧存档 zip 导入等直接解压填充。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="createdAt">备份时间，决定目录名与列表排序。</param>
    /// <returns>已创建（且目录已存在）的备份槽；同秒冲突时追加 <c>-1</c>、<c>-2</c> 后缀。</returns>
    internal SaveBackup CreateSlot(string gameId, DateTimeOffset createdAt)
    {
        string root = SavesRoot(gameId);
        _fileSystem.CreateDirectory(root);

        string baseName = createdAt.ToString(SaveBackupFormatter.TimestampFormat, CultureInfo.InvariantCulture);
        lock (SlotCreationGate)
        {
            for (int suffix = 0; ; suffix++)
            {
                string name = suffix == 0
                    ? baseName
                    : $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}";
                string target = Path.Combine(root, name);
                if (_fileSystem.DirectoryExists(target) || _fileSystem.FileExists(target))
                {
                    continue;
                }

                // 创建成功即视为「已占用」；创建失败（无权限/磁盘满）直接抛给调用方，不静默换名。
                _fileSystem.CreateDirectory(target);
                return new SaveBackup(name, target, createdAt);
            }
        }
    }

    /// <summary>用调用方在解压/复制时同步计算的清单收尾备份槽，避免再次整目录读盘。</summary>
    /// <param name="backup">已填充内容的备份槽。</param>
    /// <param name="entries">已校验的清单条目；调用方保证非空且路径合法。</param>
    /// <param name="totalBytes">条目总字节数，由调用方在校验循环中同步累加。</param>
    /// <returns>带最新大小的备份。</returns>
    /// <exception cref="DirectoryNotFoundException">备份目录不存在。</exception>
    /// <exception cref="InvalidDataException">序列化后的清单超过 16MB。</exception>
    /// <exception cref="IOException">写入清单失败。</exception>
    /// <remarks>生产导入只走预计算清单，重扫与空槽删除分支已删除。</remarks>
    internal SaveBackup FinalizeImportedBackup(
        SaveBackup backup,
        List<BackupManifestEntry> entries,
        long totalBytes)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (!_fileSystem.DirectoryExists(backup.Directory))
        {
            throw new DirectoryNotFoundException($"备份不存在: {backup.Directory}");
        }

        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        SaveBackupManifest.WriteManifest(backup.Directory, entries, _clock, _fileSystem, _manifestStore);

        // 写清单时已得到总体积，直接更新缓存并递增失效版本，不再回读整份清单。
        RefreshSizeCacheAfterManifestWrite(backup.Directory, totalBytes);

        return backup with { SizeBytes = totalBytes };
    }

    /// <summary>
    /// 写清单后刷新体积缓存：失效旧值、取一次清单时间写入正缓存；元数据读取失败时本次不缓存。
    /// </summary>
    /// <param name="directory">备份目录。</param>
    /// <param name="totalBytes">清单内文件总字节数。</param>
    private void RefreshSizeCacheAfterManifestWrite(string directory, long totalBytes)
    {
        string key = CacheKey(directory);
        InvalidateSizeCache(key);
        try
        {
            DateTime manifestWrite = _fileSystem.GetFileLastWriteTimeUtc(
                Path.Combine(directory, SaveBackupManifest.FileName));
            SetSizeCache(key, new SizeCacheEntry(totalBytes, manifestWrite, NegativeCachedAt: null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清单刚写入却读不到元数据：本次不缓存，下次列表按普通路径重新探测。
        }
    }

    /// <summary>重命名一个备份目录（只改名称，内容与备份时间不变）。</summary>
    /// <remarks>成功后把体积缓存的旧目录键迁移到新路径，避免新路径重新扫描与旧键残留。</remarks>
    /// <param name="backup">要重命名的备份。</param>
    /// <param name="newName">新名称；非法字符、<c>.</c>/<c>..</c>、以点/空格结尾会被拒绝。</param>
    /// <returns>重命名后的备份（<see cref="SaveBackup.CreatedAt"/> 与 <see cref="SaveBackup.SizeBytes"/> 保留）。</returns>
    internal SaveBackup Rename(SaveBackup backup, string newName)
    {
        ArgumentNullException.ThrowIfNull(backup);
        string trimmed = ValidateName(newName, nameof(newName));
        string source = EnsureInsideSaves(backup.Directory);
        if (!_fileSystem.DirectoryExists(source))
        {
            throw new DirectoryNotFoundException($"备份不存在: {backup.Directory}");
        }

        if (string.Equals(backup.Name, trimmed, StringComparison.Ordinal))
        {
            return backup;
        }

        string parent = Path.GetDirectoryName(source)!;
        string target = Path.Combine(parent, trimmed);
        bool caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && _fileSystem.DirectoryExists(target))
        {
            throw new IOException($"同名备份已存在: {trimmed}");
        }

        try
        {
            if (caseOnly)
            {
                // Windows 下仅改大小写时目标路径「已存在」，经临时目录两步改名。
                string temp = Path.Combine(parent, backup.Name + ".renaming-" + Guid.NewGuid().ToString("N"));
                _fileSystem.MoveDirectory(source, temp);
                try
                {
                    _fileSystem.MoveDirectory(temp, target);
                }
                catch
                {
                    TryMove(temp, source);
                    throw;
                }
            }
            else
            {
                _fileSystem.MoveDirectory(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"重命名备份失败: {backup.Name} → {trimmed}", ex);
        }

        // 键统一归一化后迁移，并递增本游戏失效版本，
        // 阻止并发扫描把旧路径的陈旧结果写回缓存。
        string sourceKey = CacheKey(source);
        string targetKey = CacheKey(target);
        bool migrated = TryGetSizeCache(sourceKey, out SizeCacheEntry cached);
        InvalidateSizeCache(sourceKey);
        if (migrated)
        {
            SetSizeCache(targetKey, cached);
        }

        return backup with { Name = trimmed, Directory = target };
    }

    /// <summary>删除一个备份目录（递归删除，不可恢复；界面需二次确认）。</summary>
    /// <param name="backup">要删除的备份。</param>
    internal void Delete(SaveBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        string full = EnsureInsideSaves(backup.Directory);
        if (!_fileSystem.DirectoryExists(full))
        {
            throw new DirectoryNotFoundException($"备份不存在: {backup.Directory}");
        }

        try
        {
            _fileSystem.DeleteDirectory(full);
            // 备份已删除：体积缓存同步移除，避免长期累积无用键（C-20 / ）。
            InvalidateSizeCache(CacheKey(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"删除备份失败: {backup.Name}", ex);
        }
    }

    private string EnsureInsideSaves(string directory)
    {
        string full = Path.GetFullPath(directory);
        string savesBase = Path.GetFullPath(Path.Combine(_paths.Root, "Saves"));
        string? gameDirectory = Path.GetDirectoryName(full);
        string? savesDirectory = gameDirectory is null ? null : Path.GetDirectoryName(gameDirectory);
        if (savesDirectory is null
            || !string.Equals(savesDirectory, savesBase, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full).Length == 0)
        {
            throw new ArgumentException($"只能操作 Saves 目录下的备份: {directory}", nameof(directory));
        }

        return full;
    }

    private static string ValidateName(string? name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

        string trimmed = name.Trim();
        if (trimmed is "." or "..")
        {
            throw new ArgumentException("备份名称不能是 . 或 ..", parameterName);
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"备份名称包含非法字符: {name}", parameterName);
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            throw new ArgumentException("备份名称不能以点或空格结尾", parameterName);
        }

        if (trimmed.Length > 120)
        {
            throw new ArgumentException("备份名称过长（最多 120 个字符）", parameterName);
        }

        return trimmed;
    }

    /// <summary>优先从哈希清单累加文件大小；清单不存在、损坏或不可读时回退递归扫目录。</summary>
    /// <remarks>
    /// 成功路径只做一次清单元数据调用（长度 + 写入时间）；缓存为进程级（跨对话框实例）、
    /// 全局与单游戏双层上限的 LRU；缓存命中不读失效版本；负缓存有效期 <see cref="NegativeSizeCacheLifetime"/>，
    /// 时钟回拨视为过期；写入清单的路径主动失效并递增版本，扫描失败不写缓存。
    /// </remarks>
    /// <param name="directory">备份目录。</param>
    /// <param name="now">本次列表/备份操作统一取一次的时钟。</param>
    /// <param name="cancellationToken">取消令牌：回退扫描逐文件检查。</param>
    /// <returns>备份字节数；扫描失败时为 0 且不写缓存。</returns>
    internal long GetBackupSize(
        string directory,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        string key = CacheKey(directory);
        bool hasCached = TryGetSizeCache(key, out SizeCacheEntry cached);
        if (hasCached && cached.NegativeCachedAt is { } cachedAt)
        {
            // 只有明确的负缓存条目才走这里；
            // 时钟回拨（now < cachedAt）按过期处理，避免负缓存被无限延长。
            if (now >= cachedAt && now - cachedAt < NegativeSizeCacheLifetime)
            {
                return cached.Size;
            }
        }

        // 命中缓存（负缓存或清单时间一致的正缓存）不再读取失效版本；
        // 只有真正要读清单/扫目录时，才在 I/O 之前取版本与重置代次快照。
        long? version = null;
        long epoch = 0;
        string manifest = Path.Combine(directory, SaveBackupManifest.FileName);
        try
        {
            // 长度与写入时间来自同一次元数据调用；
            // 已知长度直接传给 Read，成功路径不再重复探测清单大小。
            Interlocked.Increment(ref _backupSizeMetadataProbeCount);
            (long length, DateTime write) = _fileSystem.GetFileLengthAndLastWriteTimeUtc(manifest);
            if (hasCached && cached.ManifestWriteUtc == write)
            {
                return cached.Size;
            }

            version = CurrentGameVersion(key);
            epoch = Volatile.Read(ref _sizeCacheEpoch);

            // 只统计真正读取清单内容的次数；读取前长度超限而不读内容时不计入。
            SaveBackupManifest.HashManifest? parsed = SaveBackupManifest.Read(
                manifest,
                _fileSystem,
                _manifestStore,
                out bool contentRead,
                length);
            if (contentRead)
            {
                Interlocked.Increment(ref _backupSizeManifestReadCount);
            }

            if (parsed is not null)
            {
                long size = parsed.Files.Sum(file => file.Size);
                TryStoreSizeCache(
                    key,
                    new SizeCacheEntry(size, write, NegativeCachedAt: null),
                    version.Value,
                    epoch);
                return size;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清单缺失（FileNotFoundException）、不可读或权限不足：回退目录扫描。
        }

        if (version is null)
        {
            version = CurrentGameVersion(key);
            epoch = Volatile.Read(ref _sizeCacheEpoch);
        }

        long? fallback = DirectorySize(directory, cancellationToken);
        if (fallback is not { } scanned)
        {
            // 扫描失败只影响本次显示，不能把 0 当成有效体积缓存 2 分钟。
            return 0;
        }

        TryStoreSizeCache(
            key,
            new SizeCacheEntry(scanned, ManifestWriteUtc: null, NegativeCachedAt: now),
            version.Value,
            epoch);
        return scanned;
    }

    private long? DirectorySize(string directory, CancellationToken cancellationToken)
    {
        // 原子诊断计数，用例断言负缓存有效期内连续刷新只扫描一次。
        Interlocked.Increment(ref _backupSizeDirectoryScanCount);
        try
        {
            long total = 0;
            foreach (string file in _fileSystem.EnumerateFilesLazy(directory, "*", SearchOption.AllDirectories))
            {
                // 惰性枚举，大目录不必先物化全部路径；逐项检查让取消尽快生效。
                cancellationToken.ThrowIfCancellationRequested();
                // 只排除备份根目录的清单/临时文件；子目录里的同名用户文件必须计入体积，
                // 与清单读取只认根目录的语义保持一致。
                string relative = Path.GetRelativePath(directory, file);
                if (SaveBackupManifest.IsManifestFile(relative))
                {
                    continue;
                }

                total += _fileSystem.GetFileLength(file);
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个备份大小算不出来不影响列表；返回 null 表示「失败」，由调用方决定显示 0 且不缓存。
            return null;
        }
    }

    /// <summary>缓存键统一归一化：Get/Store/失效/迁移都使用同一形式。</summary>
    private static string CacheKey(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>缓存键所属的游戏目录（备份目录的父目录），用于按游戏失效。</summary>
    private static string GameKey(string key) => Path.GetDirectoryName(key) ?? key;

    /// <summary>读取缓存键所属游戏的当前失效版本。</summary>
    private static long CurrentGameVersion(string key)
    {
        lock (SizeCacheGate)
        {
            return SizeCacheGameVersions.TryGetValue(GameKey(key), out SizeCacheVersion? version)
                ? version.Value
                : 0;
        }
    }

    /// <summary>尝试读取缓存并把命中项提升为最近使用。</summary>
    private static bool TryGetSizeCache(string key, out SizeCacheEntry entry)
    {
        lock (SizeCacheGate)
        {
            if (!SizeCacheIndex.TryGetValue(key, out LinkedListNode<SizeCacheNode>? node))
            {
                entry = default;
                return false;
            }

            SizeCacheOrder.Remove(node);
            SizeCacheOrder.AddFirst(node);
            entry = node.Value.Entry;
            return true;
        }
    }

    /// <summary>移除缓存键并递增所属游戏的失效版本，阻止并发扫描回写陈旧结果。</summary>
    private static void InvalidateSizeCache(string key)
    {
        lock (SizeCacheGate)
        {
            if (SizeCacheIndex.TryGetValue(key, out LinkedListNode<SizeCacheNode>? node))
            {
                RemoveSizeCacheNode(node);
            }

            string gameKey = GameKey(key);
            if (!SizeCacheGameVersions.TryGetValue(gameKey, out SizeCacheVersion? version))
            {
                version = new SizeCacheVersion();
                SizeCacheGameVersions[gameKey] = version;
            }

            version.Increment();
        }
    }

    /// <summary>以权威结果写入缓存（写清单/复制/迁移后），并保持 LRU 顺序。</summary>
    private static void SetSizeCache(string key, SizeCacheEntry entry)
    {
        lock (SizeCacheGate)
        {
            SetSizeCacheCore(key, entry);
        }
    }

    /// <summary>
    /// 仅在重置代次与所属游戏版本都未变化时写入，避免「扫描期间清单被写入/缓存被重置」
    /// 后回写旧体积。
    /// </summary>
    private static void TryStoreSizeCache(
        string key,
        SizeCacheEntry entry,
        long version,
        long epoch)
    {
        lock (SizeCacheGate)
        {
            if (Volatile.Read(ref _sizeCacheEpoch) != epoch)
            {
                return;
            }

            long current = SizeCacheGameVersions.TryGetValue(GameKey(key), out SizeCacheVersion? scoped)
                ? scoped.Value
                : 0;
            if (current != version)
            {
                return;
            }

            SetSizeCacheCore(key, entry);
        }
    }

    /// <summary>在已持有 <see cref="SizeCacheGate"/> 时写入并按 LRU 淘汰，写入保持 O(1)。</summary>
    private static void SetSizeCacheCore(string key, SizeCacheEntry entry)
    {
        string gameKey = GameKey(key);
        if (SizeCacheIndex.TryGetValue(key, out LinkedListNode<SizeCacheNode>? node))
        {
            node.Value.Entry = entry;
            SizeCacheOrder.Remove(node);
            SizeCacheOrder.AddFirst(node);
        }
        else
        {
            LinkedListNode<SizeCacheNode> created =
                SizeCacheOrder.AddFirst(new SizeCacheNode(key, gameKey, entry));
            SizeCacheIndex[key] = created;
            SizeCacheGameCounts[gameKey] = SizeCacheGameCounts.GetValueOrDefault(gameKey) + 1;
        }

        TrimGame(gameKey);
        TrimGlobal();
    }

    /// <summary>单个游戏超过 <see cref="MaxSizeCacheEntriesPerGame"/> 时淘汰该游戏最久未使用项。</summary>
    /// <remarks>每轮读取权威计数，删除后由 <see cref="RemoveSizeCacheNode"/> 统一递减；
    /// 正常情况下最多发生一次淘汰，循环用于容纳计数被并发改写的极端情况。</remarks>
    private static void TrimGame(string gameKey)
    {
        while (SizeCacheGameCounts.GetValueOrDefault(gameKey) > MaxSizeCacheEntriesPerGame)
        {
            LinkedListNode<SizeCacheNode> candidate = SizeCacheOrder.Last!;
            while (!string.Equals(candidate.Value.GameKey, gameKey, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Previous!;
            }

            RemoveSizeCacheNode(candidate);
        }
    }

    /// <summary>全局超过 <see cref="MaxSizeCacheEntries"/> 时淘汰整体最久未使用项。</summary>
    private static void TrimGlobal()
    {
        while (SizeCacheIndex.Count > MaxSizeCacheEntries)
        {
            RemoveSizeCacheNode(SizeCacheOrder.Last!);
        }
    }

    /// <summary>从索引、LRU 链表与游戏计数中同步移除节点。</summary>
    /// <remarks>计数与索引在 <see cref="SetSizeCacheCore"/> 中同步维护，因此这里直接读取计数；
    /// 缺失说明内部状态已损坏，让索引器抛出比静默返回更利于发现问题。</remarks>
    private static void RemoveSizeCacheNode(LinkedListNode<SizeCacheNode> node)
    {
        _ = SizeCacheIndex.Remove(node.Value.Key);
        SizeCacheOrder.Remove(node);
        string gameKey = node.Value.GameKey;
        int count = SizeCacheGameCounts[gameKey];
        if (count <= 1)
        {
            _ = SizeCacheGameCounts.Remove(gameKey);
        }
        else
        {
            SizeCacheGameCounts[gameKey] = count - 1;
        }
    }

    /// <summary>复制目录并在复制过程中同步计算 SHA-256，最后写出哈希清单（一次读盘，P-10 / ）。</summary>
    /// <param name="source">源存档目录。</param>
    /// <param name="destination">备份目录。</param>
    /// <returns>复制并写入清单的文件数与总字节数（不含清单自身）。</returns>
    private (int Count, long TotalBytes) CopyDirectoryWithManifest(string source, string destination)
    {
        List<CopiedFile> copied = VerifiedDirectoryCopy.Copy(
            _fileSystem,
            source,
            destination,
            SaveBackupManifest.IsManifestFile);
        long totalBytes = 0;
        List<BackupManifestEntry> manifestFiles = new(copied.Count);
        foreach (CopiedFile file in copied)
        {
            manifestFiles.Add(new BackupManifestEntry(file.RelativePath, file.Size, file.Sha256));
            totalBytes += file.Size;
        }

        manifestFiles.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        string destinationFull = Path.GetFullPath(destination);
        SaveBackupManifest.WriteManifest(
            destinationFull, manifestFiles, _clock, _fileSystem, _manifestStore);

        // 复制过程刚写入清单，复用统一缓存刷新。
        RefreshSizeCacheAfterManifestWrite(destinationFull, totalBytes);

        return (manifestFiles.Count, totalBytes);
    }

    private void TryMove(string source, string destination)
    {
        try
        {
            if (_fileSystem.DirectoryExists(source) && !_fileSystem.DirectoryExists(destination))
            {
                _fileSystem.MoveDirectory(source, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚失败不影响原始异常。
        }
    }
}
