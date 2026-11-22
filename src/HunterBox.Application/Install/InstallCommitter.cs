using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>
/// 安装提交：把 <see cref="InstallCopyPlan"/> 原子落盘并写记录（新增或重装替换）。
/// </summary>
/// <remarks>
/// <para>提交是原子的：先把选中文件完整写进 <c>Mods/.staging/&lt;guid&gt;/files</c>，
/// 全部成功后再改名为 <c>Mods/&lt;id&gt;/files</c>，然后才写记录。中途失败只删暂存目录，
/// 不会留下半成品；ID 只在全部复制成功后分配，分配后不会回退复用（复制阶段失败时尚未分配）。</para>
/// <para>重装模式：旧 <c>files</c> 先改名保留，新目录换入成功后才更新记录，失败恢复旧目录与旧记录，
/// 保留 Id / Index / GameId。</para>
/// </remarks>
internal sealed class InstallCommitter
{
    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly string _gameId;
    private readonly string _packageFingerprint;
    private readonly string _legacyPackageFingerprint;
    private readonly string _sourcePath;
    private readonly ModRecord? _reinstallTarget;

    /// <summary>用组合根里的布局/存储、文件系统端口与只读计划装配提交器。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="store">MOD 记录存储。</param>
    /// <param name="fileSystem">文件系统端口（失败回滚/清理用）。</param>
    /// <param name="plan">同一次安装的只读计划。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    internal InstallCommitter(BoxPaths paths, IModStore store, IFileSystem fileSystem, InstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(plan);

        _paths = paths;
        _store = store;
        _fileSystem = fileSystem;
        _gameId = plan.GameId;
        _packageFingerprint = plan.PackageFingerprint;
        _legacyPackageFingerprint = plan.LegacyPackageFingerprint;
        _sourcePath = plan.SourcePath;
        _reinstallTarget = plan.ReinstallTarget;
    }

    /// <summary>是否为「重装/更新」流程（提交时替换已有记录而不是新增）。</summary>
    public bool IsReinstall => _reinstallTarget is not null;

    /// <summary>提交一份安装计划：新增记录或替换重装目标，然后按设置归档安装源。</summary>
    /// <param name="plan">要提交的文件与记录字段。</param>
    /// <param name="archiveMode">安装源归档方式（0 无操作 / 1 复制 / 2 移动）。</param>
    /// <param name="cancellationToken">取消令牌：只在写暂存目录的阶段检查。</param>
    /// <param name="finalizeRecord">
    /// 记录落盘前的最后一次内存修改（如自动分类）；在保存前调用，与记录写入合并为一次保存。
    /// </param>
    /// <returns>安装结果。</returns>
    /// <exception cref="InvalidOperationException">指纹重复或重装目标已启用。</exception>
    /// <exception cref="OperationCanceledException">令牌在复制阶段被触发（不会留下半成品/记录）。</exception>
    public InstallResult Commit(
        InstallCopyPlan plan,
        InstallSourceMode archiveMode,
        CancellationToken cancellationToken,
        Action<ModRecord>? finalizeRecord = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // 取消只在真正写文件之前/复制阶段检查：一旦开始换目录就不再取消，保证记录与目录一致。
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReinstall)
        {
            EnsureNotInstalled();
        }

        InstallResult result = IsReinstall
            ? StageAndCommitReinstall(plan, cancellationToken, finalizeRecord)
            : StageAndCommit(plan, cancellationToken, finalizeRecord);

        return ApplyInstallSource(result, archiveMode);
    }

    private ModRecord CreateRecord(int id, InstallCopyPlan plan) => new()
    {
        Id = id,
        GameId = _gameId,
        Kind = plan.Kind,
        TargetDirectory = plan.TargetDirectory,
        Metadata = plan.Metadata,
        RelativeFiles = [.. plan.RelativeFiles],
        PackageFingerprint = _packageFingerprint,
    };

    /// <summary>把安装源按设置归档，并把归档路径写回记录。</summary>
    private InstallResult ApplyInstallSource(InstallResult result, InstallSourceMode mode)
    {
        List<string> warnings = [.. result.Warnings];
        InstallSourceArchiveResult archive = InstallSourceArchiver.Archive(
            _paths, _fileSystem, _sourcePath, result.Record.DisplayName, mode);
        warnings.AddRange(archive.Warnings);

        string? source = archive.InstallSource;
        if (!string.IsNullOrWhiteSpace(source)
            && !string.Equals(result.Record.InstallSource, source, StringComparison.OrdinalIgnoreCase))
        {
            result.Record.InstallSource = source;
            try
            {
                _store.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add("安装源已归档，但 MOD 记录的安装来源保存失败：" + ex.Message);
            }
        }

        return new InstallResult(result.Record, warnings);
    }

    /// <summary>
    /// 原子提交（新增）：写暂存目录 → 目录改名换入 → 写记录。失败只清理暂存目录，不留半成品/不写记录。
    /// </summary>
    private InstallResult StageAndCommit(
        InstallCopyPlan plan,
        CancellationToken cancellationToken,
        Action<ModRecord>? finalizeRecord)
    {
        string stagingRoot = Path.Combine(_paths.StagingDirectory, Guid.NewGuid().ToString("N"));
        string stagedFiles = Path.Combine(stagingRoot, "files");
        int id = -1;
        bool recordAdded = false;
        bool filesInstalled = false;

        try
        {
            CopyIntoStaging(plan.Copies, stagedFiles, cancellationToken);

            // 全部成功后才分配 Id，并把暂存目录改名为 Mods/<id>/files。
            id = _store.NextId();
            ModRecord record = CreateRecord(id, plan);
            MoveStagedFilesIntoPlace(id, stagedFiles, stagingRoot);
            filesInstalled = true;

            // 最后写记录；失败则把刚放进来的目录删掉。ID 不写入索引，但内存 NextId 已推进、不会回退复用。
            try
            {
                _store.Add(record);
                recordAdded = true;
                finalizeRecord?.Invoke(record);
                _store.Save();
            }
            catch
            {
                if (recordAdded)
                {
                    _store.Remove(id);
                }

                throw;
            }

            return new InstallResult(record, plan.Warnings);
        }
        catch
        {
            if (filesInstalled && id > 0)
            {
                _fileSystem.TryDeleteDirectory(_paths.ModDirectory(id));
            }

            throw;
        }
        finally
        {
            _fileSystem.TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// 原子提交（重装/更新）：新内容写暂存 → 旧 files 改名保留 → 新目录换入 →
    /// 更新记录并保存；失败恢复旧目录与旧记录，Id/Index/GameId 不变。
    /// </summary>
    private InstallResult StageAndCommitReinstall(
        InstallCopyPlan plan,
        CancellationToken cancellationToken,
        Action<ModRecord>? finalizeRecord)
    {
        ModRecord target = _reinstallTarget
            ?? throw new InvalidOperationException("内部错误：缺少重装目标");
        ModRecord? live = _store.Find(target.Id)
            ?? throw new InvalidOperationException($"MOD 不存在: {target.Id}");
        if (live.Enabled)
        {
            throw new InvalidOperationException("请先禁用 MOD 再重装");
        }

        int id = live.Id;
        string stagingRoot = Path.Combine(_paths.StagingDirectory, Guid.NewGuid().ToString("N"));
        string stagedFiles = Path.Combine(stagingRoot, "files");
        string modDirectory = _paths.ModDirectory(id);
        string filesDirectory = _paths.FilesDirectory(id);
        string oldDirectory = Path.Combine(modDirectory, ".old-" + Guid.NewGuid().ToString("N"));
        bool oldMoved = false;
        bool newMoved;
        List<string> warnings = [.. plan.Warnings];

        try
        {
            CopyIntoStaging(plan.Copies, stagedFiles, cancellationToken);

            // 先构造新记录字段（读取元数据不依赖旧目录），再动旧文件。
            ModRecord updated = CreateRecord(id, plan);

            ModKind oldKind = live.Kind;
            string? oldTargetDirectory = live.TargetDirectory;
            List<string> oldRelativeFiles = live.RelativeFiles;
            ModMetadata oldMetadata = live.Metadata;
            string? oldFingerprint = live.PackageFingerprint;
            DateTimeOffset oldInstalledAt = live.InstalledAt;

            _fileSystem.CreateDirectory(modDirectory);
            if (_fileSystem.DirectoryExists(filesDirectory))
            {
                _fileSystem.MoveDirectory(filesDirectory, oldDirectory);
                oldMoved = true;
            }

            try
            {
                _fileSystem.MoveDirectory(stagedFiles, filesDirectory);
                newMoved = true;
            }
            catch (Exception ex)
            {
                if (oldMoved)
                {
                    _fileSystem.TryDeleteDirectory(filesDirectory);
                    if (!TryRestoreDirectory(oldDirectory, filesDirectory))
                    {
                        // 回滚失败时保留真实原因（换入失败的 ex）作为 InnerException，便于排查。
                        throw new IOException(
                            "重装失败，且旧文件回滚不完整；旧目录仍保留在 " + oldDirectory
                            + "，请手工检查后恢复。原始错误：" + ex.Message, ex);
                    }
                }

                throw;
            }

            live.Kind = updated.Kind;
            live.TargetDirectory = updated.TargetDirectory;
            live.RelativeFiles = [.. updated.RelativeFiles];
            live.Metadata = updated.Metadata;
            live.PackageFingerprint = updated.PackageFingerprint;
            live.InstalledAt = DateTimeOffset.UtcNow;

            try
            {
                finalizeRecord?.Invoke(live);
                _store.Save();
            }
            catch (Exception ex)
            {
                // 任何 Save 失败都按失败处理：恢复旧记录/旧目录，再如实报告是否恢复成功。
                // 不能只回滚 IOException：其它异常同样会让内存与磁盘不一致。
                live.Kind = oldKind;
                live.TargetDirectory = oldTargetDirectory;
                live.RelativeFiles = oldRelativeFiles;
                live.Metadata = oldMetadata;
                live.PackageFingerprint = oldFingerprint;
                live.InstalledAt = oldInstalledAt;

                if (newMoved)
                {
                    _fileSystem.TryDeleteDirectory(filesDirectory);
                }

                bool restored = true;
                if (oldMoved)
                {
                    restored = TryRestoreDirectory(oldDirectory, filesDirectory);
                }

                if (!restored)
                {
                    throw new IOException(
                        "重装失败，且旧文件回滚不完整；旧目录仍保留在 " + oldDirectory
                        + "，请手工检查后恢复。" + Environment.NewLine + ex.Message, ex);
                }

                throw new IOException($"重装失败，已恢复旧文件与旧记录：{ex.Message}", ex);
            }

            if (!_fileSystem.TryDeleteDirectory(oldDirectory))
            {
                warnings = [.. warnings, $"旧文件目录未能删除（已保留，可手动清理）：{oldDirectory}"];
            }

            return new InstallResult(live, warnings);
        }
        finally
        {
            _fileSystem.TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>把文件按清单写进暂存目录；任何一步失败/取消都还没碰最终位置。</summary>
    /// <remarks>
    /// <para>暂存源文件都在本次安装自己的工作目录里（提交后即销毁），因此同卷优先用改名
    /// （<see cref="IFileSystem.MoveFile(string, string, bool)"/>）代替复制，消除「目录源先复制到 workDir、
    /// 提交时再复制到 staging」的 2× 写放大（P-04）；改名不可用（跨卷等）时回退为复制。</para>
    /// <para>同一源文件被多个映射引用、或多个源覆盖同一目标（FOMOD/多根等罕见情形）时退回逐文件复制，
    /// 保持「后者覆盖前者」的原语义，避免改名消耗掉后续映射还需要的源文件。</para>
    /// <para>失败/取消时把已改名的文件逆序移回工作目录：同一暂存仍可重试，
    /// 而最终换入依旧是「全部写完才整目录改名」的原子提交，不产生半成品。</para>
    /// </remarks>
    private void CopyIntoStaging(
        IReadOnlyList<(string SourceFullPath, string TargetRelative, string InstalledRelative)> copies,
        string stagedFiles,
        CancellationToken cancellationToken)
    {
        _fileSystem.CreateDirectory(stagedFiles);

        // 先物化并校验：源缺失在动任何文件之前抛出；源/目标不唯一时不走改名快路径。
        HashSet<string> distinctSources = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> distinctTargets = new(StringComparer.OrdinalIgnoreCase);
        bool moveAllowed = true;
        foreach ((string source, string target, _) in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fileSystem.FileExists(source))
            {
                throw new FileNotFoundException($"安装源文件缺失：{source}", source);
            }

            if (!distinctSources.Add(source) || !distinctTargets.Add(target))
            {
                moveAllowed = false;
            }
        }

        if (!moveAllowed)
        {
            CopyEach(copies, stagedFiles, cancellationToken);
            return;
        }

        List<(string Source, string Destination)> moved = [];
        try
        {
            foreach ((string source, string target, _) in copies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = BoxPaths.ResolveInside(stagedFiles, target);
                string? parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    _fileSystem.CreateDirectory(parent);
                }

                MoveOrCopy(source, destination, moved);
            }
        }
        catch
        {
            RestoreMovedSources(moved);
            throw;
        }
    }

    /// <summary>逐文件复制进暂存目录（源/目标不唯一、或改名不可用时的保底路径）。</summary>
    private void CopyEach(
        IReadOnlyList<(string SourceFullPath, string TargetRelative, string InstalledRelative)> copies,
        string stagedFiles,
        CancellationToken cancellationToken)
    {
        foreach ((string source, string target, _) in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fileSystem.FileExists(source))
            {
                throw new FileNotFoundException($"安装源文件缺失：{source}", source);
            }

            string destination = BoxPaths.ResolveInside(stagedFiles, target);
            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                _fileSystem.CreateDirectory(parent);
            }

            _fileSystem.CopyFile(source, destination, overwrite: true);
        }
    }

    /// <summary>同卷改名进暂存；改名失败（跨卷等）时回退复制，复制失败则抛出可诊断异常。</summary>
    private void MoveOrCopy(
        string source,
        string destination,
        List<(string Source, string Destination)> moved)
    {
        try
        {
            _fileSystem.MoveFile(source, destination, overwrite: true);
            moved.Add((source, destination));
        }
        catch (IOException)
        {
            _fileSystem.CopyFile(source, destination, overwrite: true);
        }
    }

    /// <summary>把已改名进暂存的源文件逆序移回工作目录（尽力而为，不覆盖主异常）。</summary>
    private void RestoreMovedSources(List<(string Source, string Destination)> moved)
    {
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            (string source, string destination) = moved[i];
            try
            {
                if (!_fileSystem.FileExists(destination) || _fileSystem.FileExists(source))
                {
                    continue;
                }

                string? parent = Path.GetDirectoryName(source);
                if (!string.IsNullOrEmpty(parent))
                {
                    _fileSystem.CreateDirectory(parent);
                }

                _fileSystem.MoveFile(destination, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 回移失败只影响「失败后重试同一个暂存」的能力；暂存目录仍由调用方清理，这里不覆盖主异常。
            }
        }
    }

    /// <summary>把暂存目录里的 files 改名为 Mods/&lt;id&gt;/files；遇到旧的孤儿目录先挪开，失败时恢复。</summary>
    private void MoveStagedFilesIntoPlace(int id, string stagedFiles, string stagingRoot)
    {
        string modDirectory = _paths.ModDirectory(id);
        string filesDirectory = _paths.FilesDirectory(id);
        string? orphan = null;

        if (_fileSystem.DirectoryExists(modDirectory))
        {
            // 没有任何记录指向这个目录（Id 由 NextId 得到）；可能是本失败残留，先挪开。
            orphan = Path.Combine(stagingRoot, "orphan");
            _fileSystem.MoveDirectory(modDirectory, orphan);
        }

        try
        {
            _fileSystem.CreateDirectory(modDirectory);
            _fileSystem.MoveDirectory(stagedFiles, filesDirectory);
        }
        catch
        {
            _fileSystem.TryDeleteDirectory(filesDirectory);
            _fileSystem.TryDeleteDirectory(modDirectory);
            if (orphan is not null
                && _fileSystem.DirectoryExists(orphan)
                && !_fileSystem.DirectoryExists(modDirectory))
            {
                try
                {
                    _fileSystem.MoveDirectory(orphan, modDirectory);
                }
                catch (IOException)
                {
                    // 恢复旧残留失败也不影响主异常；暂存目录会在调用方 finally 中清理。
                }
            }

            throw;
        }

        // 成功换入后，旧孤儿目录可以直接丢弃。
        if (orphan is not null)
        {
            _fileSystem.TryDeleteDirectory(orphan);
        }
    }

    private void EnsureNotInstalled()
    {
        // 指纹去重只在当前游戏内生效：同一个包允许在不同游戏各装一份。
        foreach (ModRecord existing in _store.ModsFor(_gameId))
        {
            string? fingerprint = existing.PackageFingerprint;
            if (string.IsNullOrEmpty(fingerprint))
            {
                continue;
            }

            // 升级前的记录没有 v2: 前缀：用同一包的旧算法指纹比较，避免升级后重复安装。
            bool legacy = !fingerprint.StartsWith("v2:", StringComparison.OrdinalIgnoreCase);
            string expected = legacy ? _legacyPackageFingerprint : _packageFingerprint;
            if (string.Equals(fingerprint, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"这个包已经安装过了：{existing.DisplayName}");
            }
        }
    }

    /// <summary>
    /// 把 <c>files</c> 换回旧目录；只有旧目录确实移回原位才返回 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 调用方只在旧目录已被成功改名（<c>.old-*</c>）时调用。恢复失败或旧目录已不存在时
    /// 必须返回 <see langword="false"/>，由调用方如实报告「回滚不完整」，不能宣称已恢复。
    /// </remarks>
    private bool TryRestoreDirectory(string oldDirectory, string filesDirectory)
    {
        try
        {
            if (_fileSystem.DirectoryExists(filesDirectory))
            {
                _fileSystem.DeleteDirectory(filesDirectory);
            }

            if (!_fileSystem.DirectoryExists(oldDirectory))
            {
                // 没有旧目录可恢复：如实上报未恢复（而不是假装成功）。
                return false;
            }

            _fileSystem.MoveDirectory(oldDirectory, filesDirectory);
            return _fileSystem.DirectoryExists(filesDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 恢复失败属于极端情况（磁盘错误/权限）；旧目录仍保留在 .old-* 下。
            _ = ex;
            return false;
        }
    }
}
