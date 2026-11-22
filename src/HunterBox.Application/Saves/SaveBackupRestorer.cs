using HunterBox.Application.Common;
using HunterBox.Domain.Layout;

namespace HunterBox.Application.Saves;

/// <summary>
/// 存档备份的校验与还原：哈希清单校验、两阶段覆盖、失败回滚与临时快照清理（A-11）。
/// </summary>
internal sealed class SaveBackupRestorer
{

    private readonly IFileSystem _fileSystem;
    private readonly IBackupManifestStore _manifestStore;
    private readonly IBackupFaultHook? _faultHook;

    /// <summary>装配还原器。</summary>
    /// <param name="fileSystem">文件系统端口（临时快照清理用）。</param>
    /// <param name="manifestStore">清单文本编解码端口。</param>
    /// <param name="faultHook">测试故障注入钩子；生产路径为 <see langword="null"/>。</param>
    internal SaveBackupRestorer(
        IFileSystem fileSystem,
        IBackupManifestStore manifestStore,
        IBackupFaultHook? faultHook)
    {
        _fileSystem = fileSystem;
        _manifestStore = manifestStore;
        _faultHook = faultHook;
    }

    /// <summary>把一个备份还原回存档目录（覆盖同名文件，不删除存档目录里备份没有的文件）。</summary>
    /// <param name="backup">要还原的备份。</param>
    /// <param name="saveDirectory">目标存档目录，不存在会被创建。</param>
    /// <returns>本次还原实际使用的校验结果。</returns>
    internal SaveBackupVerification Restore(SaveBackup backup, string saveDirectory)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        if (!_fileSystem.DirectoryExists(backup.Directory))
        {
            throw new DirectoryNotFoundException($"备份不存在: {backup.Directory}");
        }

        string sourceFull = Path.GetFullPath(backup.Directory);
        string targetFull = Path.GetFullPath(saveDirectory);
        VerifiedDirectoryCopy.EnsureTargetOutsideSource(
            targetFull,
            sourceFull,
            $"存档目录不能位于备份目录内: {saveDirectory}",
            nameof(saveDirectory));

        // P-12：哈希清单只读一次，源校验复用它；有清单但损坏/含非法路径时在改动存档前拒绝还原。
        // 生产路径不回读目标，只有故障注入路径会复用清单做覆盖后校验。
        SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.LoadForRestore(
            backup,
            _fileSystem,
            _manifestStore);
        SaveBackupVerification verification = manifest is null
            ? new SaveBackupVerification(false, 0, [])
            : new SaveBackupVerification(true, manifest.Files.Count, []);

        // 目标树含符号链接/junction 时写入会落到链接目标（存档目录之外），且无法安全回滚。
        // 在改动任何文件之前整体拒绝。
        try
        {
            if (_fileSystem.DirectoryExists(targetFull) && SafeFileTree.ContainsReparsePoint(_fileSystem, targetFull))
            {
                throw new IOException(
                    "存档目录包含符号链接或目录联接，无法安全还原（写入可能落到链接目标之外）。"
                    + "请先移除存档目录内的链接后重试: " + saveDirectory);
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                "无法检查存档目录是否包含符号链接，已停止还原（未改动任何文件）: " + saveDirectory,
                ex);
        }

        string parent = Path.GetDirectoryName(targetFull)
            ?? throw new IOException($"无法确定存档目录的父目录: {saveDirectory}");
        string token = Guid.NewGuid().ToString("N");
        string snapshot = Path.Combine(parent, ".restore-undo-" + token);
        string staged = Path.Combine(parent, ".restore-staged-" + token);
        // 回滚失败时快照是「还原前存档」的唯一完整副本，必须保留并在异常里给出路径（C-01）。
        bool keepSnapshot = false;

        try
        {
            // 1) 先写影子目录：复制与源校验合并成一次读盘（P-12），任何失败都不会碰当前存档。
            // 源校验通过之前不创建快照——备份损坏时不再白写一份完整存档。
            try
            {
                if (manifest is null)
                {
                    CopyDirectory(_fileSystem, sourceFull, staged, excludeManifest: true);
                }
                else
                {
                    List<SaveBackupIssue> sourceIssues = CopyDirectoryVerified(_fileSystem, sourceFull, staged, manifest);
                    if (sourceIssues.Count > 0)
                    {
                        throw new SaveBackupVerificationException(
                            new SaveBackupVerification(true, manifest.Files.Count, sourceIssues));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw CreateTempDirectoryFailure("创建还原影子副本", saveDirectory, ex);
            }

            // 2) 源校验通过后才创建目标目录与当前存档快照（还原失败时用于回滚）；
            // 临时目录建在存档同级目录，父目录只读/磁盘满会走到包装异常。
            try
            {
                _fileSystem.CreateDirectory(targetFull);
                CopyDirectory(_fileSystem, targetFull, snapshot, excludeManifest: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw CreateTempDirectoryFailure("创建当前存档快照", saveDirectory, ex);
            }

            // 3) 校验通过才覆盖当前存档；失败则用快照回滚
            // 一旦开始覆盖，快照就是还原前存档的唯一来源：直到成功或回滚完整成功前都不得删除。
            keepSnapshot = true;
            try
            {
                CopyDirectory(_fileSystem, staged, targetFull, excludeManifest: true);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                try
                {
                    RestoreSnapshot(_fileSystem, snapshot, targetFull);
                    keepSnapshot = false;
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    keepSnapshot = true;
                    throw new IOException(
                        "存档还原失败，且回滚当前存档也失败，请手工检查存档目录: " + saveDirectory
                        + Environment.NewLine + "还原前快照已保留: " + snapshot,
                        new AggregateException(writeError, rollbackError));
                }

                throw new IOException(
                    "存档还原失败，已回滚到还原前的当前存档: " + saveDirectory, writeError);
            }

            // 4) 生产路径只做源校验：覆盖字节来自已校验的影子副本，不再回读目标；
            // 故障注入路径仍读回目标，用于复现「覆盖后被外部改写」。
            if (manifest is not null && _faultHook is not null)
            {
                _faultHook.AfterCommit();
                List<SaveBackupIssue> copiedIssues = VerifyCopiedFiles(_fileSystem, manifest, targetFull);
                if (copiedIssues.Count > 0)
                {
                    try
                    {
                        RestoreSnapshot(_fileSystem, snapshot, targetFull);
                        keepSnapshot = false;
                    }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                    {
                        keepSnapshot = true;
                        throw new IOException(
                            "存档已复制但写入后哈希校验失败，且回滚当前存档也失败，请手工检查存档目录: " + saveDirectory
                            + Environment.NewLine + "还原前快照已保留: " + snapshot,
                            new AggregateException(rollbackError));
                    }

                    throw new SaveBackupVerificationException(
                        new SaveBackupVerification(true, manifest.Files.Count, copiedIssues),
                        rolledBack: true);
                }
            }

            // 还原完整成功：快照不再需要。
            keepSnapshot = false;
        }
        finally
        {
            _fileSystem.TryDeleteDirectory(staged);
            if (!keepSnapshot)
            {
                _fileSystem.TryDeleteDirectory(snapshot);
            }
        }

        return verification;
    }

    /// <summary>
    /// 清理存档父目录下过期的 <c>.restore-undo-*</c> / <c>.restore-staged-*</c> 快照，
    /// 并始终保留最近一份 undo 快照。
    /// </summary>
    /// <param name="saveDirectory">存档目录；快照位于其父目录。</param>
    /// <param name="retention">保留时长，必须为正。</param>
    /// <param name="fileSystem">文件系统端口（强制删除用）。</param>
    /// <param name="now">当前时间；默认取 UTC 现在，测试可注入固定值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功删除的快照/影子目录数（尽力清理，单项失败跳过）；无法枚举父目录时返回 0。</returns>
    internal static int PurgeStaleSnapshots(
        string saveDirectory,
        TimeSpan retention,
        IFileSystem fileSystem,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "保留时长必须为正。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? parent = Path.GetDirectoryName(Path.GetFullPath(saveDirectory));
        if (parent is null || !fileSystem.DirectoryExists(parent))
        {
            return 0;
        }

        DateTime cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - retention;
        int removed = 0;
        try
        {
            string[] snapshots = fileSystem.EnumerateDirectories(parent, ".restore-undo-*");
            if (snapshots.Length > 1)
            {
                string newest = snapshots.MaxBy(fileSystem.GetDirectoryLastWriteTimeUtc)!;
                foreach (string snapshot in snapshots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(snapshot, newest, StringComparison.OrdinalIgnoreCase)
                        || fileSystem.GetDirectoryLastWriteTimeUtc(snapshot) >= cutoff)
                    {
                        continue;
                    }

                    if (fileSystem.TryDeleteDirectoryForce(snapshot, cancellationToken))
                    {
                        removed++;
                    }
                }
            }

            foreach (string staged in fileSystem.EnumerateDirectories(parent, ".restore-staged-*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fileSystem.GetDirectoryLastWriteTimeUtc(staged) >= cutoff)
                {
                    continue;
                }

                if (fileSystem.TryDeleteDirectoryForce(staged, cancellationToken))
                {
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法枚举父目录时保持原状，不影响启动。
        }

        return removed;
    }

    /// <summary>把备份目录复制到影子目录，并在复制过程中逐文件校验源内容（一次读盘同时完成校验与复制，P-12）。</summary>
    private static List<SaveBackupIssue> CopyDirectoryVerified(
        IFileSystem fileSystem,
        string source,
        string destination,
        SaveBackupManifest.HashManifest manifest)
    {
        Dictionary<string, SaveBackupManifest.HashManifestEntry> expected =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (SaveBackupManifest.HashManifestEntry entry in manifest.Files)
        {
            expected[entry.Path.Replace('\\', '/')] = entry;
        }

        List<CopiedFile> copied = VerifiedDirectoryCopy.Copy(
            fileSystem,
            source,
            destination,
            SaveBackupManifest.IsManifestFile);
        List<SaveBackupIssue> issues = [];
        foreach (CopiedFile file in copied)
        {
            // 清单外文件（备份目录被拷入/替换的额外内容）必须让用户知道：
            // 旧实现静默复制，界面却显示“哈希校验通过”。
            if (!expected.TryGetValue(file.RelativePath, out SaveBackupManifest.HashManifestEntry? entry))
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.UnexpectedFile, file.RelativePath));
                continue;
            }

            expected.Remove(file.RelativePath);
            if (file.Size != entry.Size)
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.SizeMismatch, file.RelativePath));
            }
            else if (!string.Equals(file.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.HashMismatch, file.RelativePath));
            }
        }

        // 清单里有、但备份目录里不存在的文件：与旧 Verify 一样视为缺失。
        issues.AddRange(expected.Keys.Select(
            static relative => new SaveBackupIssue(SaveBackupIssueKind.MissingFile, relative)));
        return issues;
    }

    /// <summary>用已读入的清单校验目标目录里的文件（覆盖后再读回目标校验一次，P-12 复用同一份清单）。</summary>
    private static List<SaveBackupIssue> VerifyCopiedFiles(
        IFileSystem fileSystem,
        SaveBackupManifest.HashManifest manifest,
        string targetDirectory)
    {
        List<SaveBackupIssue> issues = [];
        foreach (SaveBackupManifest.HashManifestEntry entry in manifest.Files)
        {
            // 与 Verify 使用同一套路径校验：清单不可信时不能把校验读写带出备份目录。
            string relative = entry.Path;
            if (string.IsNullOrWhiteSpace(relative)
                || Path.IsPathRooted(relative)
                || SaveBackupManifest.ContainsParentSegment(relative))
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.InvalidPath, relative));
                continue;
            }

            string target;
            try
            {
                target = BoxPaths.ResolveInside(targetDirectory, relative);
            }
            catch (ArgumentException)
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.InvalidPath, relative));
                continue;
            }

            if (!fileSystem.FileExists(target))
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.PostCopyMissingFile, entry.Path));
                continue;
            }

            if (fileSystem.GetFileLength(target) != entry.Size
                || !string.Equals(
                    SaveBackupManifest.ComputeSha256(target, fileSystem),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new SaveBackupIssue(SaveBackupIssueKind.PostCopyMismatch, entry.Path));
            }
        }

        return issues;
    }

    /// <summary>把临时目录创建失败（父目录只读/权限不足、磁盘满、文件被占用等）转成带可读上下文的 <see cref="IOException"/>。</summary>
    private static IOException CreateTempDirectoryFailure(string phase, string saveDirectory, Exception inner) =>
        new(
            $"存档还原失败：无法{phase}，当前存档未被改动。"
            + "（存档目录的父目录只读/权限不足、磁盘空间不足，或文件被占用/不可读；"
            + "还原峰值需要约 2× 存档体积的额外空间）"
            + Environment.NewLine + "存档目录: " + saveDirectory,
            inner);

    /// <summary>用还原前快照恢复目标目录：先删掉失败覆盖新增的文件，再写回快照内容。</summary>
    private static void RestoreSnapshot(IFileSystem fileSystem, string snapshot, string target)
    {
        if (!fileSystem.DirectoryExists(snapshot))
        {
            return;
        }

        fileSystem.CreateDirectory(target);
        // 安全枚举：目标目录里的链接不参与「删除快照外文件」，避免删到链接目标里的内容。
        foreach (string file in SafeFileTree.EnumerateFiles(fileSystem, target))
        {
            string relative = Path.GetRelativePath(target, file);
            if (!fileSystem.FileExists(Path.Combine(snapshot, relative)))
            {
                fileSystem.DeleteFile(file);
            }
        }

        CopyDirectory(fileSystem, snapshot, target, excludeManifest: false);
    }

    /// <summary>递归复制目录内容。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="source">源目录。</param>
    /// <param name="destination">目标目录（不存在会被创建）。</param>
    /// <param name="excludeManifest">是否跳过备份哈希清单（还原到游戏目录时不带清单）。</param>
    private static void CopyDirectory(
        IFileSystem fileSystem,
        string source,
        string destination,
        bool excludeManifest)
    {
        // 与备份复制共用同一套「安全枚举 → 建父目录 → 写入」循环。
        VerifiedDirectoryCopy.CopyPlain(
            fileSystem,
            source,
            destination,
            relative => excludeManifest && SaveBackupManifest.IsManifestFile(relative));
    }

}
