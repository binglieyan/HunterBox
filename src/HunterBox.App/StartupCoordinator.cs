using HunterBox.Application.Common;
using HunterBox.Application.Concurrency;
using HunterBox.Application.Mods;
using HunterBox.Application.Saves;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Logging;

namespace HunterBox.App;

/// <summary>启动准备结果：旧安装路径修复 + Mods 重扫是否需要写盘。</summary>
/// <param name="Repair">旧安装路径修复结果。</param>
/// <param name="Rescanned">是否重扫了 MOD 文件清单。</param>
internal sealed record StartupPreparation(LegacyPluginPathRepairResult Repair, bool Rescanned);

/// <summary>
/// 首帧后的启动数据准备：游戏路径探测、旧安装路径修复、MOD 文件清单重扫。
/// </summary>
/// <remarks>
/// 从 MainWindow 抽出的启动职责；全部是磁盘/注册表 IO，调用方负责放到后台线程，
/// 本类不接触任何 UI 控件。
/// </remarks>
internal sealed class StartupCoordinator
{
    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly SettingsCoordinator _settings;
    private readonly IFileSystem _fileSystem;
    private readonly GameDiscoveryService _gameDiscovery;
    private readonly IAppLogger _logger;
    private readonly IBackgroundTaskHost? _backgroundTasks;

    /// <summary>启动保存任务：完成时携带本次保存是否成功；没有走宿主直接保存时为 <see langword="null"/>。</summary>
    private Task<bool>? _pendingSave;

    /// <summary>装配启动协调器。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="store">MOD 记录存储。</param>
    /// <param name="settings">设置读写协调器（路径键名只在协调器内定义）。</param>
    /// <param name="fileSystem">文件系统端口（临时条目清理用）。</param>
    /// <param name="gameDiscovery">游戏路径发现用例（缺省才写回，A-02）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <param name="backgroundTasks">后台写盘宿主；为 <see langword="null"/> 时直接同步保存。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public StartupCoordinator(
        BoxPaths paths,
        IModStore store,
        SettingsCoordinator settings,
        IFileSystem fileSystem,
        GameDiscoveryService gameDiscovery,
        IAppLogger? logger = null,
        IBackgroundTaskHost? backgroundTasks = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(gameDiscovery);
        _paths = paths;
        _store = store;
        _settings = settings;
        _fileSystem = fileSystem;
        _gameDiscovery = gameDiscovery;
        _logger = logger ?? AppLogAdapter.Instance;
        _backgroundTasks = backgroundTasks;
    }

    /// <summary>执行一次启动准备（不写 UI，完成后由调用方统一刷新列表）。</summary>
    /// <param name="token">
    /// 取消令牌：在旧路径修复、MOD 重扫与写盘前检查；启动期的「取消」按钮依赖它真正中止准备。
    /// 默认值保持无参调用兼容。
    /// </param>
    /// <returns>修复与重扫结果。</returns>
    /// <exception cref="OperationCanceledException">任一检查点发现取消已请求。</exception>
    public StartupPreparation Prepare(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // 清理崩溃/强杀留下的过期 Temp 工作目录与嵌套隔离目录。
        int purged = TempHousekeeping.PurgeStale(
            _paths.TempDirectory,
            TempHousekeeping.DefaultRetention,
            _fileSystem,
            DateTimeOffset.UtcNow,
            token);
        if (purged > 0)
        {
            _logger.Info($"启动清理过期临时条目：{purged} 个");
        }

        // 清理存档父目录里过期的还原快照（保留最近一份作为手工恢复来源）。
        int purgedSnapshots = 0;
        foreach (GameProfile game in GameCatalog.All)
        {
            token.ThrowIfCancellationRequested();
            string savePath = _settings.ResolveSavePath(game);
            if (string.IsNullOrWhiteSpace(savePath))
            {
                continue;
            }

            purgedSnapshots += SaveBackupService.PurgeStaleSnapshots(
                savePath,
                SaveBackupService.DefaultSnapshotRetention,
                _fileSystem,
                cancellationToken: token);
        }

        if (purgedSnapshots > 0)
        {
            _logger.Info($"启动清理过期还原快照：{purgedSnapshots} 个");
        }

        token.ThrowIfCancellationRequested();
        _gameDiscovery.DetectAllMissing(GameCatalog.All);

        token.ThrowIfCancellationRequested();
        // 一次性修复旧版本把世界 nativePC/plugins 误套 reframework/plugins 的记录与已部署文件。
        LegacyPluginPathRepairResult repair = LegacyPluginPathRepair.Repair(
            _store.Mods,
            _paths,
            gameId => _settings.ResolveGamePath(gameId),
            _fileSystem);

        token.ThrowIfCancellationRequested();
        bool rescanned = ModFileRescanner.Rescan(_paths, _store.Mods, _fileSystem);

        token.ThrowIfCancellationRequested();
        if (repair.Changed || rescanned)
        {
            SaveIndex();
        }

        return new StartupPreparation(repair, rescanned);
    }

    /// <summary>保存索引：已装配后台宿主时登记到宿主，否则直接保存。</summary>
    /// <remarks>
    /// 启动准备本身已在后台线程执行；这里只登记、不同步等待宿主，避免同步等待自定义宿主造成死锁。
    /// 每次保存自带结果，后续保存不会读取前一次的错误状态；
    /// 保存失败通过 <see cref="WaitForPendingSaveAsync"/> 的返回值暴露给调用方用于启动提示。
    /// </remarks>
    private void SaveIndex()
    {
        if (_backgroundTasks is null)
        {
            _store.Save();
            return;
        }

        _pendingSave = SaveIndexAsync(_backgroundTasks);
    }

    /// <summary>登记一次启动保存并把本次结果收敛为布尔值（异常只写日志，不向上冒泡）。</summary>
    /// <param name="host">后台任务宿主。</param>
    /// <returns>保存成功时为 <see langword="true"/>。</returns>
    private async Task<bool> SaveIndexAsync(IBackgroundTaskHost host)
    {
        try
        {
            await host.EnqueueAsync(() =>
            {
                _store.Save();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("启动保存 MOD 索引失败", ex);
            return false;
        }
    }

    /// <summary>等待启动保存落盘并返回是否成功（含超时；失败原因已写入日志）。</summary>
    /// <param name="cancellationToken">取消令牌；调用方取消原样传播。</param>
    /// <returns>保存成功或本次启动无需保存时返回 <see langword="true"/>；失败/超时/任务异常返回 <see langword="false"/>。</returns>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async Task<bool> WaitForPendingSaveAsync(CancellationToken cancellationToken = default)
    {
        Task<bool>? pending = _pendingSave;
        if (pending is null)
        {
            return true;
        }

        if (!await BackgroundTaskWait.TryWaitAsync(pending, StartupSaveWaitTimeout, cancellationToken).ConfigureAwait(false))
        {
            _logger.Warn("等待启动索引保存超时或任务异常，修复/重扫结果可能尚未落盘");
            return false;
        }

        return await pending.ConfigureAwait(false);
    }

    /// <summary>启动保存的最长等待时间（与操作入口的保存等待保持一致）。</summary>
    private static readonly TimeSpan StartupSaveWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>把启动准备结果写日志（不涉及 UI）。</summary>
    /// <param name="preparation">启动准备结果。</param>
    public void Log(StartupPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        if (preparation.Repair.Changed)
        {
            _logger.Info($"已修复 {preparation.Repair.Warnings.Count} 条旧安装路径记录，"
                + $"移动 {preparation.Repair.FilesMoved} 个已部署文件");
            foreach (string warning in preparation.Repair.Warnings)
            {
                _logger.Info("安装路径修复：" + warning);
            }
        }

        if (preparation.Rescanned)
        {
            _logger.Info("启动时重扫 MOD 文件：记录已更新");
        }
    }
}
