using HunterBox.Application.Common;
using HunterBox.Application.Mods;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Mount;

/// <summary>散装文件挂载/撤出的变更统计。</summary>
/// <param name="Copied">直接写入的文件数（含禁用时从其它启用 MOD 重新复制的文件）。</param>
/// <param name="BackedUp">备份了游戏原文件的数量。</param>
/// <param name="Restored">从备份还原的数量。</param>
/// <param name="Deleted">删除的数量。</param>
public sealed record MountReport(int Copied, int BackedUp, int Restored, int Deleted)
{
    /// <summary>
    /// 本次操作中值得提示用户、但不足以中止流程的问题
    /// （例如：重叠文件需要从其它启用 MOD 重新复制，但该 MOD的源文件缺失）。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>本次从备份还原的游戏相对路径（清理残留时必须排除，不能再当HunterBox文件删除）。</summary>
    public IReadOnlyList<string> RestoredPaths { get; init; } = [];

    /// <summary>本次因备份缺失/归属无法证明而选择保留的游戏相对路径。</summary>
    public IReadOnlyList<string> PreservedPaths { get; init; } = [];
}

/// <summary>
/// 执行散装文件的真实挂载：把 <see cref="LooseFilePlanner"/> 产出的计划落到磁盘
/// （<c>docs/SPEC.md</c> 4.1）。
/// </summary>
/// <remarks>
/// <para>本类只做文件系统操作：复制、备份、还原、删除、清理空目录。
/// 「是否需要备份 / 是否该删」这类判断一律交给 Domain的 Planner。</para>
/// <para>
/// 启用/禁用均是「尽力事务」：执行前先做可诊断的预校验，执行中记录已改动文件，
/// 中途失败时逆序撤销（恢复备份/删除新写入文件），避免留下半套文件。
/// 启用路径的撤销日志统一由 <see cref="IUndoScope"/>（默认 <see cref="FileUndoJournal"/>）提供（A-10）；禁用路径恢复的是
/// 「MOD 源文件内容」而不是覆盖前的旧内容，保留独立的 <c>Undo</c> 恢复逻辑。
/// </para>
/// </remarks>
public sealed class MountService : ILooseFileMountService
{
    /// <summary>启用时复制单个文件的总尝试次数（含首次）。</summary>
    private const int CopyAttempts = 3;

    private readonly BoxPaths _paths;
    private readonly string _gameRoot;
    private readonly string _gameId;
    private readonly Func<IReadOnlyList<ModRecord>> _allModsProvider;
    private readonly IFileSystem _fileSystem;
    private readonly IContentFingerprint? _fingerprint;

    /// <summary>撤销作用域工厂（默认 <see cref="FileUndoJournal"/>；测试可替换以观察补偿动作）。</summary>
    private readonly IUndoScopeFactory _undoScopes;

    /// <summary>
    /// MOD → 游戏路径映射缓存：同一实例内文件清单/前缀未变时复用，
    /// 避免批量启用/禁用时对每个 MOD 的每个文件重复规范化路径（P-01）。
    /// </summary>
    /// <remarks>仅在单线程内使用（与挂载服务的既有约定相同）；文件清单引用变化时自动重建。</remarks>
    private readonly Dictionary<int, MappingEntry> _mappings = [];

    /// <summary>批量作用域内的占用索引；为 <see langword="null"/> 表示未在批量作用域。</summary>
    private Dictionary<string, List<OwnerEntry>>? _batchOwners;

    /// <summary>
    /// 批量作用域内的「已禁用 MOD 候选源」索引；为 <see langword="null"/> 表示未构建（首次需要时惰性构建）。
    /// </summary>
    private Dictionary<string, List<DisabledSource>>? _batchDisabledSources;

    /// <summary>候选索引的反向映射「MOD ID → 游戏路径集合」：启用成功后只移除该 MOD 的路径，避免 O(K×M)。</summary>
    private Dictionary<int, HashSet<string>>? _batchDisabledPaths;

    /// <summary>批量作用域内候选索引跳过记录时的告警；每次启用/禁用报告都会带出尚未展示的部分。</summary>
    private readonly List<string> _batchCandidateWarnings = [];

    /// <summary>已告警的 MOD ID：同一坏记录在批量作用域内只提示一次。</summary>
    private readonly HashSet<int> _batchCandidateWarnedIds = [];

    /// <summary>批量索引维护失败后置位：后续操作重建索引，避免继续使用半更新索引。</summary>
    private bool _batchIndexInvalid;

    /// <summary>装配挂载服务。</summary>
    /// <remarks>测试专用：生产组合根使用委托重载以读取最新记录。</remarks>
    /// <param name="paths">HunterBox磁盘布局（备份位置来自这里，按游戏隔离）。</param>
    /// <param name="gameRoot">游戏根目录，必须已存在。</param>
    /// <param name="gameId">当前游戏稳定 ID；只有同游戏的记录才会被当成文件占用方。</param>
    /// <param name="allMods">全部 MOD 记录（内部会过滤出当前游戏的记录）。</param>
    /// <param name="fileSystem">文件系统端口（失败回滚/清理用）。</param>
    /// <param name="fingerprint">内容指纹；无备份时用于证明目标文件仍属于该 MOD，缺失则保守保留。</param>
    /// <param name="undoScopes">
    /// 撤销作用域工厂；为 <see langword="null"/> 时使用基于 <see cref="FileUndoJournal"/> 的默认实现。
    /// </param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameRoot"/> 或 <paramref name="gameId"/> 为空。</exception>
    public MountService(
        BoxPaths paths,
        string gameRoot,
        string gameId,
        IReadOnlyList<ModRecord> allMods,
        IFileSystem fileSystem,
        IContentFingerprint? fingerprint = null,
        IUndoScopeFactory? undoScopes = null)
        : this(paths, gameRoot, gameId, () => allMods, fileSystem, fingerprint, undoScopes)
    {
    }

    /// <summary>用「按需读取记录」的委托装配挂载服务：批量期间记录被替换/新增时也能看到最新状态。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="gameRoot">游戏根目录，必须已存在。</param>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <param name="allModsProvider">返回全部 MOD 记录的委托（每次占用判断重新读取）。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="fingerprint">内容指纹。</param>
    /// <param name="undoScopes">撤销作用域工厂。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameRoot"/> 或 <paramref name="gameId"/> 为空。</exception>
    public MountService(
        BoxPaths paths,
        string gameRoot,
        string gameId,
        Func<IReadOnlyList<ModRecord>> allModsProvider,
        IFileSystem fileSystem,
        IContentFingerprint? fingerprint = null,
        IUndoScopeFactory? undoScopes = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(allModsProvider);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _paths = paths;
        _gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        _gameId = gameId.Trim();
        _allModsProvider = allModsProvider;
        _fileSystem = fileSystem;
        _fingerprint = fingerprint;
        _undoScopes = undoScopes ?? new FileUndoScopeFactory(fileSystem);
    }

    /// <summary>当前游戏的全部 MOD 记录（每次读取最新状态，跨游戏记录已过滤）。</summary>
    private IReadOnlyList<ModRecord> AllMods =>
        [.. _allModsProvider().Where(m => m.BelongsTo(_gameId))];

    /// <summary>进入批量作用域：占用索引只构建一次，候选索引在首次需要时惰性构建。</summary>
    internal void BeginBatch()
    {
        if (_batchOwners is not null)
        {
            return;
        }

        _batchOwners = BuildOwners([.. AllMods.Where(m => m.Enabled && m.Id != 0)]);
        _batchDisabledSources = null;
        _batchDisabledPaths = null;
        _batchCandidateWarnings.Clear();
        _batchCandidateWarnedIds.Clear();
        _batchIndexInvalid = false;
    }

    /// <summary>结束批量作用域：丢弃增量占用索引与候选索引；剩余候选告警返回给调用方展示。</summary>
    /// <returns>尚未随某项报告带出的候选告警，由批量调用方展示。</returns>
    internal IReadOnlyList<string> EndBatch()
    {
        List<string> remaining = [.. _batchCandidateWarnings];
        _batchOwners = null;
        _batchDisabledSources = null;
        _batchDisabledPaths = null;
        _batchCandidateWarnings.Clear();
        _batchCandidateWarnedIds.Clear();
        _batchIndexInvalid = false;
        return remaining;
    }

    /// <summary>批量作用域内启用成功后，把该 MOD 的文件加入占用索引，并从已禁用候选源里移除。</summary>
    /// <param name="mod">刚启用成功的 MOD。</param>
    private void NoteBatchEnabled(ModRecord mod)
    {
        if (_batchOwners is null)
        {
            return;
        }

        AddOwners(_batchOwners, mod);
        RemoveDisabledSourceCandidates(mod.Id);
    }

    /// <summary>把某个 MOD 的候选源从批量索引里移除（它已不再是「已禁用」状态）。</summary>
    /// <param name="modId">MOD ID。</param>
    private void RemoveDisabledSourceCandidates(int modId)
    {
        if (_batchDisabledSources is null || _batchDisabledSources.Count == 0)
        {
            return;
        }

        // 反向映射与候选索引在同一代码块成对构建/维护，
        // 未命中说明该 MOD 本来就不在候选索引里，直接返回；不存在需要全表扫描的回退分支。
        if (_batchDisabledPaths is not null)
        {
            if (!_batchDisabledPaths.Remove(modId, out HashSet<string>? modPaths))
            {
                return;
            }

            foreach (string gamePath in modPaths)
            {
                if (!_batchDisabledSources.TryGetValue(gamePath, out List<DisabledSource>? list))
                {
                    continue;
                }

                list.RemoveAll(entry => entry.Mod.Id == modId);
                if (list.Count == 0)
                {
                    _batchDisabledSources.Remove(gamePath);
                }
            }
        }
    }

    /// <summary>批量作用域内禁用成功后，把该 MOD 重新加回候选索引，供后续启用检测残留。</summary>
    /// <param name="mod">刚禁用成功的 MOD。</param>
    private void AddDisabledSourceCandidates(ModRecord mod)
    {
        if (_batchDisabledSources is null || _batchDisabledPaths is null)
        {
            // 候选索引尚未构建：后续惰性构建会读取到最新记录（此时该 MOD 已是禁用状态）。
            return;
        }

        IReadOnlyList<MappedFile>? mappedFiles = TryGetMappedFiles(mod);
        if (mappedFiles is null)
        {
            AddBatchCandidateWarning(mod, $"已禁用 MOD「{mod.DisplayName}」的文件清单含非法路径，残留检测已跳过该 MOD");
            return;
        }

        if (!_batchDisabledPaths.TryGetValue(mod.Id, out HashSet<string>? modPaths))
        {
            modPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _batchDisabledPaths[mod.Id] = modPaths;
        }

        foreach (MappedFile mapped in mappedFiles)
        {
            if (!_batchDisabledSources.TryGetValue(mapped.GamePath, out List<DisabledSource>? list))
            {
                list = [];
                _batchDisabledSources[mapped.GamePath] = list;
            }

            if (!list.Exists(entry => entry.Mod.Id == mod.Id))
            {
                list.Add(new DisabledSource(mod, mapped.FileInMod));
            }

            modPaths.Add(mapped.GamePath);
        }
    }

    /// <summary>记录一条批量候选告警；同一 MOD 只记一次，避免多次构建重复提示。</summary>
    /// <param name="mod">被跳过的记录。</param>
    /// <param name="message">告警文案。</param>
    private void AddBatchCandidateWarning(ModRecord mod, string message)
    {
        if (_batchCandidateWarnedIds.Add(mod.Id))
        {
            _batchCandidateWarnings.Add(message);
        }
    }

    /// <summary>把批量作用域内积累的候选索引告警并入本次报告；已展示的条目移除，后续新告警仍会出现。</summary>
    /// <param name="warnings">本次启用/禁用的告警列表。</param>
    private void AppendBatchCandidateWarnings(List<string> warnings)
    {
        if (_batchCandidateWarnings.Count == 0)
        {
            return;
        }

        warnings.AddRange(_batchCandidateWarnings);
        _batchCandidateWarnings.Clear();
    }

    /// <summary>批量索引属于加速结构：维护失败不能影响已经完成的文件操作，并标记失效供后续操作重建。</summary>
    /// <param name="update">索引维护动作。</param>
    /// <param name="warnings">本次操作的告警列表。</param>
    private void TryUpdateBatchIndex(Action update, List<string> warnings)
    {
        try
        {
            update();
        }
        catch (Exception ex)
        {
            _batchIndexInvalid = true;
            warnings.Add("批量索引更新失败（不影响本次文件操作；本次批量后续操作会重建索引，可能少提示）：" + ex.Message);
        }
    }

    /// <summary>批量索引已标记失效时重建占用索引与候选索引，避免继续使用半更新状态。</summary>
    private void RebuildBatchIndexesIfInvalid()
    {
        if (!_batchIndexInvalid || _batchOwners is null)
        {
            return;
        }

        _batchIndexInvalid = false;
        _batchOwners = BuildOwners([.. AllMods.Where(m => m.Enabled && m.Id != 0)]);
        _batchDisabledSources = null;
        _batchDisabledPaths = null;
    }

    /// <summary>批量作用域内禁用成功后，把本次实际撤出的文件从占用索引移除。</summary>
    /// <param name="mod">刚禁用成功的 MOD。</param>
    /// <param name="disabledGamePaths">本次实际撤出的游戏路径；不能传整个 MOD 的文件清单。</param>
    private void NoteBatchDisabled(ModRecord mod, IEnumerable<string> disabledGamePaths)
    {
        if (_batchOwners is null)
        {
            return;
        }

        foreach (string gamePath in disabledGamePaths)
        {
            if (!_batchOwners.TryGetValue(gamePath, out List<OwnerEntry>? list))
            {
                continue;
            }

            list.RemoveAll(entry => entry.Mod.Id == mod.Id);
            if (list.Count == 0)
            {
                _batchOwners.Remove(gamePath);
            }
        }

        // 重新加入候选索引，后续启用其它 MOD 时仍能识别它的残留内容。
        AddDisabledSourceCandidates(mod);
    }

    /// <summary>把一个 MOD 的全部映射文件加入占用索引。</summary>
    /// <param name="owners">占用索引。</param>
    /// <param name="mod">要加入的 MOD。</param>
    private void AddOwners(Dictionary<string, List<OwnerEntry>> owners, ModRecord mod)
    {
        foreach (MappedFile mappedFile in GetMappedFiles(mod))
        {
            AddOwner(owners, mod, mappedFile);
        }
    }

    /// <summary>把 MOD 内相对路径映射成游戏根目录相对路径。</summary>
    /// <param name="mod">所属 MOD。</param>
    /// <param name="fileInMod">MOD 内相对路径。</param>
    /// <returns>游戏根目录下的相对路径；<see cref="ModRecord.TargetDirectory"/> 非空时会加上该前缀。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">路径非法。</exception>
    public static string MapToGamePath(ModRecord mod, string fileInMod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        string normalized = BoxPaths.NormalizeRelative(fileInMod);
        return string.IsNullOrWhiteSpace(mod.TargetDirectory)
            ? normalized
            : BoxPaths.NormalizeRelative(mod.TargetDirectory + "/" + normalized);
    }

    /// <summary>
    /// 查询某 MOD 与其它已启用 MOD 会映射到同一游戏文件的路径（启用前冲突提示）。
    /// </summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <returns>冲突的游戏相对路径（去重，忽略大小写）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public IReadOnlyList<string> FindLooseFileConflicts(ModRecord mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        Dictionary<string, string> sources = BuildSourceMap(mod, files: null);
        MountContext context = BuildContext(mod).Context;
        return [.. sources.Keys
            .Where(context.ClaimedByOtherEnabledMod)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>启用：把 MOD的散装文件写进游戏根目录。</summary>
    /// <param name="mod">要启用的 MOD。</param>
    /// <param name="files">参与散装复制的文件清单；为 <see langword="null"/> 时用完整的 <see cref="ModRecord.RelativeFiles"/>。</param>
    /// <returns>实际执行的变更统计。</returns>
    /// <exception cref="InvalidOperationException">记录不属于当前游戏（<c>GameScope</c> 校验）。</exception>
    /// <exception cref="DirectoryNotFoundException">游戏根目录不存在。</exception>
    /// <exception cref="FileNotFoundException">MOD 文件在HunterBox目录里缺失（会在动任何文件之前抛出）。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限创建目录/写入目标文件。</exception>
    /// <exception cref="IOException">中途复制失败；已做的修改会尽量回滚后再抛出。</exception>
    public MountReport Enable(ModRecord mod, IEnumerable<string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        GameScope.EnsureBelongsTo(mod, _gameId);
        EnsureGameRoot();
        RebuildBatchIndexesIfInvalid();

        Dictionary<string, string> sources = BuildSourceMap(mod, files);
        EnsureSourcesPresent(mod, sources);

        // 本次启用只读取一次记录快照，占用索引与残留比对共用。
        List<string> warnings = [];
        IReadOnlyList<ModRecord> allMods = AllMods;
        MountContext context = BuildContext(mod, allMods, warnings).Context;
        IReadOnlyList<FileOperation> operations = LooseFilePlanner.PlanEnable(sources.Keys, context);

        // 回滚日志：每成功改动一个文件就记一条；失败时逆序撤销。
        // - BackupThenCopy 的前像就是刚写出的持久备份（已含游戏原文件）；
        // - 普通 Copy 若目标已存在，则先把旧内容复制到撤销目录。
        using IUndoScope undo = _undoScopes.Create(
            Path.Combine(_paths.TempDirectory, "undo-" + Guid.NewGuid().ToString("N")));
        int copied = 0;
        int backedUp = 0;
        // 候选索引按需惰性构建；批量作用域构建一次后复用，空结果同样记住。
        Dictionary<string, List<DisabledSource>> disabledSources =
            _batchDisabledSources ?? new Dictionary<string, List<DisabledSource>>(StringComparer.OrdinalIgnoreCase);
        bool disabledSourcesBuilt = _batchDisabledSources is not null;

        try
        {
            foreach (FileOperation operation in operations)
            {
                if (!sources.TryGetValue(operation.RelativePath, out string? source))
                {
                    throw new FileNotFoundException(
                        $"MOD「{mod.DisplayName}」的文件缺失，无法启用：{operation.RelativePath}",
                        operation.RelativePath);
                }

                string destination = BoxPaths.ResolveInside(_gameRoot, operation.RelativePath);
                EnsureParent(destination);

                if (operation.Kind == FileOperationKind.BackupThenCopy)
                {
                    // 只在存在已禁用同名候选时比对，先按长度预筛，避免整个游戏目录被整文件哈希；
                    // 索引在一次启用内只构建一次（空结果同样记住）。
                    if (!disabledSourcesBuilt)
                    {
                        if (_batchOwners is not null)
                        {
                            // 批量作用域首次需要时构建（纯禁用/纯 pak 批次不构建），
                            // 并记录反向映射供后续启用 O(该 MOD 文件数) 移除。
                            _batchDisabledPaths = [];
                            _batchDisabledSources = disabledSources = BuildDisabledSourceIndex(
                                allMods,
                                excludeModId: 0,
                                _batchCandidateWarnings,
                                _batchDisabledPaths,
                                _batchCandidateWarnedIds);
                        }
                        else
                        {
                            disabledSources = BuildDisabledSourceIndex(allMods, mod.Id, warnings);
                        }

                        disabledSourcesBuilt = true;
                    }

                    if (disabledSources.TryGetValue(operation.RelativePath, out List<DisabledSource>? leftoverCandidates)
                        && IsLeftoverFromDisabledMod(destination, leftoverCandidates, mod.Id, out string leftoverOwner))
                    {
                        warnings.Add(
                            $"目标文件与已禁用 MOD「{leftoverOwner}」的源文件内容一致，可能不是游戏原文件；"
                            + $"已按原样备份：{operation.RelativePath}");
                    }

                    string backup = _paths.BackupFile(_gameId, operation.RelativePath);
                    EnsureParent(backup);
                    bool backupExisted = _fileSystem.FileExists(backup);
                    // 先写临时文件再改名：备份路径上永远不会出现半截内容，避免下次被当成「游戏原文件」。
                    string tempBackup = backup + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        _fileSystem.CopyFile(destination, tempBackup, overwrite: true);
                        _fileSystem.MoveFile(tempBackup, backup, overwrite: true);
                    }
                    catch
                    {
                        // 备份写了一半就失败：清掉临时文件后原样抛出，游戏原文件未动。
                        _fileSystem.TryDeleteFile(tempBackup);
                        if (!backupExisted)
                        {
                            _fileSystem.TryDeleteFile(backup);
                        }

                        throw;
                    }

                    // 先登记再覆盖：即使覆盖中途失败，回滚也能从备份恢复游戏原文件。
                    undo.RecordRestore(destination, backup, deletePreImageOnRollback: !backupExisted);
                    CopyWithRetry(source, destination);
                    backedUp++;
                    copied++;
                    continue;
                }

                // 先登记再覆盖：覆盖失败时由回滚恢复旧内容（原本不存在则删除半截新文件）。
                if (_fileSystem.FileExists(destination))
                {
                    undo.BackupFile(destination);
                }
                else
                {
                    undo.RecordCreated(destination);
                }

                CopyWithRetry(source, destination);
                copied++;
            }

            undo.Commit();
            // 索引维护失败不能让已提交的文件操作进入「回滚已提交日志」的异常路径。
            TryUpdateBatchIndex(() => NoteBatchEnabled(mod), warnings);
            AppendBatchCandidateWarnings(warnings);
            return new MountReport(copied, backedUp, 0, 0) { Warnings = warnings };
        }
        catch (Exception ex)
        {
            List<string> rollbackWarnings = [];
            undo.Rollback(rollbackWarnings);
            // 失败路径也要把尚未带出的候选告警展示出来。
            List<string> pendingWarnings = [];
            AppendBatchCandidateWarnings(pendingWarnings);
            string pendingText = pendingWarnings.Count == 0
                ? string.Empty
                : Environment.NewLine + "提示：" + string.Join("；", pendingWarnings);
            if (rollbackWarnings.Count == 0)
            {
                throw new IOException(
                    "启用失败，本次已写入的文件已回滚；游戏目录保持启用前状态。" + Environment.NewLine + ex.Message
                    + pendingText,
                    ex);
            }

            throw new IOException(
                "启用失败，且回滚不完整，请关闭占用程序后重试或在「重新复制MOD」里修复："
                + Environment.NewLine + ex.Message
                + Environment.NewLine + string.Join(Environment.NewLine, rollbackWarnings)
                + pendingText,
                ex);
        }
    }

    /// <summary>禁用：把 MOD的文件从游戏根目录撤出（有备份则还原）。</summary>
    /// <param name="mod">要禁用的 MOD。</param>
    /// <param name="files">参与撤出的文件清单；为 <see langword="null"/> 时用完整的 <see cref="ModRecord.RelativeFiles"/>。</param>
    /// <returns>实际执行的变更统计。</returns>
    /// <exception cref="InvalidOperationException">记录不属于当前游戏。</exception>
    /// <exception cref="DirectoryNotFoundException">游戏根目录不存在，或撤出过程中目录被外部删除。</exception>
    /// <exception cref="IOException">中途失败；已做的修改会尽量回滚后再抛出。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限撤出/还原文件（原样向上抛出）。</exception>
    /// <remarks>
    /// 重叠文件的语义：如果还有其它启用中的 MOD 提供同一个游戏文件，
    /// 不能保留被禁用 MOD的内容，而是从那个 MOD 重新复制；备份保留到最后一个占用方禁用时才还原。
    /// </remarks>
    public MountReport Disable(ModRecord mod, IEnumerable<string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        GameScope.EnsureBelongsTo(mod, _gameId);
        EnsureGameRoot();
        RebuildBatchIndexesIfInvalid();

        Dictionary<string, string> sources = BuildSourceMap(mod, files);
        List<string> warnings = [];
        (MountContext context, Dictionary<string, List<OwnerEntry>> owners) = BuildContext(mod, warnings: warnings);
        List<FileOperation> operations = [.. LooseFilePlanner.PlanDisable(sources.Keys, context)];

        List<string> preservedPaths = [];

        // 备份缺失时无法证明目标文件属于 MOD：保留目标并告警，绝不把可能是游戏原文件的路径删掉。
        // 重建列表代替 RemoveAt，避免大量删除时的 O(n²) 移位（P-11）。
        List<FileOperation> kept = [];
        foreach (FileOperation operation in operations)
        {
            if (operation.Kind == FileOperationKind.RestoreBackup)
            {
                string backup = _paths.BackupFile(_gameId, operation.RelativePath);
                if (!_fileSystem.FileExists(backup))
                {
                    warnings.Add($"备份文件缺失，无法确认目标文件归属，已保留：{operation.RelativePath}");
                    preservedPaths.Add(operation.RelativePath);
                    continue;
                }
            }

            kept.Add(operation);
        }

        operations = kept;

        // 没有备份的 Delete 操作：必须能证明目标内容仍等于该 MOD 的源文件，否则保留。
        kept = [];
        foreach (FileOperation operation in operations)
        {
            if (operation.Kind != FileOperationKind.Delete)
            {
                kept.Add(operation);
                continue;
            }

            string destination = BoxPaths.ResolveInside(_gameRoot, operation.RelativePath);
            if (!_fileSystem.FileExists(destination))
            {
                kept.Add(operation);
                continue;
            }

            bool proven = false;
            if (_fingerprint is not null
                && sources.TryGetValue(operation.RelativePath, out string? source)
                && _fileSystem.FileExists(source))
            {
                try
                {
                    // 先比长度：长度不同时无需整文件哈希（P-11）。
                    if (_fileSystem.GetFileLength(destination) == _fileSystem.GetFileLength(source))
                    {
                        proven = string.Equals(
                            _fingerprint.GetFingerprint(destination),
                            _fingerprint.GetFingerprint(source),
                            StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"禁用时无法计算文件指纹：{operation.RelativePath}（{ex.Message}）");
                }
            }

            if (!proven)
            {
                warnings.Add($"无备份且无法证明目标文件属于该 MOD，已保留：{operation.RelativePath}");
                preservedPaths.Add(operation.RelativePath);
                continue;
            }

            kept.Add(operation);
        }

        operations = kept;

        List<string> restoredPaths = [];
        List<string> modifiedPaths = [];
        List<string> backupsToDelete = [];
        // 重叠文件回滚时要恢复「胜出方」的源内容，而不是被禁用 MOD 自己的源内容。
        Dictionary<string, string> restoreSources = new(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        int restored = 0;
        int copied = 0;

        try
        {
            // 1) 常规撤出：有备份还原，否则删除。
            foreach (FileOperation operation in operations)
            {
                string destination = BoxPaths.ResolveInside(_gameRoot, operation.RelativePath);

                if (operation.Kind == FileOperationKind.RestoreBackup)
                {
                    // 目标文件可能已被外部删除（游戏更新/手工清理）：仍要还原备份，
                    // 否则游戏原文件会永久留在备份目录，后续启用也不会重新备份（C-05）。
                    string backup = _paths.BackupFile(_gameId, operation.RelativePath);
                    // 覆盖前先登记：即使复制中途失败/目标被截断，Undo 也知道要恢复该路径。
                    modifiedPaths.Add(operation.RelativePath);
                    EnsureParent(destination);
                    _fileSystem.CopyFile(backup, destination, overwrite: true);
                    restoredPaths.Add(operation.RelativePath);
                    backupsToDelete.Add(backup);
                    restored++;
                    continue;
                }

                if (!_fileSystem.FileExists(destination))
                {
                    continue;
                }

                modifiedPaths.Add(operation.RelativePath);
                _fileSystem.DeleteFile(destination);
                deleted++;
            }

            // 2) 重叠文件：仍被其它启用 MOD 提供的文件，从那个 MOD 重新复制。
            // 复制前先登记回滚路径（与 RestoreBackup 分支一致）：覆盖中途失败时 Undo 才知道要恢复。
            foreach (string gamePath in sources.Keys)
            {
                if (!context.ClaimedByOtherEnabledMod(gamePath))
                {
                    continue;
                }

                if (TryCopyFromOtherEnabledMod(
                    mod,
                    gamePath,
                    owners,
                    (path, source) =>
                    {
                        modifiedPaths.Add(path);
                        restoreSources[path] = source;
                    },
                    out string winner))
                {
                    copied++;
                    continue;
                }

                warnings.Add(
                    $"文件仍被其它启用 MOD 需要，但找不到可用源文件，保留了当前内容：{gamePath}"
                    + (winner.Length > 0 ? $"（最后占用方：{winner}）" : string.Empty));
                preservedPaths.Add(gamePath);
            }

            // 3) 成功后才删备份：中途失败时备份还在，回滚可以依赖它判断原文件。
            foreach (string backup in backupsToDelete)
            {
                try
                {
                    if (_fileSystem.FileExists(backup))
                    {
                        _fileSystem.DeleteFile(backup);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"备份文件未能删除（已保留，可下次还原）：{backup}（{ex.Message}）");
                }
            }
        }
        catch
        {
            Undo(modifiedPaths, sources, restoreSources, warnings);
            throw;
        }

        // 4) 清理空目录（只删本次涉及的父目录，绝不动游戏根目录/备份根目录）。
        // 文件已经撤出/还原，单个目录清理失败只记 warning，不能中断其余目录的清理（C-03/）。
        // 父目录先去重，避免每个文件都重新枚举/删除同一个目录（P-12）。
        HashSet<string> gamePruneDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string gamePath in modifiedPaths)
        {
            string? directory = Path.GetDirectoryName(BoxPaths.ResolveInside(_gameRoot, gamePath));
            if (!string.IsNullOrEmpty(directory))
            {
                gamePruneDirectories.Add(directory);
            }
        }

        foreach (string directory in gamePruneDirectories)
        {
            PruneEmptyDirectoriesWithWarning(directory, _gameRoot, warnings);
        }

        HashSet<string> backupPruneDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string backup in backupsToDelete)
        {
            string? directory = Path.GetDirectoryName(backup);
            if (!string.IsNullOrEmpty(directory))
            {
                backupPruneDirectories.Add(directory);
            }
        }

        foreach (string directory in backupPruneDirectories)
        {
            // 备份目录的清理上界是备份根目录，而不是游戏根目录（C-25）。
            PruneEmptyDirectoriesWithWarning(directory, _paths.BackupRootDirectory, warnings);
        }

        // 只移除本次实际撤出的路径：保留路径（PreservedPaths）仍属于本 MOD 的占用，
        // 后续同批次启用其它 MOD 时不能再把它们当作游戏原文件备份。
        // 索引维护失败不能把已完成的禁用操作报成失败。
        TryUpdateBatchIndex(() => NoteBatchDisabled(mod, modifiedPaths), warnings);
        AppendBatchCandidateWarnings(warnings);
        return new MountReport(copied, 0, restored, deleted)
        {
            Warnings = warnings,
            RestoredPaths = restoredPaths,
            PreservedPaths = preservedPaths,
        };
    }

    /// <summary>构建「游戏路径 → 已禁用 MOD 候选源」索引；没有候选时返回空表而不是 <see langword="null"/>。</summary>
    /// <remarks>
    /// 非批量路径在本次启用首次出现「先备份再复制」时构建一次；批量作用域首次需要时构建并增量维护。
    /// 记录里含非法相对路径时跳过该 MOD 并告警，不影响本次启用。
    /// </remarks>
    /// <param name="allMods">本次启用开始时的记录快照。</param>
    /// <param name="excludeModId">正在启用的 MOD ID（跳过它自己的源文件）；批量作用域传 0。</param>
    /// <param name="warnings">可选：被跳过记录的告警收集目标。</param>
    /// <param name="pathsByMod">可选：「MOD ID → 游戏路径集合」反向映射的收集目标（批量移除用）。</param>
    /// <param name="warnedModIds">可选：批量作用域内已告警的 MOD ID，避免同一坏记录重复提示。</param>
    /// <returns>按游戏路径索引的候选源；没有已禁用记录时为空表。</returns>
    private Dictionary<string, List<DisabledSource>> BuildDisabledSourceIndex(
        IReadOnlyList<ModRecord> allMods,
        int excludeModId,
        List<string>? warnings = null,
        Dictionary<int, HashSet<string>>? pathsByMod = null,
        HashSet<int>? warnedModIds = null)
    {
        DisabledSourceIndexBuildCount++;
        Dictionary<string, List<DisabledSource>> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModRecord mod in allMods)
        {
            if (mod.Enabled || mod.Id == 0 || mod.Id == excludeModId)
            {
                continue;
            }

            IReadOnlyList<MappedFile>? mappedFiles = TryGetMappedFiles(mod);
            if (mappedFiles is null)
            {
                // 已禁用记录里残留的非法路径不影响本次启用，但必须让用户知道残留检测跳过了它。
                if (warnedModIds is null || warnedModIds.Add(mod.Id))
                {
                    warnings?.Add($"已禁用 MOD「{mod.DisplayName}」的文件清单含非法路径，残留检测已跳过该 MOD");
                }

                continue;
            }

            HashSet<string>? modPaths = null;
            if (pathsByMod is not null && !pathsByMod.TryGetValue(mod.Id, out modPaths))
            {
                modPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pathsByMod[mod.Id] = modPaths;
            }

            foreach (MappedFile mapped in mappedFiles)
            {
                if (!index.TryGetValue(mapped.GamePath, out List<DisabledSource>? list))
                {
                    list = [];
                    index[mapped.GamePath] = list;
                }

                list.Add(new DisabledSource(mod, mapped.FileInMod));
                modPaths?.Add(mapped.GamePath);
            }
        }

        return index;
    }

    /// <summary>已禁用候选源索引的构建次数（诊断用途，供测试断言批量复用）。</summary>
    internal int DisabledSourceIndexBuildCount { get; private set; }

    /// <summary>判断目标文件是否与某个已禁用 MOD 的同路径源文件内容一致。</summary>
    /// <remarks>
    /// 先按文件长度过滤候选，只有长度相同才计算目标哈希；目标最多计算一次。
    /// 生产环境注入的进程级 <see cref="HunterBox.Application.Paks.PakFingerprintCache"/> 会复用已算过的指纹，无候选时不读盘。
    /// </remarks>
    /// <param name="destination">目标文件的绝对路径。</param>
    /// <param name="candidates">同一游戏路径下的已禁用 MOD 候选源。</param>
    /// <param name="excludeModId">正在启用的 MOD ID（批量快照里可能仍把它算作候选）。</param>
    /// <param name="ownerName">内容一致的已禁用 MOD 显示名。</param>
    /// <returns>目标内容确实来自某个已禁用 MOD 时返回 <see langword="true"/>。</returns>
    private bool IsLeftoverFromDisabledMod(
        string destination,
        IReadOnlyList<DisabledSource> candidates,
        int excludeModId,
        out string ownerName)
    {
        ownerName = string.Empty;
        if (_fingerprint is null || candidates.Count == 0 || !_fileSystem.FileExists(destination))
        {
            return false;
        }

        long targetLength;
        try
        {
            targetLength = _fileSystem.GetFileLength(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目标读不到时沿用原流程：由后续备份/复制给出具体错误。
            return false;
        }

        // 长度不同不可能内容相同：先预筛，避免为每个待覆盖文件做整文件哈希。
        List<DisabledSource> sameLength = [];
        foreach (DisabledSource candidate in candidates)
        {
            if (candidate.Mod.Id == excludeModId)
            {
                continue;
            }

            string source = SourcePathOf(candidate);
            if (!_fileSystem.FileExists(source))
            {
                continue;
            }

            try
            {
                if (_fileSystem.GetFileLength(source) == targetLength)
                {
                    sameLength.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个候选源读不到时跳过，不影响其它候选与启用流程。
            }
        }

        if (sameLength.Count == 0)
        {
            return false;
        }

        string targetFingerprint;
        try
        {
            targetFingerprint = _fingerprint.GetFingerprint(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (DisabledSource candidate in sameLength)
        {
            try
            {
                if (string.Equals(
                    targetFingerprint,
                    _fingerprint.GetFingerprint(SourcePathOf(candidate)),
                    StringComparison.OrdinalIgnoreCase))
                {
                    ownerName = candidate.Mod.DisplayName;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个候选源读不到时跳过，不影响其它候选与启用流程。
            }
        }

        return false;
    }

    /// <summary>候选源文件的绝对路径。</summary>
    private string SourcePathOf(DisabledSource candidate) =>
        Path.Combine(
            _paths.FilesDirectory(candidate.Mod.Id),
            candidate.FileInMod.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>从「仍启用的其它 MOD」里找同一个游戏文件并复制回来；找不到可用源返回 false。</summary>
    /// <remarks>
    /// 占用关系来自 <see cref="BuildContext"/> 一次性建好的「游戏路径 → 占用方」索引，
    /// 这里只按路径查表，不再为每个冲突路径遍历全部 MOD 的文件（P-09）。
    /// </remarks>
    private bool TryCopyFromOtherEnabledMod(
        ModRecord mod,
        string gamePath,
        IReadOnlyDictionary<string, List<OwnerEntry>> owners,
        Action<string, string>? beforeCopy,
        out string winner)
    {
        winner = string.Empty;
        if (!owners.TryGetValue(gamePath, out List<OwnerEntry>? list) || list.Count == 0)
        {
            return false;
        }

        // 后加载的 MOD 生效（列表顺序靠后的先赢），所以从后往前找第一个源文件存在的。
        for (int i = list.Count - 1; i >= 0; i--)
        {
            OwnerEntry owner = list[i];
            if (owner.Mod.Id == mod.Id)
            {
                continue;
            }

            winner = owner.Mod.DisplayName;
            string source = Path.Combine(
                _paths.FilesDirectory(owner.Mod.Id),
                owner.FileInMod.Replace('/', Path.DirectorySeparatorChar));
            if (!_fileSystem.FileExists(source))
            {
                continue;
            }

            string destination = BoxPaths.ResolveInside(_gameRoot, gamePath);
            EnsureParent(destination);
            // 覆盖前先登记：复制中途失败时，Undo 才能用同一个胜出方源文件恢复。
            beforeCopy?.Invoke(gamePath, source);
            CopyWithRetry(source, destination);
            return true;
        }

        return false;
    }

    /// <summary>禁用失败后的补偿：把本次撤出的 MOD 文件重新复制回游戏目录（逐项失败只告警）。</summary>
    /// <remarks>
    /// A-07：这里没有改成 <see cref="FileUndoJournal"/>——它恢复的是「本次禁用前的 MOD 源文件内容」，
    /// 不是「覆盖前的旧内容」，且备份必须保留到整体禁用成功后才删除，无法用临时撤销目录表达；
    /// 因此只统一启用路径与 pak 路径的事务脚手架，禁用路径保留这份独立的恢复逻辑。
    /// </remarks>
    private void Undo(
        List<string> modifiedPaths,
        Dictionary<string, string> sources,
        Dictionary<string, string> restoreSources,
        List<string> warnings)
    {
        // 逆序恢复：把本次禁用前的 MOD 文件内容重新复制回游戏目录。
        // 备份此时尚未删除，所以 RestoreBackup 之后备份仍在，整体状态回到禁用前。
        // 重叠文件优先用「胜出方」的源文件恢复，避免把内容换回被禁用 MOD 的版本。
        for (int i = modifiedPaths.Count - 1; i >= 0; i--)
        {
            string gamePath = modifiedPaths[i];
            string destination = BoxPaths.ResolveInside(_gameRoot, gamePath);
            string? source = restoreSources.TryGetValue(gamePath, out string? restoreSource)
                ? restoreSource
                : sources.GetValueOrDefault(gamePath);
            if (source is not null && _fileSystem.FileExists(source))
            {
                try
                {
                    EnsureParent(destination);
                    _fileSystem.CopyFile(source, destination, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"禁用失败后回滚文件失败：{gamePath}（{ex.Message}）");
                }

                continue;
            }

            warnings.Add($"禁用失败后无法回滚（MOD 源文件缺失）：{gamePath}");
        }
    }

    private Dictionary<string, string> BuildSourceMap(ModRecord mod, IEnumerable<string>? files)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        string modFiles = _paths.FilesDirectory(mod.Id);
        if (files is null)
        {
            // 复用占用索引的映射缓存：同一批启用/禁用内不再逐文件规范化（P-01）。
            foreach (MappedFile mapped in GetMappedFiles(mod))
            {
                map[mapped.GamePath] =
                    Path.Combine(modFiles, mapped.FileInMod.Replace('/', Path.DirectorySeparatorChar));
            }

            return map;
        }

        foreach (string file in files)
        {
            string gamePath = MapToGamePath(mod, file);
            map[gamePath] = Path.Combine(modFiles, file.Replace('/', Path.DirectorySeparatorChar));
        }

        return map;
    }

    /// <summary>取某 MOD 的「游戏路径 → MOD 内文件」映射；文件清单/前缀未变时复用缓存（P-01）。</summary>
    /// <param name="mod">MOD 记录。</param>
    /// <returns>映射后的文件列表。</returns>
    private IReadOnlyList<MappedFile> GetMappedFiles(ModRecord mod)
    {
        if (_mappings.TryGetValue(mod.Id, out MappingEntry? cached)
            && ReferenceEquals(cached.Files, mod.RelativeFiles)
            && string.Equals(cached.TargetDirectory, mod.TargetDirectory, StringComparison.Ordinal))
        {
            return cached.Mapped;
        }

        List<MappedFile> mapped = new(mod.RelativeFiles.Count);
        foreach (string file in mod.RelativeFiles)
        {
            mapped.Add(new MappedFile(MapToGamePath(mod, file), file));
        }

        _mappings[mod.Id] = new MappingEntry(mod.RelativeFiles, mod.TargetDirectory, mapped);
        return mapped;
    }

    /// <summary>启用前校验所有源文件存在：缺失时在修改任何文件之前抛出可诊断异常。</summary>
    private void EnsureSourcesPresent(ModRecord mod, IReadOnlyDictionary<string, string> sources)
    {
        List<string> missing = [];
        foreach ((string gamePath, string source) in sources)
        {
            if (!_fileSystem.FileExists(source))
            {
                missing.Add(gamePath);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new FileNotFoundException(
            $"MOD「{mod.DisplayName}」的 {missing.Count} 个文件在HunterBox目录里缺失，无法启用（可能被杀毒软件隔离或手动删除）："
            + string.Join("、", missing.Take(5))
            + (missing.Count > 5 ? "…" : string.Empty)
            + "。请重新安装该 MOD。");
    }

    private void CopyWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= CopyAttempts; attempt++)
        {
            try
            {
                _fileSystem.CopyFile(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < CopyAttempts)
                {
                    Thread.Sleep(30);
                }
            }
        }

        throw new IOException(
            $"复制 MOD 文件失败（可能被游戏/杀毒软件占用）：{destination}（{last?.Message}）",
            last);
    }

    /// <summary>取某 MOD 的「游戏路径 → MOD 内文件」映射；记录含非法路径时返回 <see langword="null"/>。</summary>
    /// <param name="mod">MOD 记录。</param>
    /// <returns>映射结果；路径非法（索引被手工改坏）时为空。</returns>
    private IReadOnlyList<MappedFile>? TryGetMappedFiles(ModRecord mod)
    {
        try
        {
            return GetMappedFiles(mod);
        }
        catch (ArgumentException)
        {
            // 单条记录的非法路径不能拖垮整个占用索引/启用禁用。
            return null;
        }
    }

    private (MountContext Context, Dictionary<string, List<OwnerEntry>> Owners) BuildContext(
        ModRecord mod,
        IReadOnlyList<ModRecord>? allModsSnapshot = null,
        List<string>? warnings = null)
    {
        Dictionary<string, List<OwnerEntry>> owners;
        if (_batchOwners is not null)
        {
            // 批量作用域：索引在批量开始时构建一次，启用/禁用成功后增量维护。
            owners = _batchOwners;
        }
        else
        {
            // 非批量路径只读取一次最新记录，避免复制/过滤整表三次。
            IReadOnlyList<ModRecord> allMods = allModsSnapshot ?? AllMods;

            // 其它启用中的 MOD 一定算占用方；
            // 本 MOD 只有在「已经启用」时才算（重新复制场景），否则首次启用会把游戏原文件误判成自己的。
            HashSet<int> activeIds = [.. allMods
                .Where(m => m.Enabled && m.Id != mod.Id)
                .Select(m => m.Id)];
            if (mod.Enabled)
            {
                activeIds.Add(mod.Id);
            }

            owners = BuildOwners([.. allMods.Where(m => activeIds.Contains(m.Id))], warnings);
        }

        return (new MountContext
        {
            ExistsInGame = path => _fileSystem.FileExists(BoxPaths.ResolveInside(_gameRoot, path)),
            OwnerOf = path => owners.TryGetValue(path, out List<OwnerEntry>? list) && list.Count > 0
                ? list[0].Mod.Id
                : null,
            HasBackup = path => _fileSystem.FileExists(_paths.BackupFile(_gameId, path)),
            ClaimedByOtherEnabledMod = path =>
                owners.TryGetValue(path, out List<OwnerEntry>? list)
                && list.Exists(entry => entry.Mod.Id != mod.Id),
        }, owners);
    }

    /// <summary>按给定 MOD 集合构建「游戏路径 → 占用方」索引。</summary>
    /// <param name="mods">参与占用的 MOD（调用方已按启用状态过滤）。</param>
    /// <param name="warnings">可选：记录被跳过（文件清单含非法路径）时的告警，用于把问题告诉用户。</param>
    /// <returns>占用索引。</returns>
    private Dictionary<string, List<OwnerEntry>> BuildOwners(IReadOnlyList<ModRecord> mods, List<string>? warnings = null)
    {
        Dictionary<string, List<OwnerEntry>> owners = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModRecord other in mods)
        {
            if (TryGetMappedFiles(other) is not { } mappedFiles)
            {
                // 单条记录被手工改坏时跳过它，而不是让整个启用/禁用失败。
                warnings?.Add($"MOD「{other.DisplayName}」的文件清单含非法路径，已跳过其文件占用判断");
                continue;
            }

            foreach (MappedFile mappedFile in mappedFiles)
            {
                AddOwner(owners, other, mappedFile);
            }
        }

        return owners;
    }

    /// <summary>把一个映射文件加入占用索引（同一 MOD 的同一路径只记一次）。</summary>
    /// <param name="owners">占用索引。</param>
    /// <param name="mod">占用方 MOD。</param>
    /// <param name="mappedFile">映射文件。</param>
    private static void AddOwner(
        Dictionary<string, List<OwnerEntry>> owners,
        ModRecord mod,
        MappedFile mappedFile)
    {
        if (!owners.TryGetValue(mappedFile.GamePath, out List<OwnerEntry>? list))
        {
            list = [];
            owners[mappedFile.GamePath] = list;
        }

        if (!list.Exists(entry => entry.Mod.Id == mod.Id))
        {
            list.Add(new OwnerEntry(mod, mappedFile.FileInMod));
        }
    }

    private void EnsureGameRoot()
    {
        if (!_fileSystem.DirectoryExists(_gameRoot))
        {
            throw new DirectoryNotFoundException($"游戏目录不存在: {_gameRoot}");
        }
    }

    private void EnsureParent(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }
    }

    /// <summary>清理空目录并把单个目录的失败降级为 warning，不影响其余目录。</summary>
    /// <param name="directory">待清理的目录；可为 <see langword="null"/>。</param>
    /// <param name="stopAt">清理上界（游戏根目录或备份根目录）。</param>
    /// <param name="warnings">禁用报告的问题列表。</param>
    private void PruneEmptyDirectoriesWithWarning(string? directory, string stopAt, List<string> warnings)
    {
        try
        {
            DirectoryPruner.PruneEmptyDirectoriesUpward(directory, stopAt, _fileSystem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"空目录清理失败（文件已撤出，不影响本次禁用）：{ex.Message}");
        }
    }

    /// <summary>已禁用 MOD 的候选源（残留内容比对用）。</summary>
    /// <param name="Mod">候选 MOD 记录。</param>
    /// <param name="FileInMod">该 MOD 内映射到同一游戏路径的相对文件。</param>
    private sealed record DisabledSource(ModRecord Mod, string FileInMod);

    /// <summary>一个游戏相对路径的占用方：MOD 记录与该文件在 MOD 内的相对路径（P-09 冲突复制查表用）。</summary>
    /// <param name="Mod">占用方 MOD 记录。</param>
    /// <param name="FileInMod">该 MOD 内映射到该游戏路径的相对文件。</param>
    private sealed record OwnerEntry(ModRecord Mod, string FileInMod);

    /// <summary>MOD 内文件到游戏相对路径的映射结果（P-01 缓存用）。</summary>
    /// <param name="GamePath">映射后的游戏相对路径。</param>
    /// <param name="FileInMod">MOD 内相对路径。</param>
    private sealed record MappedFile(string GamePath, string FileInMod);

    /// <summary>单个 MOD 的映射缓存：文件清单实例与前缀未变时可直接复用。</summary>
    /// <param name="Files">生成缓存时的文件清单实例。</param>
    /// <param name="TargetDirectory">生成缓存时的安装前缀。</param>
    /// <param name="Mapped">映射结果。</param>
    private sealed record MappingEntry(
        IReadOnlyList<string> Files,
        string? TargetDirectory,
        IReadOnlyList<MappedFile> Mapped);
}
