using System.Globalization;
using HunterBox.Application.Activation;
using HunterBox.Application.Common;
using HunterBox.Application.Mount;
using HunterBox.Application.Paks;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Cleanup;

/// <summary>清理范围。</summary>
public enum CleanupScope
{
    /// <summary>只处理HunterBox安装进游戏目录的文件：安全禁用当前游戏的 MOD（有备份的还原游戏原文件）。</summary>
    BoxInstalledOnly,

    /// <summary>
    /// 在「安全禁用」的基础上，再按HunterBox MOD 记录删除仍残留的游戏目录文件；
    /// 不整目录删除、不删除记录之外的用户文件/目录、不删除备份。
    /// </summary>
    Everything,
}

/// <summary>清理结果（取消时报告已完成多少项，未处理项保持原状）。</summary>
/// <param name="Disabled">已安全禁用的 MOD 数量。</param>
/// <param name="Total">本次清理涉及的已启用 MOD 总数。</param>
/// <param name="Canceled">是否因取消而提前停止（取消后不再开始新动作；已完成的禁用/隔离保持生效）。</param>
public sealed record CleanupReport(int Disabled, int Total, bool Canceled)
{
    /// <summary>清理完成后的提示（如 pak_mods 里无法证明归属的 pak 已保留）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 「删除全部MOD文件」用例。
/// </summary>
/// <remarks>
/// <para>
/// 安全模型：清理**永远先安全禁用**当前游戏里启用中的 MOD —— 禁用会还原被覆盖的
/// 游戏原文件、删除HunterBox复制进去的文件、把记录置为未启用。
/// <see cref="CleanupScope.Everything"/> 随后只按记录删除「能证明归属且没有原文件备份」的残留；
/// 禁用阶段刚还原/选择保留的路径会被显式排除，绝不会再被当残留二次删除。
/// </para>
/// <para>
/// <see cref="CleanupScope.Everything"/> 只删除当前游戏 MOD 记录能证明归属、且没有原文件备份的残留文件；
/// 不整目录删除，也不删除备份目录（失败残留的备份可能是唯一可恢复来源）。
/// </para>
/// <para>只处理当前游戏：切换游戏后清理不会动另一个游戏的记录、文件与备份。</para>
/// <para>
/// 崩溃取舍：禁用阶段共用一个保存作用域，但每 <see cref="SaveHeartbeatInterval"/> 项做一次持久化心跳；
/// 进程被杀/断电时最多丢失最后一个心跳窗口内的状态，重启后可按游戏目录重新清理/重新启用恢复。
/// </para>
/// </remarks>
public sealed class ModCleanupService
{
    /// <summary>清理全部时的持久化心跳间隔：每禁用多少项落盘一次。</summary>
    private const int SaveHeartbeatInterval = 50;

    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly string _gameRoot;
    private readonly GameProfile _game;
    private readonly PakFingerprintCache _fingerprints;
    private readonly IFileSystem _fileSystem;
    private readonly PakNamingMode _pakMode;
    private readonly GameRunningGuard? _gameRunningGuard;
    private readonly IClock _clock;

    /// <summary>装配清理用例。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="store">MOD 记录存储。</param>
    /// <param name="gameRoot">当前游戏的根目录。</param>
    /// <param name="game">当前游戏配置。</param>
    /// <param name="fingerprints">
    /// 内容指纹缓存（同时用于散装文件比对与 pak 链路）：单一依赖，避免传入两个不同实例导致重复哈希。
    /// </param>
    /// <param name="fileSystem">文件系统端口（隔离/清理失败路径用）。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="gameRunningGuard">
    /// 「检测游戏运行」守卫；为 <see langword="null"/> 表示不检测（设置关闭或未接线）。
    /// </param>
    /// <param name="clock">可替换时钟；省略时使用系统时钟（隔离目录时间戳用）。</param>
    /// <exception cref="ArgumentNullException">paths/store/game/fingerprints/fileSystem 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="gameRoot"/> 为空。</exception>
    public ModCleanupService(
        BoxPaths paths,
        IModStore store,
        string gameRoot,
        GameProfile game,
        PakFingerprintCache fingerprints,
        IFileSystem fileSystem,
        PakNamingMode pakMode,
        GameRunningGuard? gameRunningGuard = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        _paths = paths;
        _store = store;
        _gameRoot = gameRoot;
        _game = game;
        _fingerprints = fingerprints;
        _fileSystem = fileSystem;
        _pakMode = pakMode;
        _gameRunningGuard = gameRunningGuard;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>执行清理（不可取消版本，供测试使用）。</summary>
    /// <param name="scope">清理范围。</param>
    /// <returns>被安全禁用的 MOD 数量。</returns>
    /// <exception cref="InvalidOperationException">设置开启「检测游戏运行」且游戏正在运行。</exception>
    /// <exception cref="IOException">文件操作失败（例如目录被占用）。</exception>
    internal int Cleanup(CleanupScope scope) => Cleanup(scope, CancellationToken.None, progress: null).Disabled;

    /// <summary>执行清理（可取消并上报「已处理/总数」）。</summary>
    /// <param name="scope">清理范围。</param>
    /// <param name="cancellationToken">
    /// 取消令牌：在 MOD 与 MOD 之间、以及执行残留删除前检查。取消时已完成的安全禁用保持生效，
    /// 未处理的 MOD 与游戏目录保持原状；不会把取消当成功。
    /// </param>
    /// <param name="progress">进度回调：<c>(已处理数, 总数)</c>；可能从后台线程调用。</param>
    /// <returns>清理结果（含已禁用数量与是否取消）。</returns>
    /// <exception cref="InvalidOperationException">设置开启「检测游戏运行」且游戏正在运行。</exception>
    /// <exception cref="IOException">文件操作或索引保存失败（心跳与最终保存依次失败时为 <see cref="AggregateException"/>）。</exception>
    /// <exception cref="AggregateException">
    /// 索引心跳保存失败（消息携带已禁用数量与 warnings）；最终保存也失败时包含两个异常。
    /// 实际类型为 <see cref="OperationBusinessAggregateException"/>，错误分类固定为 Business。
    /// </exception>
    public CleanupReport Cleanup(
        CleanupScope scope,
        CancellationToken cancellationToken,
        IProgress<(int Done, int Total)>? progress = null)
    {
        _gameRunningGuard?.EnsureGameNotRunning();

        ModActivationService activation = new(
            _paths,
            _store,
            _gameRoot,
            _game,
            _fingerprints,
            new ActivationOptions { FileSystem = _fileSystem, PakFingerprintCache = _fingerprints });

        // 只处理当前游戏的记录：切换游戏后清理不能动另一个游戏的启用状态/文件/备份。
        List<ModRecord> enabled = [.. _store.ModsFor(_game.Id).Where(m => m.Enabled)];
        HashSet<string> protectedPaths = new(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        int disabled = 0;

        // 整批禁用共用一个保存作用域，逐项只维护增量占用索引，结束时统一保存一次；
        // 取消或提前返回时由作用域释放落盘，已完成的禁用不会丢。
        // 每 50 项做一次持久化心跳，进程被杀/断电时最多丢失一个心跳窗口的状态。
        Exception? heartbeatSaveError = null;
        try
        {
            using (activation.BeginSaveBatch())
            {
                for (int index = 0; index < enabled.Count; index++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new CleanupReport(disabled, enabled.Count, true) { Warnings = warnings };
                    }

                    // 禁用内部会「有备份则还原原文件、否则删除HunterBox文件」，是唯一直接删除/还原的路径；
                    // 残留隔离阶段则是「先复制到隔离目录、再删除游戏文件」（N-06）。
                    ActivationReport report = activation.Disable(enabled[index], _pakMode, cancellationToken);
                    protectedPaths.UnionWith(report.Loose.RestoredPaths);
                    protectedPaths.UnionWith(report.Loose.PreservedPaths);
                    // 禁用阶段的保守保留（备份缺失/无法证明归属）必须让用户看到，不能只保留在内部报告里。
                    warnings.AddRange(report.Warnings);
                    disabled++;
                    if (disabled % SaveHeartbeatInterval == 0)
                    {
                        try
                        {
                            activation.FlushSaveBatch();
                        }
                        catch (Exception ex)
                        {
                            // 记住心跳失败的根因，避免 finally 里的最终保存异常把它覆盖。
                            heartbeatSaveError = ex;
                            throw;
                        }
                    }

                    progress?.Report((index + 1, enabled.Count));
                }
            }
        }
        catch (Exception ex) when (heartbeatSaveError is not null)
        {
            // 无论最终保存是否成功，异常消息都携带已禁用数量与保守保留警告。
            bool finalSaveFailed = !ReferenceEquals(heartbeatSaveError, ex);
            string detail = warnings.Count == 0
                ? string.Empty
                : Environment.NewLine + "警告：" + string.Join("；", warnings);
            string phase = finalSaveFailed ? "心跳保存与最终保存都失败" : "心跳保存失败";
            string message =
                $"清理过程中索引保存失败：已禁用 {disabled}/{enabled.Count} 个 MOD，{phase}（详见日志）。" + detail;
            throw finalSaveFailed
                ? new OperationBusinessAggregateException(message, heartbeatSaveError, ex)
                : new OperationBusinessAggregateException(message, heartbeatSaveError);
        }

        if (scope == CleanupScope.Everything)
        {
            // 取消后不再做破坏性的残留删除（已禁用的部分保持安全状态）。
            if (cancellationToken.IsCancellationRequested)
            {
                return new CleanupReport(disabled, enabled.Count, true) { Warnings = warnings };
            }

            (int quarantined, string? quarantineRoot) =
                QuarantineResidualModFiles(cancellationToken, protectedPaths, warnings, out bool canceled);
            if (quarantineRoot is not null)
            {
                // 放在首位：UI 只展示前几条时，隔离目录路径不能被其它 warning 挤掉。
                warnings.Insert(
                    0,
                    $"已将 {quarantined} 个无法区分「HunterBox残留 / 游戏原文件」的文件移入隔离目录：{quarantineRoot}；"
                    + "确认无用后可手动删除。");
            }

            // 取消前已隔离的文件也必须告诉用户落在哪里。
            if (canceled)
            {
                return new CleanupReport(disabled, enabled.Count, true) { Warnings = warnings };
            }
        }

        if (scope == CleanupScope.Everything)
        {
            // pak_mods 里的文件名由 pak 规划器生成，记录不固化最终文件名；无法证明归属的 pak 一律保留并提示。
            string pakMods = Path.Combine(_gameRoot, PakMountService.PakModsDirectoryName);
            if (_fileSystem.DirectoryExists(pakMods))
            {
                int pakCount = _fileSystem.EnumerateFiles(pakMods, "*.pak", SearchOption.TopDirectoryOnly).Length;
                if (pakCount > 0)
                {
                    warnings.Add($"pak_mods 内有 {pakCount} 个 pak 无法从 MOD 记录证明归属，已保留；"
                        + "如确认无用请手动清理：" + pakMods);
                }
            }
        }

        return new CleanupReport(disabled, enabled.Count, false) { Warnings = warnings };
    }

    /// <summary>
    /// 把「当前游戏记录能证明内容一致、且没有原文件备份」的散装残留文件移入隔离目录。
    /// 记录外的用户文件/目录一律保留；pak 记录映射到游戏根目录的根层 <c>*.pak</c> 不参与
    /// （它们实际部署在 <c>pak_mods</c>，游戏根目录的同名文件可能是官方 pak）。
    /// </summary>
    /// <remarks>
    /// 不直接删除：当 MOD 源文件与游戏原文件字节相同时，「内容相等」无法证明目标由HunterBox写入；
    /// 隔离目录保留文件本体，用户确认无用后可手动删除。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="protectedPaths">本次禁用刚还原/选择保留的游戏相对路径。</param>
    /// <param name="warnings">收集「无法证明归属而保留」等提示。</param>
    /// <param name="canceled">因取消而提前停止时为 <see langword="true"/>。</param>
    /// <returns>已隔离文件数与隔离根目录；没有隔离任何文件时根目录为 <see langword="null"/>。</returns>
    private (int Quarantined, string? Root) QuarantineResidualModFiles(
        CancellationToken cancellationToken,
        HashSet<string> protectedPaths,
        List<string> warnings,
        out bool canceled)
    {
        canceled = false;
        int quarantined = 0;
        string? quarantineRoot = null;
        foreach (ModRecord mod in _store.ModsFor(_game.Id))
        {
            string filesDirectory = _paths.FilesDirectory(mod.Id);
            foreach (string file in mod.RelativeFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    return (quarantined, quarantineRoot);
                }

                // 使用 pak_mods 的游戏：根层 pak 由 pak 链部署，游戏根目录的文件不是本 MOD 落点。
                // 记录里的 *.pak 若被 MapToGamePath 映射到根目录，删除会误伤官方 patch。
                // 根层 pak 判定统一走 ModelPathRules.IsRootPak。
                if (_game.UsesPakModsFolder && ModelPathRules.IsRootPak(file))
                {
                    continue;
                }

                string mapped;
                try
                {
                    mapped = MountService.MapToGamePath(mod, file);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                string full;
                try
                {
                    full = BoxPaths.ResolveInside(_gameRoot, mapped);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!_fileSystem.FileExists(full))
                {
                    continue;
                }

                // 本次禁用刚还原/选择保留的文件：绝不能当残留再删一次。
                if (protectedPaths.Contains(mapped))
                {
                    continue;
                }

                // 有游戏原文件备份时，这份文件可能是待还原的原文件；清理不碰。
                string backup;
                try
                {
                    backup = _paths.BackupFile(_game.Id, mapped);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (_fileSystem.FileExists(backup))
                {
                    continue;
                }

                // 归属证明：目标内容必须仍等于该 MOD 的源文件。第二次清理时游戏原文件已被还原
                // （protectedPaths 为空），只有内容证明才能避免把它当残留误删。
                string source = Path.Combine(
                    filesDirectory,
                    file.Replace('/', Path.DirectorySeparatorChar));
                if (!_fileSystem.FileExists(source))
                {
                    warnings.Add($"无法证明残留文件属于该 MOD（源文件缺失），已保留：{mapped}");
                    continue;
                }

                bool proven;
                try
                {
                    // 先比长度，长度不同直接判定不一致，避免双份整文件哈希（与 一致）。
                    long targetLength = _fileSystem.GetFileLength(full);
                    long sourceLength = _fileSystem.GetFileLength(source);
                    proven = targetLength == sourceLength
                        && string.Equals(
                            _fingerprints.GetFingerprint(full),
                            _fingerprints.GetFingerprint(source),
                            StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"计算残留文件指纹失败，已保留：{mapped}（{ex.Message}）");
                    continue;
                }

                if (!proven)
                {
                    warnings.Add($"残留文件内容与 MOD 源文件不一致，已保留：{mapped}");
                    continue;
                }

                if (quarantineRoot is null)
                {
                    // 延迟到真正要复制时才创建根目录；复制失败且目录为空时删掉，避免留下空隔离目录。
                    string candidate = CreateQuarantineRoot();
                    if (TryQuarantine(full, mapped, candidate, warnings, _fileSystem, out string? createdDirectory))
                    {
                        quarantineRoot = candidate;
                        quarantined++;
                        PruneEmptyDirectories(Path.GetDirectoryName(full));
                    }
                    else
                    {
                        // 只清理本次新建的那条空目录链，不扫描整棵隔离根。
                        TryDeleteEmptyDirectoriesUpTo(createdDirectory ?? candidate, candidate);
                    }
                }
                else if (TryQuarantine(full, mapped, quarantineRoot, warnings, _fileSystem, out string? createdDirectory))
                {
                    quarantined++;
                    PruneEmptyDirectories(Path.GetDirectoryName(full));
                }
                else if (createdDirectory is not null)
                {
                    // 复用已有隔离根时，失败也要清掉本次新建的空子目录。
                    TryDeleteEmptyDirectoriesUpTo(createdDirectory, quarantineRoot);
                }
            }
        }

        return (quarantined, quarantineRoot);
    }

    /// <summary>创建本次清理的隔离根目录（同一次清理复用，避免每个文件一个时间戳目录）。</summary>
    private string CreateQuarantineRoot()
    {
        // 统一走 IClock（本地时间仅用于目录名展示，不影响业务判定，A-21）。
        string stamp = _clock.UtcNow.ToLocalTime().ToString(Common.Formats.CompactTimestamp, CultureInfo.InvariantCulture);
        string baseRoot = Path.Combine(_paths.ModsDirectory, ".cleanup-quarantine", _game.Id, stamp);
        string root = baseRoot;
        int suffix = 1;
        while (_fileSystem.DirectoryExists(root))
        {
            root = $"{baseRoot}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        }

        _fileSystem.CreateDirectory(root);
        return root;
    }

    /// <summary>把残留文件复制到隔离目录后再删除游戏目录里的副本；失败时保留现场并告警。</summary>
    /// <param name="source">游戏目录里的残留文件。</param>
    /// <param name="mapped">包内相对路径（用于在隔离目录里保留可恢复位置）。</param>
    /// <param name="quarantineRoot">本次清理的隔离根目录。</param>
    /// <param name="warnings">警告收集。</param>
    /// <param name="fileSystem">文件系统端口（失败清理用）。</param>
    /// <param name="createdDirectory">本次尝试创建的目录（失败清理用）；未创建时为 <see langword="null"/>。</param>
    /// <returns>复制并删除源文件成功时返回 <see langword="true"/>。</returns>
    private static bool TryQuarantine(
        string source,
        string mapped,
        string quarantineRoot,
        List<string> warnings,
        IFileSystem fileSystem,
        out string? createdDirectory)
    {
        createdDirectory = null;
        string target;
        try
        {
            target = BoxPaths.ResolveInside(quarantineRoot, mapped);
        }
        catch (ArgumentException)
        {
            warnings.Add($"无法计算隔离路径，已保留：{mapped}");
            return false;
        }

        string? targetDirectory = Path.GetDirectoryName(target);
        try
        {
            if (targetDirectory is not null)
            {
                createdDirectory = targetDirectory;
                fileSystem.CreateDirectory(targetDirectory);
            }

            if (fileSystem.FileExists(target))
            {
                target += "." + Guid.NewGuid().ToString("N");
            }

            fileSystem.CopyFile(source, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fileSystem.TryDeleteFile(target);
            warnings.Add($"无法移入隔离目录，已保留：{mapped}（{ex.Message}）");
            return false;
        }

        if (!fileSystem.TryDeleteFile(source))
        {
            warnings.Add($"已复制到隔离目录但未能删除游戏文件：{mapped}（可稍后手动清理）");
        }

        createdDirectory = null;
        return true;
    }

    /// <summary>从 <paramref name="directory"/> 向上逐级删除空目录，直到（含）<paramref name="root"/>。</summary>
    /// <remarks>
    /// 只处理本次复制尝试创建的那条目录链，不枚举整个隔离根子树；遇到非空目录立即停止，
    /// 绝不删除仍有隔离文件的目录。
    /// </remarks>
    /// <param name="directory">本次尝试创建的目录；可为 <see langword="null"/>。</param>
    /// <param name="root">隔离根目录（删除上界）。</param>
    private void TryDeleteEmptyDirectoriesUpTo(string? directory, string root)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!BoxPaths.IsSameOrInside(current, rootPath))
            {
                return;
            }

            // 隔离根是本次操作自建的目录：允许连同根一起删除。
            DirectoryPruner.PruneEmptyDirectoriesUpward(current, rootPath, _fileSystem, includeStopAt: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 空目录清理失败不影响主流程。
        }
    }

    private void PruneEmptyDirectories(string? directory)
    {
        try
        {
            DirectoryPruner.PruneEmptyDirectoriesUpward(directory, _gameRoot, _fileSystem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个空目录无权枚举/删除时保留，不中断清理（C-06）。
        }
    }
}
