using HunterBox.Application.Common;
using HunterBox.Application.Mods;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Paks;

/// <summary>pak 挂载/撤出的变更统计。</summary>
/// <param name="Written">写入 <c>pak_mods</c>的文件数。</param>
/// <param name="Removed">从 <c>pak_mods</c> 删除的文件数。</param>
public sealed record PakMountReport(int Written, int Removed)
{
    /// <summary>空报告。</summary>
    public static PakMountReport Empty { get; } = new(0, 0);

    /// <summary>本次操作中值得提示用户、但不足以中止流程的问题（例如源 pak 文件缺失）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 把 pak 型 MOD 落进「游戏目录/pak_mods」（荒野 / 崛起，见 <c>docs/SPEC.md</c> 4.2）。
/// </summary>
/// <remarks>
/// <para>命名与冲突判断全部委托给 <see cref="PakPlanner"/>；本类只负责
/// 收集启用中的 pak、创建目录与复制，不清理未参与本次计划的文件（<c>pak_mods</c> 里的其它 pak
/// 可能是已禁用 MOD 留下的，删掉等于替用户删 MOD）；禁用时只删除记录能证明归属的 pak。</para>
/// <para>
/// 稳定性约定：
/// <list type="bullet">
/// <item>启用时本 MOD 自己的 pak 源文件缺失 → 在动任何文件之前抛出可读的
/// <see cref="FileNotFoundException"/>（不能半套生效）。</item>
/// <item>其它启用中 MOD的源文件缺失 → 跳过并记录 warning，不让整个启用/禁用被卡死。</item>
/// <item>禁用时源文件缺失 → 只按记录能证明的文件名删除；编号模式下无法证明归属时全部保留并警告，
/// 绝不按“内容不像别人的”推断删除（可能误删用户手放或其它 MOD 的 pak）。</item>
/// </list>
/// </para>
/// <para>
/// 写/删均带撤销日志：中途失败时恢复已覆盖/已移动的文件，避免留下半套 pak
/// （A-10：统一使用 Application/Common 的 <see cref="IUndoScope"/>，默认实现是 <see cref="FileUndoJournal"/>）。
/// </para>
/// <para>
/// 同名不同内容的旧 pak 一律移动到 <c>pak_mods/.hb-overwritten</c> 隔离目录并告警（不推断归属）；
/// 该目录为固定名，重复冲突复用同一目录，同名副本已存在时追加短后缀。隔离文件保留到用户手动删除；
/// 写入失败且回滚完整时，本次移动的隔离副本会随回滚一并删除。
/// </para>
/// </remarks>
public sealed class PakMountService : IPakMountService
{
    /// <summary>REFramework 新版要求的 pak 目录名。</summary>
    public const string PakModsDirectoryName = "pak_mods";

    /// <summary>撤消日志目录前缀（位于 <c>pak_mods</c> 内，保证同卷改名/复制，且不会以 .pak 结尾）。</summary>
    private const string UndoDirectoryPrefix = ".hb-undo-";

    /// <summary>同名异内容 pak 的隔离目录（固定名，重复冲突复用）。</summary>
    private const string OverwrittenDirectoryName = ".hb-overwritten";

    private readonly BoxPaths _paths;
    private readonly string _gameRoot;
    private readonly string _gameId;
    private readonly Func<IReadOnlyList<ModRecord>> _allModsProvider;
    private readonly IFileSystem _fileSystem;
    private readonly IContentFingerprint _fingerprint;

    /// <summary>撤销作用域工厂（默认 <see cref="FileUndoJournal"/>；测试可替换以观察补偿动作）。</summary>
    private readonly IUndoScopeFactory _undoScopes;

    /// <summary>共享指纹缓存的失效回调；为 <see langword="null"/> 时不清理共享缓存（调用方需自行保证指纹新鲜）。</summary>
    private readonly Action<string>? _forgetFingerprint;

    /// <summary>共享指纹缓存的登记回调；用于复制后立即登记目标指纹。</summary>
    private readonly Action<string, string>? _rememberFingerprint;

    /// <summary>装配 pak 挂载服务。</summary>
    /// <remarks>测试专用：生产组合根使用委托重载以读取最新记录。</remarks>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="gameRoot">游戏根目录。</param>
    /// <param name="gameId">当前游戏稳定 ID；只有同游戏的记录才会被当成 pak 占用方。</param>
    /// <param name="allMods">全部 MOD 记录（内部会过滤出当前游戏的记录）。</param>
    /// <param name="fileSystem">文件系统端口（清理撤销目录用）。</param>
    /// <param name="fingerprint">内容指纹实现。</param>
    /// <param name="undoScopes">
    /// 撤销作用域工厂；为 <see langword="null"/> 时使用基于 <see cref="FileUndoJournal"/> 的默认实现。
    /// </param>
    /// <param name="forgetFingerprint">共享指纹缓存的失效回调；为 <see langword="null"/> 时不清理共享缓存。</param>
    /// <param name="rememberFingerprint">共享指纹缓存的登记回调；为 <see langword="null"/> 时不登记。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public PakMountService(
        BoxPaths paths,
        string gameRoot,
        string gameId,
        IReadOnlyList<ModRecord> allMods,
        IFileSystem fileSystem,
        IContentFingerprint fingerprint,
        IUndoScopeFactory? undoScopes = null,
        Action<string>? forgetFingerprint = null,
        Action<string, string>? rememberFingerprint = null)
        : this(
            paths,
            gameRoot,
            gameId,
            () => allMods,
            fileSystem,
            fingerprint,
            undoScopes,
            forgetFingerprint,
            rememberFingerprint)
    {
    }

    /// <summary>用「按需读取记录」的委托装配 pak 挂载服务：批量期间记录被替换/新增时也能看到最新状态。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="gameRoot">游戏根目录。</param>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <param name="allModsProvider">返回全部 MOD 记录的委托。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="fingerprint">内容指纹实现。</param>
    /// <param name="undoScopes">撤销作用域工厂。</param>
    /// <param name="forgetFingerprint">共享指纹缓存的失效回调；为 <see langword="null"/> 时不清理共享缓存。</param>
    /// <param name="rememberFingerprint">共享指纹缓存的登记回调；为 <see langword="null"/> 时不登记。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public PakMountService(
        BoxPaths paths,
        string gameRoot,
        string gameId,
        Func<IReadOnlyList<ModRecord>> allModsProvider,
        IFileSystem fileSystem,
        IContentFingerprint fingerprint,
        IUndoScopeFactory? undoScopes = null,
        Action<string>? forgetFingerprint = null,
        Action<string, string>? rememberFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(allModsProvider);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(fingerprint);

        _paths = paths;
        _gameRoot = Path.GetFullPath(gameRoot);
        _gameId = gameId.Trim();
        _allModsProvider = allModsProvider;
        _fileSystem = fileSystem;
        _fingerprint = fingerprint;
        _forgetFingerprint = forgetFingerprint;
        _rememberFingerprint = rememberFingerprint;
        _undoScopes = undoScopes ?? new FileUndoScopeFactory(fileSystem);
    }

    /// <summary>当前游戏的全部 MOD 记录（每次读取最新状态，跨游戏记录已过滤）。</summary>
    /// <remarks>
    /// 每个公开操作最多读取一次：<see cref="PlanEnable"/> 与 <see cref="Disable"/> 各自只取一份快照，
    /// 批量启用复用已算好的 <see cref="PakPlan"/>，不会为每个 MOD 重复复制整表。
    /// </remarks>
    private IReadOnlyList<ModRecord> AllMods =>
        [.. _allModsProvider().Where(m => m.BelongsTo(_gameId))];
    /// <summary><c>pak_mods</c> 目录的绝对路径。</summary>
    public string PakModsDirectory => Path.Combine(_gameRoot, PakModsDirectoryName);

    /// <summary>计算「把该 MOD 也启用后」的 pak 放置计划，不落盘。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <param name="profile">该游戏的 pak 配置。</param>
    /// <param name="warnings">收集「其它 MOD 源文件缺失」等不致命问题的列表；可为 <see langword="null"/>。</param>
    /// <returns>放置计划；存在冲突时 <see cref="PakPlan.CanEnable"/> 为 <see langword="false"/>。</returns>
    /// <exception cref="FileNotFoundException">本 MOD 自己的 pak 源文件缺失（启用前必须报错）。</exception>
    public PakPlan PlanEnable(ModRecord mod, PakProfile profile, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(profile);
        GameScope.EnsureBelongsTo(mod, _gameId);

        EnsureOwnSourcesPresent(mod);
        return BuildPlan(mod, profile, warnings);
    }

    /// <summary>启用：把该 MOD的 pak 写入 <c>pak_mods</c>。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <param name="profile">该游戏的 pak 配置。</param>
    /// <returns>写入统计。</returns>
    /// <exception cref="PakConflictException">存在同名且内容不同的 pak。</exception>
    /// <exception cref="FileNotFoundException">本 MOD的 pak 源文件缺失。</exception>
    /// <exception cref="DirectoryNotFoundException">游戏根目录不存在，无法创建 <c>pak_mods</c>。</exception>
    public PakMountReport Enable(ModRecord mod, PakProfile profile)
    {
        List<string> warnings = [];
        PakPlan plan = PlanEnable(mod, profile, warnings);
        return EnablePlanned(plan, warnings);
    }

    /// <summary>按已算好的计划写入，避免再次枚举全部启用 MOD 的 pak。</summary>
    /// <param name="plan">已算好的放置计划。</param>
    /// <returns>写入统计。</returns>
    /// <exception cref="PakConflictException">计划存在同名且内容不同的 pak。</exception>
    /// <exception cref="DirectoryNotFoundException">游戏根目录不存在，无法创建 <c>pak_mods</c>。</exception>
    public PakMountReport Enable(PakPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return EnablePlanned(plan, []);
    }

    private PakMountReport EnablePlanned(PakPlan plan, List<string> warnings)
    {
        if (!plan.CanEnable)
        {
            throw new PakConflictException(plan.Conflicts);
        }

        if (plan.Assignments.Count == 0)
        {
            return PakMountReport.Empty with { Warnings = warnings };
        }

        if (!_fileSystem.DirectoryExists(_gameRoot))
        {
            throw new DirectoryNotFoundException($"游戏根目录不存在，无法写入 pak_mods：{_gameRoot}");
        }

        _fileSystem.CreateDirectory(PakModsDirectory);

        // 不清理「未参与本次计划」的文件：pak_mods 里可能有已禁用 MOD 留下的 pak，删掉等于替用户删 MOD。
        const int removed = 0;
        int written = 0;
        // 回滚日志：撤销目录放在 pak_mods 内（同卷改名/复制，且不会以 .pak 结尾，不会被当成 pak 扫描）。
        // 成功路径随 Dispose 清理；失败路径逆序还原并让共享指纹缓存里被覆盖/删除的路径立即失效。
        using IUndoScope undo = _undoScopes.Create(
            Path.Combine(PakModsDirectory, UndoDirectoryPrefix + Guid.NewGuid().ToString("N")),
            ForgetFingerprint);

        string? quarantineDirectory = null;
        List<string> quarantinedFiles = [];
        try
        {
            foreach (PakAssignment assignment in plan.Assignments)
            {
                string destination = Path.Combine(PakModsDirectory, assignment.FinalName);
                if (_fileSystem.FileExists(destination)
                    && Fingerprint(destination) == Fingerprint(assignment.Source.FullPath))
                {
                    continue;
                }

                if (_fileSystem.FileExists(destination))
                {
                    // 同名不同内容一律隔离并告警，不推断归属：
                    // 可能是本 MOD 的旧版本、已禁用 MOD 的残留或用户手放的 pak。
                    undo.BackupFile(destination);
                    quarantineDirectory ??= Path.Combine(PakModsDirectory, OverwrittenDirectoryName);
                    _fileSystem.CreateDirectory(quarantineDirectory);
                    string quarantined = Path.Combine(quarantineDirectory, assignment.FinalName);
                    if (_fileSystem.FileExists(quarantined))
                    {
                        // 固定目录避免每次冲突都新建 GUID 目录；同名副本已存在时追加短后缀保留两份。
                        quarantined += "." + Guid.NewGuid().ToString("N")[..8];
                    }

                    _fileSystem.MoveFile(destination, quarantined);
                    quarantinedFiles.Add(quarantined);
                    warnings.Add(
                        $"pak_mods 中已存在同名但内容不同的文件：{assignment.FinalName}"
                        + $"；旧文件已移动到 {quarantined}（保留至你手动删除），请确认是否需要保留。");
                }
                else
                {
                    undo.RecordCreated(destination);
                }

                _fileSystem.CopyFile(assignment.Source.FullPath, destination, overwrite: true);
                // 该路径刚被本服务改写：共享指纹缓存里的旧值（若有）立即失效。
                ForgetFingerprint(destination);
                if (_rememberFingerprint is not null)
                {
                    // 无登记回调时不计算源指纹，避免白读整个 pak。
                    RememberFingerprint(destination, Fingerprint(assignment.Source.FullPath));
                }

                written++;
            }

            undo.Commit();
            return new PakMountReport(written, removed) { Warnings = warnings };
        }
        catch (Exception ex)
        {
            List<string> rollbackWarnings = [];
            undo.Rollback(rollbackWarnings);
            if (rollbackWarnings.Count == 0)
            {
                // 回滚完整：目标已从撤销前像恢复，本次移动的隔离副本不再需要，避免失败后残留。
                foreach (string quarantined in quarantinedFiles)
                {
                    _fileSystem.TryDeleteFile(quarantined);
                }

                if (quarantineDirectory is not null)
                {
                    _fileSystem.TryDeleteDirectory(quarantineDirectory);
                }

                throw;
            }

            // 回滚不完整时撤销目录会被保留；路径在 warnings 里，必须交给用户而不是丢弃。
            throw new IOException(
                "pak 写入失败，且回滚不完整；请关闭占用程序后重试：\n"
                + ex.Message + "\n" + string.Join("\n", rollbackWarnings),
                ex);
        }
    }

    /// <summary>禁用：把该 MOD的 pak 从 <c>pak_mods</c> 撤出（其它 MOD 还需要则保留）。</summary>
    /// <param name="mod">待禁用的 MOD。</param>
    /// <param name="profile">该游戏的 pak 配置。</param>
    /// <returns>删除统计。</returns>
    /// <remarks>
    /// 源文件缺失时不再抛 <see cref="FileNotFoundException"/>：跳过该源并记录 warning。
    /// 只删除记录能证明归属的文件名（KeepOriginalName 模式下的源文件名）；编号模式等无法证明归属时
    /// 全部保留并警告，绝不按“内容不像别人的”做否定式推断删除。
    /// </remarks>
    public PakMountReport Disable(ModRecord mod, PakProfile profile)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(profile);
        GameScope.EnsureBelongsTo(mod, _gameId);

        List<string> warnings = [];
        if (!_fileSystem.DirectoryExists(PakModsDirectory))
        {
            return PakMountReport.Empty;
        }

        // 剩余启用中的 MOD（不含本 MOD）的源：缺失的跳过并告警，绝不让禁用卡死。
        List<ModRecord> remainingRecords = [.. AllMods.Where(m => m.Enabled && m.Id != mod.Id)];
        (List<PakSource> remaining, bool remainingIncomplete) = CollectSources(remainingRecords, warnings);

        // 用「不含本 MOD」的计划算出它当前占用的文件名，只删自己的文件，不重排其它 MOD。
        // pak_mods 的编号/指纹快照只构建一次，禁用过程中的两次计划共用它。
        PakModsState? state = profile.Mode == PakNamingMode.PatchNumbered ? CreatePakModsState(profile) : null;
        PakPlan withoutPlan = BuildPlanFromSources(remaining, profile, state);
        HashSet<string> keep = new(
            withoutPlan.Assignments.Select(a => a.FinalName),
            StringComparer.OrdinalIgnoreCase);
        if (withoutPlan.Conflicts.Count > 0)
        {
            // 剩余启用 MOD 之间已有同名冲突：冲突占用方的名字也要保留，宁可少删不能误删。
            foreach (PakConflict conflict in withoutPlan.Conflicts)
            {
                keep.Add(conflict.FileName);
            }

            warnings.Add($"剩余启用中的 MOD 之间存在 pak 同名冲突，禁用时已保守跳过相关文件：{string.Join("、", withoutPlan.Conflicts.Select(c => c.FileName).Distinct(StringComparer.OrdinalIgnoreCase))}");
        }

        if (remainingIncomplete)
        {
            // 剩余启用 MOD 的源文件缺失 ⇒ 无法证明 pak_mods 里某个编号/同名文件不属于它们。
            // 典型误删：KeepOriginalName 模式下 B 的源缺失且与本 MOD 同名，withoutPlan 里没有 B 的名字，
            // 本 MOD 的计划会把这个名字算成自己的 → 删掉 B 已部署的 pak（B 仍启用）。
            // 因此保守保留 pak_mods 内当前所有 .pak，等源文件恢复后再禁用。
            foreach (string path in _fileSystem.EnumerateFiles(PakModsDirectory))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                {
                    keep.Add(name);
                }
            }

            warnings.Add("部分剩余启用 MOD 的源文件缺失，禁用时已保守保留 pak_mods 内的现有文件。");
        }

        (List<PakSource> targetPresent, List<string> targetMissing) = CollectTargetSources(mod);

        HashSet<string> toDelete = new(StringComparer.OrdinalIgnoreCase);
        if (targetPresent.Count > 0)
        {
            PakPlan withPlan = BuildPlanFromSources([.. remaining, .. targetPresent], profile, state);
            foreach (PakAssignment assignment in withPlan.Assignments)
            {
                if (assignment.Source.ModId == mod.Id && !keep.Contains(assignment.FinalName))
                {
                    toDelete.Add(assignment.FinalName);
                }
            }
        }

        if (targetMissing.Count > 0)
        {
            warnings.Add($"MOD「{mod.DisplayName}」的 pak 源文件缺失，已跳过：{string.Join("、", targetMissing)}");
            if (profile.Mode == PakNamingMode.KeepOriginalName)
            {
                // 保留原名模式：最终文件名就是源文件名，按记录即可定位。
                foreach (string name in targetMissing)
                {
                    if (!keep.Contains(name))
                    {
                        toDelete.Add(name);
                    }
                }
            }
            else
            {
                // 编号模式下源文件缺失 ⇒ 无法证明 pak_mods 里哪些编号文件属于本 MOD。
                // 否定式推断（“内容不是剩余 MOD 的，就是本 MOD 的”）会误删用户手放/其它 MOD 的 pak，
                // 因此只保留并提示，等源文件恢复后再禁用。
                warnings.Add("无法确认 pak_mods 中残留文件的归属，已全部保留；请恢复源文件（或重新安装）后再禁用一次。");
            }
        }

        int removed = RemoveWithJournal(toDelete, warnings);
        return new PakMountReport(0, removed) { Warnings = warnings };
    }

    /// <summary>读取 pak 内容指纹；生产注入的进程级缓存负责「同一文件只算一次」。</summary>
    private string Fingerprint(string path) => _fingerprint.GetFingerprint(path);

    /// <summary>文件被本服务改写/删除后清掉共享指纹缓存，避免下一次操作命中旧内容。</summary>
    private void ForgetFingerprint(string path) => _forgetFingerprint?.Invoke(path);

    /// <summary>登记刚复制文件的内容指纹，让下一次单独启用/禁用直接命中共享缓存。</summary>
    private void RememberFingerprint(string path, string fingerprint) =>
        _rememberFingerprint?.Invoke(path, fingerprint);

    /// <summary>禁用时撤出（删除）pak：先把文件移入撤销目录，失败时逆序移回，避免删一半。</summary>
    /// <param name="names">要删除的 pak 文件名（<c>pak_mods</c> 内）。</param>
    /// <param name="warnings">收集回滚失败告警的列表。</param>
    /// <returns>实际删除的文件数。</returns>
    private int RemoveWithJournal(HashSet<string> names, List<string> warnings)
    {
        if (names.Count == 0)
        {
            return 0;
        }

        // 撤销目录放在 pak_mods 内（同卷改名，且目录名不以 .pak 结尾），成功路径随 Dispose 一起清理。
        using IUndoScope undo = _undoScopes.Create(
            Path.Combine(PakModsDirectory, UndoDirectoryPrefix + Guid.NewGuid().ToString("N")),
            ForgetFingerprint);
        int removed = 0;
        try
        {
            foreach (string name in names)
            {
                string target = Path.Combine(PakModsDirectory, name);
                if (!_fileSystem.FileExists(target))
                {
                    continue;
                }

                undo.MoveAside(target);
                // 该路径刚被本服务移走：共享指纹缓存里的旧值立即失效。
                ForgetFingerprint(target);
                removed++;
            }

            undo.Commit();
            return removed;
        }
        catch
        {
            // 恢复已移动的文件，避免删一半；单项恢复失败只告警，原始异常照常抛出。
            undo.Rollback(warnings);
            throw;
        }
    }

    /// <summary>把本 MOD 也算进启用集合，计算启用后的放置计划（启用前校验用）。</summary>
    private PakPlan BuildPlan(ModRecord mod, PakProfile profile, ICollection<string>? warnings)
    {
        // 只看当前游戏的记录：跨游戏的 Enabled=true 记录绝不能参与占用/去重判断。
        // 本 MOD 此刻通常还是 Enabled=false，但启用计划必须把它显式算进去。
        List<ModRecord> active = [.. AllMods
            .Where(m => m.Enabled || m.Id == mod.Id)
            .OrderBy(m => m.Index)];

        (List<PakSource> sources, _) = CollectSources(active, warnings);
        return BuildPlanFromSources(sources, profile);
    }

    /// <summary>收集记录的根层 pak 源；文件缺失时跳过并告警，并报告是否有缺失（返回值代替 ref）。</summary>
    /// <param name="records">参与计划的记录。</param>
    /// <param name="warnings">告警收集目标；可为 <see langword="null"/>。</param>
    /// <returns>可用的 pak 源与是否存在缺失文件。</returns>
    private (List<PakSource> Sources, bool Incomplete) CollectSources(
        IReadOnlyList<ModRecord> records,
        ICollection<string>? warnings)
    {
        bool incomplete = false;
        List<PakSource> sources = [];
        foreach (ModRecord record in records)
        {
            foreach (string file in PakFilesOf(record))
            {
                string fullPath = Path.Combine(
                    _paths.FilesDirectory(record.Id),
                    file.Replace('/', Path.DirectorySeparatorChar));
                if (!_fileSystem.FileExists(fullPath))
                {
                    incomplete = true;
                    warnings?.Add($"MOD「{record.DisplayName}」的 pak 源文件缺失，已跳过：{file}");
                    continue;
                }

                sources.Add(new PakSource(record.Id, record.DisplayName, file, fullPath));
            }
        }

        return (sources, incomplete);
    }

    private (List<PakSource> Present, List<string> Missing) CollectTargetSources(ModRecord mod)
    {
        List<PakSource> present = [];
        List<string> missing = [];
        foreach (string file in PakFilesOf(mod))
        {
            string fullPath = Path.Combine(
                _paths.FilesDirectory(mod.Id),
                file.Replace('/', Path.DirectorySeparatorChar));
            if (_fileSystem.FileExists(fullPath))
            {
                present.Add(new PakSource(mod.Id, mod.DisplayName, file, fullPath));
            }
            else
            {
                missing.Add(file);
            }
        }

        return (present, missing);
    }

    private List<string> PakFilesOf(ModRecord mod)
    {
        List<string> files = [];
        foreach (string file in mod.RelativeFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = BoxPaths.NormalizeRelative(file);
            }
            catch (ArgumentException)
            {
                // 记录里的非法路径不参与 pak 计划，避免越界写入 pak_mods（C-16）。
                continue;
            }

            // 根层 pak 判定统一走 ModelPathRules.IsRootPak，避免两处规则漂移。
            if (!ModelPathRules.IsRootPak(normalized))
            {
                continue;
            }

            _ = BoxPaths.ResolveInside(PakModsDirectory, normalized);
            files.Add(normalized);
        }

        return files;
    }

    private void EnsureOwnSourcesPresent(ModRecord mod)
    {
        List<string> missing = [.. PakFilesOf(mod).Where(file => !_fileSystem.FileExists(Path.Combine(
            _paths.FilesDirectory(mod.Id),
            file.Replace('/', Path.DirectorySeparatorChar))))];
        if (missing.Count == 0)
        {
            return;
        }

        throw new FileNotFoundException(
            $"MOD「{mod.DisplayName}」的 pak 源文件缺失，无法启用：{string.Join("、", missing)}"
            + "（可能被手动删除或杀毒软件隔离）。请在「重装MOD」里重新导入该包后再启用。");
    }

    private PakPlan BuildPlanFromSources(
        IReadOnlyList<PakSource> sources,
        PakProfile profile,
        PakModsState? state = null)
    {
        if (profile.Mode == PakNamingMode.KeepOriginalName)
        {
            PakPlacementOptions keepOptions = new()
            {
                Mode = profile.Mode,
                PatchPrefix = profile.PatchPrefix,
                PatchNumberFloor = profile.PatchNumberFloor,
            };
            return PakPlanner.Plan(sources, keepOptions, _fingerprint);
        }

        return PlanNumbered(sources, profile, state ?? CreatePakModsState(profile));
    }

    /// <summary><c>pak_mods</c> 的编号/内容快照：一次操作内多次计划复用它，避免重复整文件哈希。</summary>
    /// <param name="ExistingByContent">内容指纹 → 已有 pak 文件名。</param>
    /// <param name="UsedNumbers">已占用的 patch 编号。</param>
    /// <param name="MaxNumber">游戏根目录里看到的最高 patch 号（含静态下界）；<c>pak_mods</c> 的编号只进入 UsedNumbers。</param>
    private sealed record PakModsState(
        Dictionary<string, string> ExistingByContent,
        HashSet<int> UsedNumbers,
        int MaxNumber);

    /// <summary>扫描 <c>pak_mods</c> 与游戏根目录，构建编号/内容快照。</summary>
    /// <param name="profile">pak 配置。</param>
    /// <returns>本次操作内可复用的快照。</returns>
    private PakModsState CreatePakModsState(PakProfile profile)
    {
        Dictionary<string, string> existingByContent = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> usedNumbers = [];
        // 静态下界兜底；随后用游戏根目录里实际存在的 patch 号取更高值（游戏更新/DLC 不需要改配置）。
        int maxNumber = profile.PatchNumberFloor;

        if (_fileSystem.DirectoryExists(PakModsDirectory))
        {
            foreach (string file in _fileSystem.EnumerateFiles(PakModsDirectory))
            {
                string name = Path.GetFileName(file);
                if (!PakPlanner.TryParsePatchNumber(name, profile.PatchPrefix, out int number))
                {
                    continue;
                }

                usedNumbers.Add(number);
                try
                {
                    existingByContent[Fingerprint(file)] = name;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读不到就当作不存在（权限不足与 IO 错误同等对待，C-09）
                }
            }
        }

        // 游戏根目录里的 patch 也参与编号起点
        if (_fileSystem.DirectoryExists(_gameRoot))
        {
            foreach (string file in _fileSystem.EnumerateFiles(_gameRoot))
            {
                if (PakPlanner.TryParsePatchNumber(Path.GetFileName(file), profile.PatchPrefix, out int number))
                {
                    maxNumber = Math.Max(maxNumber, number);
                }
            }
        }

        return new PakModsState(existingByContent, usedNumbers, maxNumber);
    }

    /// <summary>
    /// 编号分配：已经在 <c>pak_mods</c> 里的 pak 沿用原编号（按内容指纹识别），
    /// 新 pak 才占用下一个编号。这样启用/禁用不会让其它 MOD的文件名来回跳动。
    /// 编号规则本身与 <see cref="PakPlanner"/> 共用 <see cref="PakNumbering.AssignPatchNumbers"/>，
    /// 这里只负责收集「已有编号 / 按内容复用映射 / 起始号」这些外部状态。
    /// </summary>
    private PakPlan PlanNumbered(IReadOnlyList<PakSource> sources, PakProfile profile, PakModsState state)
    {
        // 取「最小可用编号」而不是 max+1：否则反复禁用/启用会让编号一路涨（007→009→011…）。
        PakNumberingOptions numbering = new()
        {
            PatchPrefix = profile.PatchPrefix,
            FirstCandidateNumber = state.MaxNumber + 1,
            OccupiedNumbers = state.UsedNumbers,
            ExistingNamesByContent = state.ExistingByContent,
            ReuseExistingContent = true,
        };
        return new PakPlan(PakNumbering.AssignPatchNumbers(sources, numbering, Fingerprint), []);
    }
}
