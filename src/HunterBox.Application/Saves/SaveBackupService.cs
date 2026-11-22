using HunterBox.Application.Common;
using HunterBox.Domain.Layout;

namespace HunterBox.Application.Saves;

/// <summary>一个存档备份槽（目录 + 时间 + 大小）。</summary>
/// <param name="Name">备份目录名（默认 <c>yyyyMMdd-HHmmss</c> 时间戳，允许自定义名）。</param>
/// <param name="Directory">备份目录绝对路径。</param>
/// <param name="CreatedAt">备份时间（本地时间，优先取目录名里的时间戳，否则取目录创建时间）。</param>
public sealed record SaveBackup(string Name, string Directory, DateTimeOffset CreatedAt)
{
    /// <summary>备份内容总字节数；尚未计算（如刚创建的导入槽）时为 0。</summary>
    public long SizeBytes { get; init; }
}

/// <summary>备份哈希校验结果。</summary>
/// <param name="HasManifest">备份里是否有哈希清单（旧备份可能没有）。</param>
/// <param name="FileCount">清单中记录的文件数。</param>
/// <param name="Issues">结构化校验问题；通过时为空。</param>
public sealed record SaveBackupVerification(
    bool HasManifest,
    int FileCount,
    IReadOnlyList<SaveBackupIssue> Issues)
{
    /// <summary>是否校验通过（有清单且没有问题；仅测试/诊断断言使用）。</summary>
    public bool IsValid => HasManifest && Issues.Count == 0;
}

/// <summary>测试故障注入钩子：覆盖写盘成功后、覆盖后校验开始前调用（生产代码不实现）。</summary>
internal interface IBackupFaultHook
{
    /// <summary>覆盖写盘成功后调用。</summary>
    void AfterCommit();
}

/// <summary>
/// 存档备份与还原：把游戏存档目录整体复制到HunterBox <c>Saves/&lt;gameId&gt;/&lt;时间戳&gt;/</c> 下（保留目录格式）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>备份格式为目录复制，不做 zip：避免编码/密码坑； <c>Saves_&lt;appid&gt;/*.zip</c>
/// 由 <see cref="ILegacySaveImporter"/> 导入为同样的目录备份。</item>
/// <item>重命名/删除只允许作用于 <c>Saves/&lt;gameId&gt;/&lt;backup&gt;</c> 这一层，避免误删HunterBox里其它数据。</item>
/// <item>还原覆盖前由界面做二次确认；服务层负责「备份存在」「目标目录可创建」校验。</item>
/// <item>职责拆分（A-11）：目录存储与命名格式化在 <see cref="SaveBackupStore"/> /
/// <see cref="SaveBackupFormatter"/>，校验与还原在 <see cref="SaveBackupRestorer"/>，
/// 哈希清单读写与路径校验在 <see cref="SaveBackupManifest"/>；本类保留原公开 API 作为门面。</item>
/// </list>
/// </remarks>
public sealed class SaveBackupService
{
    /// <summary>还原快照的默认保留时长：7 天。</summary>
    public static readonly TimeSpan DefaultSnapshotRetention = TimeSpan.FromDays(7);

    private readonly SaveBackupStore _store;
    private readonly SaveBackupRestorer _restorer;

    /// <summary>装配存档服务。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="fileSystem">文件系统端口（失败清理用）。</param>
    /// <param name="manifestStore">哈希清单文本编解码端口（JSON 实现在 Infrastructure）。</param>
    /// <param name="clock">可替换时钟；省略时使用系统 UTC 时钟。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 本构造函数自 v1.5.136 起要求显式传入清单编解码端口，属于 API 破坏性变更；
    /// 当前仅由应用内组合根装配。若未来对外提供插件/脚本 API，应保留一个带默认 JSON 实现的兼容重载。
    /// </remarks>
    public SaveBackupService(
        BoxPaths paths,
        IFileSystem fileSystem,
        IBackupManifestStore manifestStore,
        IClock? clock = null)
        : this(paths, fileSystem, clock, manifestStore, faultHook: null)
    {
    }

    /// <summary>
    /// 测试专用构造：阶段 3（覆盖写盘）成功、阶段 4（覆盖后哈希校验）开始前调用
    /// <paramref name="faultHook"/>，用于稳定复现「覆盖后被改写/占用」。
    /// </summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="fileSystem">文件系统端口（失败清理用）。</param>
    /// <param name="clock">时钟；为 <see langword="null"/> 时用系统时钟。</param>
    /// <param name="manifestStore">哈希清单文本编解码端口。</param>
    /// <param name="faultHook">故障注入钩子；生产路径传 <see langword="null"/>。</param>
    internal SaveBackupService(
        BoxPaths paths,
        IFileSystem fileSystem,
        IClock? clock,
        IBackupManifestStore manifestStore,
        IBackupFaultHook? faultHook)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(manifestStore);
        _store = new SaveBackupStore(paths, clock ?? SystemClock.Instance, fileSystem, manifestStore);
        _restorer = new SaveBackupRestorer(fileSystem, manifestStore, faultHook);
    }

    /// <summary>某款游戏的备份根目录。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns><c>&lt;Root&gt;/Saves/&lt;gameId&gt;</c>。</returns>
    public string SavesRoot(string gameId) => _store.SavesRoot(gameId);

    /// <summary>异步列出已有备份（体积统计可能扫目录，放到后台线程，避免卡 UI）。</summary>
    /// <remarks>
    /// 没有哈希清单的备份会回退目录扫描，结果按进程级负缓存最多保留 2 分钟；
    /// 写清单、备份改名与删除会立即失效该缓存。滞后只影响绕过本进程的外部改动
    /// （例如在有效期内直接向无清单备份追加文件），手工删除清单会因元数据不匹配而立即重新扫描。
    /// </remarks>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>备份列表。</returns>
    public Task<IReadOnlyList<SaveBackup>> ListAsync(string gameId, CancellationToken cancellationToken = default) =>
        _store.ListAsync(gameId, cancellationToken);

    /// <summary>备份一个存档目录。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="saveDirectory">游戏存档目录（必须存在）。</param>
    /// <returns>新建的备份。</returns>
    /// <exception cref="DirectoryNotFoundException">存档目录不存在。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="saveDirectory"/> 包含HunterBox数据目录（备份目标会落在源目录内部，边枚举边复制会撑爆磁盘）。
    /// </exception>
    /// <exception cref="IOException">复制失败（磁盘满/权限/文件被占用等）。</exception>
    /// <exception cref="InvalidDataException">存档目录为空，没有可备份/可还原的文件。</exception>
    public SaveBackup Backup(string gameId, string saveDirectory) => _store.Backup(gameId, saveDirectory);

    /// <summary>创建一个空备份目录（<c>Saves/&lt;gameId&gt;/&lt;时间戳&gt;</c>），供旧存档 zip 导入等直接解压填充。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="createdAt">备份时间，决定目录名与列表排序。</param>
    /// <returns>已创建（且目录已存在）的备份槽；同秒冲突时追加 <c>-1</c>、<c>-2</c> 后缀。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空或含路径字符。</exception>
    /// <exception cref="IOException">目录创建失败（无权限等）。</exception>
    public SaveBackup CreateSlot(string gameId, DateTimeOffset createdAt) => _store.CreateSlot(gameId, createdAt);

    /// <summary>
    /// 用调用方在解压/复制时同步计算的哈希清单收尾备份，避免再次整目录读盘。
    /// </summary>
    /// <remarks>
    /// 生产导入链路只有本方法：旧存档 zip 在解压分块时同步计算哈希，
    /// 这里校验并一次性写入清单。条目路径会统一为正斜杠并校验：空、绝对路径、
    /// 含 <c>..</c>、重复路径、负大小或非 64 位十六进制哈希都会被拒绝。
    /// 清单仍由 <see cref="IBackupManifestStore"/> 编码并原子写入，写侧大小上限与普通备份一致。
    /// </remarks>
    /// <param name="backup">已填充内容的备份槽。</param>
    /// <param name="entries">解压/复制时计算的相对路径、大小与 SHA-256；不能为空。</param>
    /// <returns>带最新大小的备份。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="backup"/> 或 <paramref name="entries"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidDataException">条目为空、路径/哈希非法、存在重复路径，或清单超过 16MB。</exception>
    /// <exception cref="IOException">写入清单失败（占用/权限/磁盘）。</exception>
    public SaveBackup FinalizeImportedBackup(SaveBackup backup, IReadOnlyList<BackupManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"备份目录为空，没有可生成清单的文件: {backup.Directory}");
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<BackupManifestEntry> normalized = new(entries.Count);
        long totalBytes = 0;
        foreach (BackupManifestEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string path = entry.Path.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path)
                || Path.IsPathRooted(path)
                || SaveBackupManifest.ContainsParentSegment(path)
                || SaveBackupManifest.IsManifestFile(path))
            {
                // 与读侧一致，拒绝根清单自身/临时文件，避免清单自引用。
                throw new InvalidDataException($"导入清单包含非法路径: {entry.Path}");
            }

            if (entry.Size < 0 || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"导入清单条目非法: {entry.Path}");
            }

            if (!seen.Add(path))
            {
                throw new InvalidDataException($"导入清单存在重复条目: {entry.Path}");
            }

            normalized.Add(entry with { Path = path, Sha256 = entry.Sha256.ToUpperInvariant() });
            totalBytes += entry.Size;
        }

        return _store.FinalizeImportedBackup(backup, normalized, totalBytes);
    }

    /// <summary>把一个备份还原回存档目录（覆盖同名文件，不删除存档目录里备份没有的文件）。</summary>
    /// <remarks>
    /// 两阶段提交：先在目标同级影子目录完成复制与源校验，全部通过后才覆盖存档；
    /// 覆盖中途失败会用还原前的当前存档快照回滚。目标目录只在最后一步被改动，避免半新半旧。
    /// 生产路径只校验备份源（源哈希），覆盖后不回读目标；目标回读仅用于故障注入测试。
    /// </remarks>
    /// <param name="backup">要还原的备份。</param>
    /// <param name="saveDirectory">目标存档目录，不存在会被创建。</param>
    /// <returns>本次还原实际使用的校验结果。</returns>
    /// <exception cref="DirectoryNotFoundException">备份目录不存在。</exception>
    /// <exception cref="ArgumentException">目标存档目录位于备份目录内部（会自我复制）。</exception>
    /// <exception cref="IOException">目标目录创建、复制或回滚失败。</exception>
    /// <exception cref="SaveBackupVerificationException">备份源哈希校验失败（携带结构化问题与是否已回滚；生产路径不回读目标）。</exception>
    public SaveBackupVerification Restore(SaveBackup backup, string saveDirectory) =>
        _restorer.Restore(backup, saveDirectory);

    /// <summary>
    /// 清理存档父目录下过期的 <c>.restore-undo-*</c> / <c>.restore-staged-*</c> 快照，
    /// 并始终保留最近一份 undo 快照。
    /// </summary>
    /// <param name="saveDirectory">存档目录；快照位于其父目录。</param>
    /// <param name="retention">保留时长，必须为正。</param>
    /// <param name="fileSystem">文件系统端口（强制删除用）。</param>
    /// <param name="now">当前时间；默认取 UTC 现在，测试可注入固定值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除的快照/影子目录数；无法枚举父目录时返回 0。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> 不是正值。</exception>
    public static int PurgeStaleSnapshots(
        string saveDirectory,
        TimeSpan retention,
        IFileSystem fileSystem,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        SaveBackupRestorer.PurgeStaleSnapshots(saveDirectory, retention, fileSystem, now, cancellationToken);

    /// <summary>重命名一个备份目录（只改名称，内容与备份时间不变）。</summary>
    /// <param name="backup">要重命名的备份。</param>
    /// <param name="newName">新名称；非法字符、<c>.</c>/<c>..</c>、以点/空格结尾会被拒绝。</param>
    /// <returns>重命名后的备份（<see cref="SaveBackup.CreatedAt"/> 与 <see cref="SaveBackup.SizeBytes"/> 保留）。</returns>
    /// <exception cref="ArgumentException">名称为空/含非法字符，或备份路径不在 <c>Saves</c> 目录下。</exception>
    /// <exception cref="DirectoryNotFoundException">备份目录不存在。</exception>
    /// <exception cref="IOException">同名备份已存在或改名失败。</exception>
    public SaveBackup Rename(SaveBackup backup, string newName) => _store.Rename(backup, newName);

    /// <summary>删除一个备份目录（递归删除，不可恢复；界面需二次确认）。</summary>
    /// <param name="backup">要删除的备份。</param>
    /// <exception cref="ArgumentException">备份路径不在 <c>Saves</c> 目录下。</exception>
    /// <exception cref="DirectoryNotFoundException">备份目录不存在。</exception>
    /// <exception cref="IOException">删除失败（文件被占用/权限不足等）。</exception>
    public void Delete(SaveBackup backup) => _store.Delete(backup);

    /// <summary>把字节数格式化成界面用的「x.x KB / x.x MB / x.x GB / xx B」。</summary>
    /// <param name="bytes">字节数，负数按 0 处理。</param>
    /// <returns>带单位的可读文本。</returns>
    public static string FormatSize(long bytes) => SaveBackupFormatter.FormatSize(bytes);
}
