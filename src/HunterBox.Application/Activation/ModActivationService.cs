using HunterBox.Application.Common;
using HunterBox.Application.Mods;
using HunterBox.Application.Mount;
using HunterBox.Application.Paks;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Activation;

/// <summary>一次启用/禁用操作的结果：散装文件与 pak 的变更统计。</summary>
/// <param name="Loose">散装文件的变更统计。</param>
/// <param name="Pak">pak的变更统计；未使用 <c>pak_mods</c>的游戏或没有 pak 时为 <see langword="null"/>。</param>
public sealed record ActivationReport(MountReport Loose, PakMountReport? Pak)
{
    /// <summary>本次操作中值得提示用户、但不足以中止流程的问题（源文件缺失、回滚说明等）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>检测游戏主程序是否正在运行（由组合根用进程枚举实现）。</summary>
public interface IGameProcessDetector
{
    /// <summary>判断指定主程序是否有正在运行的进程。</summary>
    /// <param name="executableName">主程序文件名（例如 <c>MonsterHunterWilds.exe</c>）。</param>
    /// <returns>有同名进程时返回 <see langword="true"/>。</returns>
    bool IsRunning(string executableName);
}

/// <summary>「检测游戏状态」设置的前置守卫：开启时在改动游戏目录前拦截正在运行的游戏。</summary>
/// <remarks>
/// 游戏运行时启用/禁用/清理 MOD，文件可能被占用或复制失败，留下半套文件（任务层同样拦截）。
/// 未接线或设置为关闭时不产生任何行为。
/// </remarks>
public sealed class GameRunningGuard
{
    /// <summary>同一实例内复用上一次进程检查结果的时间窗口（批量启用/禁用时避免逐 MOD 枚举进程）。</summary>
    /// <remarks>
    /// 取舍：批量操作完成得足够快，窗口内启动的游戏可能不会被发现；单个操作每次都会在窗口外重新检查，
    /// 因此不把窗口放宽为「每批一次」——那样会让一批 300 个 MOD 的清理在游戏启动后仍继续写文件。
    /// </remarks>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(250);

    private readonly GameProfile _game;
    private readonly bool _enabled;
    private readonly IGameProcessDetector _detector;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private DateTimeOffset _lastCheckedAt = DateTimeOffset.MinValue;
    private bool _lastRunning;

    /// <summary>装配守卫。</summary>
    /// <param name="game">当前游戏（取 <see cref="GameProfile.ExecutableName"/> 做进程名）。</param>
    /// <param name="enabled">设置里「检测游戏运行」是否开启。</param>
    /// <param name="detector">进程检测实现。</param>
    /// <param name="clock">时钟端口；为 <see langword="null"/> 时使用 <see cref="SystemClock.Instance"/>。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public GameRunningGuard(
        GameProfile game,
        bool enabled,
        IGameProcessDetector detector,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(detector);

        _game = game;
        _enabled = enabled;
        _detector = detector;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>如果设置开启且游戏正在运行，抛出可读异常。</summary>
    /// <exception cref="InvalidOperationException">游戏正在运行。</exception>
    public void EnsureGameNotRunning()
    {
        if (!_enabled || string.IsNullOrWhiteSpace(_game.ExecutableName))
        {
            return;
        }

        lock (_gate)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (now - _lastCheckedAt >= CacheDuration)
            {
                _lastRunning = _detector.IsRunning(_game.ExecutableName);
                _lastCheckedAt = now;
            }

            if (_lastRunning)
            {
                throw new InvalidOperationException("游戏正在运行，请先退出游戏");
            }
        }
    }
}

/// <summary>
/// 启用 / 禁用一个 MOD的完整用例：散装文件走 <see cref="MountService"/>，
/// pak 走 <see cref="PakMountService"/>，最后把启用状态写回存储。
/// </summary>
/// <remarks>
/// <para>顺序保证：pak 计划先算（可能抛冲突/源缺失），通过后才动散装文件，
/// 避免「散装已经铺好、pak 却因冲突失败」留下半启用状态。</para>
/// <para>
/// 事务性：散装与 pak 各自在服务内部做文件级回滚；本类再做跨服务补偿——
/// pak 写入失败或 <see cref="IModStore.Save"/> 失败时，撤销已经复制的散装文件并删除本次写入的 pak，
/// 最后把 <see cref="ModRecord.Enabled"/> 保持为 <see langword="false"/>。禁用失败时尽量恢复启用状态。
/// </para>
/// </remarks>
public sealed class ModActivationService
{
    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly GameProfile _game;

    /// <summary>文件系统端口：pak 内部文件检查与挂载服务装配共用。</summary>
    private readonly IFileSystem _fileSystem;

    /// <summary>进程级共享的 pak 指纹缓存：由组合根注入，跨多次启用/禁用/清理复用。</summary>
    private readonly PakFingerprintCache _pakFingerprintCache;

    /// <summary>散装挂载端口工厂；默认首次使用时创建 <see cref="MountService"/>，实例内复用（参数与既有装配一致）。</summary>
    private readonly Func<ILooseFileMountService> _createMountService;

    /// <summary>pak 挂载端口工厂；默认首次使用时创建 <see cref="PakMountService"/>，实例内复用并共享指纹缓存。</summary>
    private readonly Func<IPakMountService> _createPakService;
    private readonly GameRunningGuard? _gameRunningGuard;
    private readonly bool _checkPakInternalFiles;
    private readonly IPakArchiveReader? _pakReader;
    private readonly object _saveGate = new();
    private int _saveBatchDepth;
    private bool _savePending;

    /// <summary>批量作用域内是否启用占用索引复用。</summary>
    private bool _mountBatchActive;

    /// <summary>批量作用域内正在复用的挂载服务；作用域结束时调用 <c>EndBatch</c>。</summary>
    private MountService? _batchMount;

    /// <summary>启用前冲突确认预热的挂载服务；Enable 复用它并在结束时释放索引。</summary>
    private MountService? _preparedMount;

    /// <summary>当前是否仍持有冲突预检留下的批量作用域。</summary>
    /// <remarks>测试专用：验证准备阶段异常后不会泄漏陈旧索引。</remarks>
    internal bool HasPreparedMount => _preparedMount is not null;

    /// <summary>批量作用域结束时从挂载服务回收的剩余告警，由批量调用方展示。</summary>
    private readonly List<string> _batchWarnings = [];

    /// <summary>取走并清空批量作用域累计的告警。</summary>
    /// <returns>尚未展示的告警。</returns>
    internal IReadOnlyList<string> TakeBatchWarnings()
    {
        if (_batchWarnings.Count == 0)
        {
            return [];
        }

        List<string> warnings = [.. _batchWarnings];
        _batchWarnings.Clear();
        return warnings;
    }

    /// <summary>装配启用/禁用用例。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="store">MOD 记录存储。</param>
    /// <param name="gameRoot">当前游戏的根目录。</param>
    /// <param name="game">当前游戏配置。</param>
    /// <param name="fingerprint">pak 内容指纹实现。</param>
    /// <param name="options">可选装配项：文件系统端口、游戏运行守卫、pak 检查开关与挂载端口工厂。</param>
    /// <exception cref="ArgumentNullException">paths/store/game/fingerprint/options 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="gameRoot"/> 为空；或 <see cref="ActivationOptions.CheckPakInternalFiles"/> 为
    /// <see langword="true"/> 却没有提供 <see cref="ActivationOptions.PakReader"/>（该能力不允许被静默关闭）。
    /// </exception>
    public ModActivationService(
        BoxPaths paths,
        IModStore store,
        string gameRoot,
        GameProfile game,
        IContentFingerprint fingerprint,
        ActivationOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(options);

        IFileSystem fileSystem = options.FileSystem;
        Func<ILooseFileMountService>? mountServiceFactory = options.MountServiceFactory;
        Func<IPakMountService>? pakServiceFactory = options.PakServiceFactory;
        _paths = paths;
        _store = store;
        _game = game;
        _fileSystem = fileSystem;
        // 进程级共享缓存（由组合根注入，跨多次启用/禁用复用）：
        // 未注入时才退化为本实例自建，避免每个 MOD 新建 MountService 后重复整文件哈希（P-01）。
        _pakFingerprintCache = options.PakFingerprintCache ?? new PakFingerprintCache(fingerprint, fileSystem);
        // 默认工厂在当前实例内复用同一份服务：批量期间只改记录字段（Enabled），记录引用不变；
        // 安装新增记录会创建新的 ModActivationService，不会命中这里的快照。注入工厂只替换协作者来源。
        MountService? sharedMount = null;
        PakMountService? sharedPak = null;
        _createMountService = mountServiceFactory is null
            ? () =>
            {
                // 用委托读取最新记录，批量期间记录被替换/新增也能正确判断占用。
                sharedMount ??= new MountService(
                    paths, gameRoot, game.Id, () => store.Mods, fileSystem, _pakFingerprintCache);
                ActivateMountBatch(sharedMount);
                return sharedMount;
            }
            : () =>
            {
                ILooseFileMountService mount = mountServiceFactory();
                if (mount is MountService concrete)
                {
                    ActivateMountBatch(concrete);
                }

                return mount;
            };
        _createPakService = pakServiceFactory
            ?? (() => sharedPak ??= new PakMountService(
                paths,
                gameRoot,
                game.Id,
                () => store.Mods,
                fileSystem,
                _pakFingerprintCache,
                forgetFingerprint: _pakFingerprintCache.Invalidate,
                rememberFingerprint: _pakFingerprintCache.Remember));
        if (options.CheckPakInternalFiles && options.PakReader is null)
        {
            throw new ArgumentException(
                "CheckPakInternalFiles 为 true 时必须同时提供 PakReader，否则该能力会被静默关闭。",
                nameof(options));
        }

        _gameRunningGuard = options.GameRunningGuard;
        _checkPakInternalFiles = options.CheckPakInternalFiles;
        _pakReader = options.PakReader;
    }

    /// <summary>
    /// 批量落盘作用域：作用域内 <see cref="Enable"/>/<see cref="Disable"/> 不逐项写索引，
    /// 作用域结束时（含取消与逐项失败路径）统一 <see cref="IModStore.Save"/> 一次，已处理项不丢。
    /// </summary>
    /// <returns>释放时落盘的作用域；可嵌套，只有最外层释放时才落盘，重复释放无副作用。</returns>
    /// <remarks>
    /// <para>单项路径（不在作用域内）行为不变：成功后立即保存一次。</para>
    /// <para>
    /// 批量结束时的保存失败会从 <see cref="IDisposable.Dispose"/> 抛出：此时文件已写入、索引尚未更新，
    /// 调用方必须如实向用户报告，不能当成成功（这是有意保留的「记录保存失败不能静默」语义）。
    /// </para>
    /// </remarks>
    public IDisposable BeginSaveBatch()
    {
        lock (_saveGate)
        {
            _saveBatchDepth++;
            _mountBatchActive = true;
            return new SaveBatchScope(this);
        }
    }

    /// <summary>
    /// 批量作用域内的持久化心跳：立即把已完成的启用/禁用状态写入索引，但不结束批量作用域。
    /// </summary>
    /// <remarks>
    /// 不清理脏标记：作用域结束时仍会再保存一次，多一次小文件写入换取「崩溃时最多丢一个心跳窗口」。
    /// 不在作用域内或没有待保存变更时为空操作。
    /// </remarks>
    internal void FlushSaveBatch()
    {
        lock (_saveGate)
        {
            if (_saveBatchDepth == 0 || !_savePending)
            {
                return;
            }
        }

        _store.Save();
    }

    /// <summary>批量作用域内让共享 MountService 复用占用索引；首次创建时构建，成功后增量维护。</summary>
    /// <param name="mount">即将参与批量操作的挂载服务。</param>
    private void ActivateMountBatch(MountService mount)
    {
        if (!_mountBatchActive)
        {
            return;
        }

        mount.BeginBatch();
        _batchMount = mount;
    }

    /// <summary>批量作用域关闭：最外层结束时统一落盘一次（没有变更则跳过）。</summary>
    private void EndSaveBatch()
    {
        bool save;
        bool endMountBatch;
        lock (_saveGate)
        {
            if (_saveBatchDepth == 0)
            {
                return;
            }

            _saveBatchDepth--;
            save = _saveBatchDepth == 0 && _savePending;
            endMountBatch = _saveBatchDepth == 0;
            if (_saveBatchDepth == 0)
            {
                _savePending = false;
                _mountBatchActive = false;
            }
        }

        if (endMountBatch)
        {
            if (_batchMount is not null)
            {
                // 批次最后一项之后仍未带出的候选告警，交给批量调用方展示。
                _batchWarnings.AddRange(_batchMount.EndBatch());
                _batchMount = null;
            }
        }

        if (save)
        {
            _store.Save();
        }
    }

    /// <summary>状态变更后的落盘：普通路径立即保存；批量作用域内只记脏标记，由作用域结束统一保存。</summary>
    private void SaveState()
    {
        lock (_saveGate)
        {
            if (_saveBatchDepth > 0)
            {
                _savePending = true;
                return;
            }
        }

        _store.Save();
    }

    /// <summary>查询某 MOD 与当前游戏其它已启用 MOD 的文件冲突（供 UI 启用前确认）。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <returns>冲突的游戏相对路径。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public IReadOnlyList<string> FindLooseFileConflicts(ModRecord mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        // 冲突检查会构建「游戏路径 → 占用方」索引；留在批量作用域里供随后的 Enable 复用，
        // 避免同一次启用把索引建两遍。
        _preparedMount?.EndBatch();
        _preparedMount = null;
        ILooseFileMountService mount = CreateMountService();
        MountService? prepared = mount as MountService;
        prepared?.BeginBatch();
        try
        {
            IReadOnlyList<string> conflicts = mount.FindLooseFileConflicts(mod);
            // 查询成功才把批量作用域留给随后的 Enable；异常路径不能泄漏陈旧索引。
            _preparedMount = prepared;
            return conflicts;
        }
        catch
        {
            prepared?.EndBatch();
            throw;
        }
    }

    /// <summary>启用 MOD。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <param name="pakMode">pak 命名策略（来自设置里的「自动修正PAK」）。</param>
    /// <param name="cancellationToken">
    /// 取消令牌：只在开始改动文件之前的安全点检查，取消时不会留下半套文件。
    /// </param>
    /// <returns>各链路的变更统计。</returns>
    /// <exception cref="PakConflictException">pak 同名且内容不同，界面应提示并中止。</exception>
    /// <exception cref="FileNotFoundException">MOD 源文件缺失（不会留下半套文件）。</exception>
    /// <exception cref="InvalidOperationException">设置开启「检测游戏运行」且游戏正在运行。</exception>
    /// <exception cref="OperationCanceledException">令牌被触发（尚未改动任何文件）。</exception>
    public ActivationReport Enable(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);

        // 复用冲突确认时建好的占用索引；无论成功/失败/取消都在本次启用结束时释放。
        MountService? preparedMount = _preparedMount;
        _preparedMount = null;
        try
        {
            return EnableCore(mod, pakMode, cancellationToken);
        }
        finally
        {
            preparedMount?.EndBatch();
        }
    }

    private ActivationReport EnableCore(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mod);
        GameScope.EnsureBelongsTo(mod, _game.Id);
        cancellationToken.ThrowIfCancellationRequested();
        _gameRunningGuard?.EnsureGameNotRunning();

        (IReadOnlyList<string> looseFiles, IReadOnlyList<string> pakFiles) = SplitFiles(mod);

        List<string> warnings = [];
        if (_checkPakInternalFiles)
        {
            warnings.AddRange(InspectPakInternalFiles(mod, pakFiles, cancellationToken));
        }

        IPakMountService? pakService = _game.UsesPakModsFolder && pakFiles.Count > 0
            ? CreatePakService()
            : null;
        PakProfile? profile = pakService is null ? null : _game.CreatePakProfile(pakMode);

        // 先校验 pak：冲突/源缺失时直接抛出，散装文件一个都不动。
        PakPlan? plan = pakService?.PlanEnable(mod, profile!, warnings);
        if (plan is { CanEnable: false })
        {
            throw new PakConflictException(plan.Conflicts);
        }

        ILooseFileMountService mount = CreateMountService();
        // 内部预校验 + 失败自行回滚，避免散装半套。
        MountReport loose = mount.Enable(mod, looseFiles);

        PakMountReport? pak = null;
        try
        {
            pak = pakService?.Enable(plan!);
            mod.Enabled = true;
            SaveState();
        }
        catch (Exception ex)
        {
            bool rolledBack = RollbackEnable(mod, pakService, profile, pak, mount, looseFiles, warnings);
            mod.Enabled = false;
            if (rolledBack)
            {
                // 回滚成功也不能丢掉 warnings：禁用式回滚可能保守保留了无备份/无法证明归属的文件，
                // 用户必须知道「MOD 显示未启用，但游戏目录仍有文件」。
                if (warnings.Count == 0)
                {
                    throw;
                }

                throw new IOException(
                    "启用失败，本次写入的文件已回滚；以下是需要人工确认的提示：\n"
                    + string.Join("\n", warnings) + "\n" + ex.Message,
                    ex);
            }

            throw new IOException(
                "启用失败，且部分文件未能完整回滚；请关闭占用程序后重试，或在「重新复制MOD」里修复。\n"
                + ex.Message + "\n" + string.Join("\n", warnings),
                ex);
        }

        return new ActivationReport(loose, pak) { Warnings = Merge(warnings, loose.Warnings, pak?.Warnings) };
    }

    /// <summary>禁用 MOD。</summary>
    /// <param name="mod">待禁用的 MOD。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="cancellationToken">
    /// 取消令牌：只在开始改动文件之前的安全点检查，取消时不改动任何文件。
    /// </param>
    /// <returns>各链路的变更统计。</returns>
    /// <remarks>任一步失败时尽量恢复到「仍启用」的一致状态。</remarks>
    /// <exception cref="InvalidOperationException">设置开启「检测游戏运行」且游戏正在运行。</exception>
    /// <exception cref="OperationCanceledException">令牌被触发（尚未改动任何文件）。</exception>
    public ActivationReport Disable(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        GameScope.EnsureBelongsTo(mod, _game.Id);
        cancellationToken.ThrowIfCancellationRequested();
        _gameRunningGuard?.EnsureGameNotRunning();
        if (!mod.Enabled)
        {
            throw new InvalidOperationException("只有已启用的 MOD 才能禁用");
        }

        (IReadOnlyList<string> looseFiles, IReadOnlyList<string> pakFiles) = SplitFiles(mod);
        List<string> warnings = [];

        IPakMountService? pakService = _game.UsesPakModsFolder && pakFiles.Count > 0
            ? CreatePakService()
            : null;
        PakProfile? profile = pakService is null ? null : _game.CreatePakProfile(pakMode);
        PakMountReport? pak = null;
        if (pakService is not null)
        {
            // pak 服务内部原子：失败时 pak_mods 保持原样，散装还没动，状态一致。
            pak = pakService.Disable(mod, profile!);
        }

        // PlanDisable 显式排除自身；这里先撤文件再改状态，只是保持「失败时记录仍为启用」的时序约定（N-17）。
        ILooseFileMountService mount = CreateMountService();
        MountReport loose;
        try
        {
            loose = mount.Disable(mod, looseFiles);
        }
        catch (Exception ex)
        {
            bool restored = RestorePakAfterLooseFailure(mod, pakService, profile, warnings);
            if (restored)
            {
                throw;
            }

            throw new IOException(
                "禁用失败，且已写入的 pak 未能恢复；MOD 保持启用状态，请关闭占用程序后重试。\n"
                + ex.Message + "\n" + string.Join("\n", warnings),
                ex);
        }

        try
        {
            mod.Enabled = false;
            SaveState();
        }
        catch (Exception ex)
        {
            bool restored = RestoreDisabledState(mod, looseFiles, pakService, profile, warnings);
            mod.Enabled = true;
            if (restored)
            {
                throw;
            }

            throw new IOException(
                "禁用失败，且文件状态未能完整恢复；MOD 记录保持启用。请检查游戏目录后重新启用/禁用。\n"
                + ex.Message + "\n" + string.Join("\n", warnings),
                ex);
        }

        return new ActivationReport(loose, pak) { Warnings = Merge(warnings, loose.Warnings, pak?.Warnings) };
    }

    /// <summary>重新复制：对已启用的 MOD 重跑一次复制流程（不改变启用状态）。</summary>
    /// <param name="mod">已启用的 MOD。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="cancellationToken">取消令牌：只在开始改动文件之前的安全点检查。</param>
    /// <returns>各链路的变更统计。</returns>
    /// <exception cref="InvalidOperationException">MOD 尚未启用，或设置开启「检测游戏运行」且游戏正在运行。</exception>
    /// <exception cref="OperationCanceledException">令牌被触发（尚未改动任何文件）。</exception>
    public ActivationReport Recopy(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        GameScope.EnsureBelongsTo(mod, _game.Id);
        cancellationToken.ThrowIfCancellationRequested();
        _gameRunningGuard?.EnsureGameNotRunning();
        if (!mod.Enabled)
        {
            throw new InvalidOperationException("只有已启用的 MOD 才能重新复制");
        }

        (IReadOnlyList<string> looseFiles, IReadOnlyList<string> pakFiles) = SplitFiles(mod);
        PakMountReport? pak = null;
        if (_game.UsesPakModsFolder && pakFiles.Count > 0)
        {
            // pak 步骤自身幂等且内置回滚：内容相同会 skip，内容不同会覆盖。
            pak = CreatePakService().Enable(mod, _game.CreatePakProfile(pakMode));
        }

        ILooseFileMountService mount = CreateMountService();
        try
        {
            MountReport loose = mount.Enable(mod, looseFiles);
            return new ActivationReport(loose, pak) { Warnings = Merge(loose.Warnings, pak?.Warnings) };
        }
        catch (Exception ex)
        {
            // 注意：不能在这里用 RollbackEnable 删 pak。
            // 该 pak 可能是上一次启用写入、本次内容相同被 skip 的既有文件；删掉会让 MOD
            // 显示已启用但游戏内失效。pak 步骤本身已自洽，失败时散装复制由 MountService 自行回滚。
            string hint = pak is { Written: > 0 }
                ? Environment.NewLine + "pak 已复制成功、散装文件未写入：可重试「重新复制」，或先禁用再启用。"
                : string.Empty;

            throw new IOException(
                "重新复制失败，散装文件未能写入；游戏目录已回滚。" + Environment.NewLine + ex.Message + hint,
                ex);
        }
    }

    /// <summary>启用失败后的补偿：逆序撤销 pak 写入，再把散装文件恢复到启用前状态。</summary>
    /// <remarks>调用前提：pak 是本次启用从无到有写入的；重新复制等「可能已有旧 pak」的场景不得调用。</remarks>
    private static bool RollbackEnable(
        ModRecord mod,
        IPakMountService? pakService,
        PakProfile? profile,
        PakMountReport? pak,
        ILooseFileMountService mount,
        IReadOnlyList<string> looseFiles,
        List<string> warnings)
    {
        bool ok = true;
        warnings.Add("启用失败，正在撤销本次已复制的文件…");

        if (pak is not null && pakService is not null && profile is not null)
        {
            try
            {
                warnings.AddRange(pakService.Disable(mod, profile).Warnings);
            }
            catch (Exception ex)
            {
                ok = false;
                warnings.Add($"撤销已写入的 pak 失败：{ex.Message}");
            }
        }

        try
        {
            // 让 MountService 把本 MOD的旧文件视为自己的占用，才能正确还原/删除。
            mod.Enabled = true;
            try
            {
                warnings.AddRange(mount.Disable(mod, looseFiles).Warnings);
            }
            finally
            {
                mod.Enabled = false;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            warnings.Add($"撤销已复制的散装文件失败：{ex.Message}");
        }

        return ok;
    }

    /// <summary>禁用时散装撤出失败：把已经删掉的 pak 重新写回（状态仍是启用）。</summary>
    private static bool RestorePakAfterLooseFailure(
        ModRecord mod,
        IPakMountService? pakService,
        PakProfile? profile,
        List<string> warnings)
    {
        if (pakService is null || profile is null)
        {
            return true;
        }

        try
        {
            pakService.Enable(mod, profile);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"恢复已删除的 pak 失败：{ex.Message}（可点「重新复制MOD」修复）");
            return false;
        }
    }

    /// <summary>禁用已撤出文件但保存状态失败：把文件恢复成启用状态。</summary>
    private bool RestoreDisabledState(
        ModRecord mod,
        IReadOnlyList<string> looseFiles,
        IPakMountService? pakService,
        PakProfile? profile,
        List<string> warnings)
    {
        bool ok = true;

        // 逆序：先恢复后撤的散装文件（按「未启用」上下文，让游戏原文件重新备份），再恢复先撤的 pak。
        try
        {
            mod.Enabled = false;
            CreateMountService().Enable(mod, looseFiles);
        }
        catch (Exception ex)
        {
            ok = false;
            warnings.Add($"恢复散装文件失败：{ex.Message}");
        }

        if (pakService is not null && profile is not null)
        {
            try
            {
                pakService.Enable(mod, profile);
            }
            catch (Exception ex)
            {
                ok = false;
                warnings.Add($"恢复 pak 失败：{ex.Message}");
            }
        }

        return ok;
    }

    private IPakMountService CreatePakService() => _createPakService();

    private ILooseFileMountService CreateMountService() => _createMountService();

    private static List<string> Merge(params IEnumerable<string>?[] lists)
    {
        List<string> merged = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (IEnumerable<string>? list in lists)
        {
            if (list is null)
            {
                continue;
            }

            foreach (string item in list)
            {
                if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
                {
                    merged.Add(item);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// 「检查PAK内部文件」：展开本 MOD 与已启用 MOD的 pak 条目表（RE pak 只存路径哈希、不存文件名），
    /// 找出会被后启用者覆盖的同名内部文件，返回可读提示；提示不中止启用。
    /// </summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <param name="pakFiles">本 MOD的根层 pak 文件。</param>
    /// <param name="cancellationToken">取消令牌（这里是只读扫描阶段，取消不会留下任何改动）。</param>
    /// <returns>可读提示列表。</returns>
    private List<string> InspectPakInternalFiles(
        ModRecord mod,
        IReadOnlyList<string> pakFiles,
        CancellationToken cancellationToken)
    {
        List<string> warnings = [];
        if (_pakReader is null || pakFiles.Count == 0 || !_game.UsesPakModsFolder)
        {
            return warnings;
        }

        List<ModRecord> enabledOthers = [.. _store.ModsFor(_game.Id)
            .Where(m => m.Enabled && m.Id != mod.Id)
            .OrderBy(m => m.Index)];
        if (enabledOthers.Count == 0)
        {
            return warnings;
        }

        Dictionary<ulong, string> ownEntries = [];
        TryCollectPakEntries(mod, pakFiles, ownEntries, warnings, cancellationToken);
        if (ownEntries.Count == 0)
        {
            return warnings;
        }

        foreach (ModRecord other in enabledOthers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> otherPaks = RootPakFiles(other);
            if (otherPaks.Count == 0)
            {
                continue;
            }

            Dictionary<ulong, string> otherEntries = [];
            TryCollectPakEntries(other, otherPaks, otherEntries, warnings, cancellationToken);
            List<ulong> shared = [.. ownEntries.Keys.Where(otherEntries.ContainsKey)];
            if (shared.Count == 0)
            {
                continue;
            }

            string ownFiles = string.Join(
                "、",
                shared.Select(hash => ownEntries[hash]).Distinct(StringComparer.OrdinalIgnoreCase));
            string otherFiles = string.Join(
                "、",
                shared.Select(hash => otherEntries[hash]).Distinct(StringComparer.OrdinalIgnoreCase));
            warnings.Add(
                $"检查PAK内部文件：「{mod.DisplayName}」与已启用的「{other.DisplayName}」有 {shared.Count} 个 pak 内部文件重叠"
                + $"（{ownFiles} ↔ {otherFiles}）；同时启用会互相覆盖，建议只启用其中一个。");
        }

        return warnings;
    }

    /// <summary>读取若干个 pak的条目路径哈希（同哈希即同一内部文件）；读不了的包记录 warning 后跳过。</summary>
    private void TryCollectPakEntries(
        ModRecord mod,
        IReadOnlyList<string> pakFiles,
        Dictionary<ulong, string> entries,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (string file in pakFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.Combine(
                _paths.FilesDirectory(mod.Id),
                file.Replace('/', Path.DirectorySeparatorChar));
            if (!_fileSystem.FileExists(fullPath))
            {
                // 源文件缺失由 pak 挂载流程统一报错，这里不重复。
                continue;
            }

            try
            {
                PakArchiveInfo archive = _pakReader!.Read(fullPath);
                foreach (PakEntryInfo entry in archive.Entries)
                {
                    entries[entry.Hash] = file;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                warnings.Add(
                    $"检查PAK内部文件：无法读取「{mod.DisplayName}」的 {file}（{ex.Message}），已跳过该包的内部比对。");
            }
        }
    }

    private static List<string> RootPakFiles(ModRecord mod)
    {
        List<string> paks = [];
        foreach (string file in mod.RelativeFiles)
        {
            if (ModelPathRules.IsRootPak(file))
            {
                paks.Add(file);
            }
        }

        return paks;
    }

    /// <summary>
    /// 把 MOD 文件拆成「散装」与「根层 pak」两路。
    /// 不使用 <c>pak_mods</c>的游戏，pak 也按散装文件处理（留在游戏根目录）。
    /// </summary>
    private (IReadOnlyList<string> Loose, IReadOnlyList<string> Pak) SplitFiles(ModRecord mod)
    {
        List<string> loose = [];
        List<string> pak = [];
        foreach (string file in mod.RelativeFiles)
        {
            bool isRootPak = ModelPathRules.IsRootPak(file);
            if (isRootPak && _game.UsesPakModsFolder)
            {
                pak.Add(file);
            }
            else
            {
                loose.Add(file);
            }
        }

        return (loose, pak);
    }

    /// <summary><see cref="BeginSaveBatch"/> 的作用域释放钩子；重复释放不会重复落盘。</summary>
    /// <param name="owner">所属启用/禁用服务。</param>
    private sealed class SaveBatchScope(ModActivationService owner) : IDisposable
    {
        private ModActivationService? _owner = owner;

        public void Dispose()
        {
            ModActivationService? current = Interlocked.Exchange(ref _owner, null);
            current?.EndSaveBatch();
        }
    }
}
