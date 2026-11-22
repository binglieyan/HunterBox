using HunterBox.Application.Activation;
using HunterBox.Application.Cleanup;
using HunterBox.Application.Common;
using HunterBox.Application.Concurrency;
using HunterBox.Application.Install;
using HunterBox.Application.Models;
using HunterBox.Application.Mods;
using HunterBox.Application.Saves;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Games;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Logging;
using HunterBox.Infrastructure.Migration;
using HunterBox.Infrastructure.Models;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Saves;
using HunterBox.Infrastructure.Settings;
using Microsoft.UI.Xaml;

namespace HunterBox.App;

/// <summary>
/// App 层的唯一组合根：集中持有HunterBox布局、MOD 记录存储与设置，并统一装配
/// 安装器 / 启用禁用 / 清理服务。
/// </summary>
/// <remarks>
/// <para>
/// 收敛前 <see cref="MainWindow"/> 与 <see cref="MaintenanceWorkflow"/> 各写了一套
/// <c>CreateActivation</c>/<c>CreateInstaller</c>，同一组构造依赖（内容指纹、游戏运行守卫、
/// pak 内部文件读取器、pak 命名模式）有两份，容易漂移；现在只有这里一处装配。
/// </para>
/// <para>
/// 依赖边界保持简单：不引入 DI 容器，由 <see cref="MainWindow"/> 启动时创建一次并持有。
/// 当前游戏与游戏目录属于窗口状态，仍由调用方按现状传入，保证切换游戏立即可见。
/// 工厂返回的服务都是瞬态（内部有批量落盘等状态），一次操作一份，不要跨操作复用；
/// 例外是 <see cref="PakFingerprints"/> 与 <see cref="ModelTextCacheStore"/>：前者是进程级指纹缓存，
/// 后者是跨会话模型文本缓存的持久化端口；两者由工厂显式注入到各用例，不随瞬态服务销毁。
/// </para>
/// </remarks>
internal sealed class AppServices
{
    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly ISettings _settings;

    /// <summary>用窗口/应用已有的根依赖装配组合根。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="store">MOD 记录存储。</param>
    /// <param name="settings">HunterBox设置端口（与 App/MainWindow 共用同一实例，避免多份内存状态互相覆盖）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AppServices(BoxPaths paths, IModStore store, ISettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _store = store;
        _settings = settings;
        ModList = new ModListCoordinator(store);
        SettingsCoordinator = new SettingsCoordinator(settings);
        GameSession = new GameSession(SettingsCoordinator);
        GameDiscovery = new GameDiscoveryService(settings, new SteamGameProbe(), Logger);
        PakFingerprints = new PakFingerprintCache(new FileContentFingerprint(), FileSystem);
        ModelTextCacheStore = new JsonModelTextCacheStore(
            Path.Combine(paths.Root, "model-text-cache.json"),
            Logger);
        PakEntryPathReader = new PakEntryPathReader(Logger);
        PakRepacker = new PakRepacker(logger: Logger);
    }

    /// <summary>按程序目录创建设置端口实例（组合根唯一创建点）。</summary>
    /// <returns>基于 <c>hunterbox.ini</c> 的设置端口实例。</returns>
    public static ISettings CreateDefaultSettings() =>
        new IniSettings(
            new BoxPaths(AppContext.BaseDirectory).SettingsFile,
            AppLogAdapter.Instance);

    /// <summary>HunterBox磁盘布局。</summary>
    public BoxPaths Paths => _paths;

    /// <summary>MOD 记录存储。</summary>
    public IModStore Store => _store;

    /// <summary>HunterBox设置端口。</summary>
    public ISettings Settings => _settings;

    /// <summary>应用日志端口（App 层与 Infrastructure 适配器共用同一实现）。</summary>
    public IAppLogger Logger { get; } = AppLogAdapter.Instance;

    /// <summary>当前日志文件路径（只用于界面提示「详见日志」，不属于日志端口契约）。</summary>
    public static string LogFile => AppLog.CurrentFile;

    /// <summary>文件系统端口（清理/删除的唯一入口，由 Infrastructure 实现）。</summary>
    public IFileSystem FileSystem { get; } = SystemFileSystem.Instance;

    /// <summary>后台写盘任务宿主：登记串行保存，关窗按空闲状态等待（A-07）。</summary>
    public IBackgroundTaskHost BackgroundTasks { get; } = new BackgroundTaskHost();

    /// <summary>MOD 列表顺序协调器：顺序归一/移动逻辑从窗口抽出（A-06）。</summary>
    public ModListCoordinator ModList { get; }

    /// <summary>当前游戏会话：游戏与根目录的唯一状态来源（A-06）。</summary>
    public GameSession GameSession { get; }

    /// <summary>游戏路径发现用例（探测在 Infrastructure，缺省才写回规则在 Application，A-02）。</summary>
    public GameDiscoveryService GameDiscovery { get; }

    /// <summary>进程级 pak 指纹缓存：跨多次单独启用/禁用复用整文件哈希。</summary>
    public PakFingerprintCache PakFingerprints { get; }

    /// <summary>跨会话模型备注文本缓存的持久化端口（与内存缓存 <c>ModelTextCache</c> 区分）。</summary>
    public IModelTextCacheStore ModelTextCacheStore { get; }

    /// <summary>设置读写协调器：键名、默认值与批量保存从对话框收敛到 Application（A-06）。</summary>
    public SettingsCoordinator SettingsCoordinator { get; }

    /// <summary>界面主题偏好（启动时由 <see cref="LoadTheme"/> 读好，运行期由 <see cref="ApplyTheme"/> 更新）。</summary>
    public ThemePreference Theme { get; private set; } = ThemePreference.System;

    /// <summary>当前主题对应的元素主题；ContentDialog/MenuFlyout 等弹出层不在根元素下，需要显式指定。</summary>
    public ElementTheme ThemeElement => AppTheme.ToElementTheme(Theme);

    /// <summary>启动时读取界面主题；失败按「跟随系统」处理，绝不阻塞启动。</summary>
    public void LoadTheme()
    {
        try
        {
            Theme = AppTheme.Read(_settings);
            Logger.Info($"界面主题：{AppTheme.ToIni(Theme)}");
        }
        catch (Exception ex)
        {
            // 主题不是关键配置：任何读取失败都降级为「跟随系统」，不能让启动失败。
            Theme = ThemePreference.System;
            Logger.Warn("读取界面主题失败，按「跟随系统」处理：" + ex.Message);
        }
    }

    /// <summary>把主题应用到窗口根元素（运行期切换主题的唯一可靠方式）。</summary>
    /// <remarks>
    /// <see cref="Microsoft.UI.Xaml.Application.RequestedTheme"/> 只允许在应用启动时设置，
    /// 运行期只能改根 <see cref="FrameworkElement"/>的 <see cref="FrameworkElement.RequestedTheme"/>。
    /// </remarks>
    /// <param name="root">窗口根元素（通常是 Window.Content）。</param>
    /// <param name="theme">要应用的主题偏好。</param>
    public void ApplyTheme(FrameworkElement root, ThemePreference theme)
    {
        ArgumentNullException.ThrowIfNull(root);
        Theme = theme;
        ElementTheme elementTheme = AppTheme.ToElementTheme(theme);
        if (root.RequestedTheme != elementTheme)
        {
            root.RequestedTheme = elementTheme;
        }
    }

    /// <summary>
    /// pak 条目路径读取端口（模型识别/文件夹扫描用；实现无状态，可安全复用）。
    /// </summary>
    /// <remarks>
    /// Application 的模型分类/文件夹改写只依赖 <see cref="IPakEntryPathReader"/>，
    /// 具体 pak 解析/解密在 Infrastructure。App 各调用点统一从这里取，避免各自 new。
    /// </remarks>
    public IPakEntryPathReader PakEntryPathReader { get; }

    /// <summary>模型显示名解析端口（Application 模型逻辑与 Infrastructure 名称表的适配）。</summary>
    public IModelNameResolver ModelNames { get; } = new AppModelNameResolver();

    /// <summary>pak 整包解析端口（「没有可改模型」提示需要检查根层 pak 是否可解析）。</summary>
    public IPakRepacker PakRepacker { get; }

    /// <summary>按指定游戏装配安装器（瞬态：一次安装/卸载流程内使用）。</summary>
    /// <param name="game">当前游戏。</param>
    /// <returns>绑定该游戏 ID 的安装器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public ModInstaller CreateInstaller(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return new ModInstaller(
            _paths,
            _store,
            new ArchiveExtractor(Logger),
            FileSystem,
            game.Id);
    }

    /// <summary>装配安装处理上下文（安装器 + 当前游戏目录），供安装向导的暂存阶段使用。</summary>
    /// <param name="game">当前游戏。</param>
    /// <param name="gameRoot">当前游戏根目录。</param>
    /// <returns>安装处理上下文。</returns>
    public ModInstallContext CreateInstallContext(GameProfile game, string gameRoot) =>
        new(_paths, _store, CreateInstaller(game), gameRoot);

    /// <summary>按当前游戏/游戏目录与设置装配启用/禁用用例（瞬态：一项批量或单项操作一份）。</summary>
    /// <param name="game">当前游戏。</param>
    /// <param name="gameRoot">当前游戏根目录。</param>
    /// <returns>启用/禁用用例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public ModActivationService CreateActivation(GameProfile game, string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(game);
        IPakArchiveReader? pakReader = CreatePakInternalInspector();
        bool checkPakFiles = _settings.GetBool(
            SettingsKeys.Section,
            SettingsKeys.CheckPakFiles,
            fallback: SettingsDefaults.CheckPakFiles);
        return new ModActivationService(
            _paths,
            _store,
            gameRoot,
            game,
            new FileContentFingerprint(),
            new ActivationOptions
            {
                FileSystem = FileSystem,
                GameRunningGuard = CreateGameRunningGuard(game),
                CheckPakInternalFiles = checkPakFiles,
                PakReader = pakReader,
                PakFingerprintCache = PakFingerprints,
            });
    }

    /// <summary>装配旧数据迁移服务（只读扫描/复制旧HunterBox数据）。</summary>
    /// <returns>绑定当前存储的迁移服务。</returns>
    public LegacyMigrationService CreateLegacyMigration() =>
        new(_paths, _store, FileSystem, Logger);

    /// <summary>装配「未归属 MOD 归到当前游戏」用例（A-11）。</summary>
    /// <returns>归属服务。</returns>
    public GameAssignmentService CreateGameAssignment() => new(_store);

    /// <summary>装配存档备份服务（对话框与维护流程共用同一组合根）。</summary>
    /// <returns>绑定当前HunterBox布局的存档服务。</returns>
    public SaveBackupService CreateSaveBackup() =>
        new(_paths, FileSystem, new JsonBackupManifestStore());

    /// <summary>装配旧存档 zip 导入器（依赖 Infrastructure 的 SharpCompress 实现）。</summary>
    /// <returns>旧存档导入端口。</returns>
    public ILegacySaveImporter CreateLegacySaveImporter() =>
        new SharpCompressSaveImporter(_paths, FileSystem, CreateSaveBackup(), Logger);

    /// <summary>装配 pak 模型改写服务（路径哈希改名 + TOC 前像 / 整包备份）。</summary>
    /// <returns>pak 改写服务。</returns>
    public PakModelRewriteService CreatePakRewrite() =>
        new(
            new RePakReader(),
            new RePakRenamer(Logger),
            new PakPathHashProvider(),
            new PakRepacker(logger: Logger));

    /// <summary>按当前游戏/游戏目录/命名策略与设置装配清理用例。</summary>
    /// <param name="game">当前游戏。</param>
    /// <param name="gameRoot">当前游戏根目录。</param>
    /// <param name="pakMode">pak 命名策略（清理用例的构造依赖）。</param>
    /// <returns>清理用例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public ModCleanupService CreateCleanup(GameProfile game, string gameRoot, PakNamingMode pakMode)
    {
        ArgumentNullException.ThrowIfNull(game);
        return new ModCleanupService(
            _paths,
            _store,
            gameRoot,
            game,
            PakFingerprints,
            FileSystem,
            pakMode,
            CreateGameRunningGuard(game));
    }

    /// <summary>按「检测游戏运行」设置装配守卫；关闭时返回 <see langword="null"/>。</summary>
    private GameRunningGuard? CreateGameRunningGuard(GameProfile game) =>
        _settings.GetBool(
            SettingsKeys.Section,
            SettingsKeys.CheckGameRunning,
            fallback: SettingsDefaults.CheckGameRunning)
            ? new GameRunningGuard(game, enabled: true, new GameProcessDetector())
            : null;

    /// <summary>按「检查PAK内部文件」设置装配 pak 条目读取器；关闭时返回 <see langword="null"/>。</summary>
    private RePakReader? CreatePakInternalInspector() =>
        _settings.GetBool(
            SettingsKeys.Section,
            SettingsKeys.CheckPakFiles,
            fallback: SettingsDefaults.CheckPakFiles)
            ? new RePakReader()
            : null;
}
