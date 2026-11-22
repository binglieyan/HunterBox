using System.Diagnostics.CodeAnalysis;
using HunterBox.Domain.Games;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
namespace HunterBox.App;
/// <summary>主窗口：MOD 列表、常用操作与状态提示。</summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "窗口与进程同生命周期；SemaphoreSlim 未使用 AvailableWaitHandle，不持有需要显式释放的 OS 句柄。")]
public sealed partial class MainWindow : Window
{
    /// <summary>默认窗口尺寸（DIP）。</summary>
    private const int DefaultWidth = 1240;

    /// <summary>默认窗口高度（DIP）。</summary>
    private const int DefaultHeight = 820;

    /// <summary>窗口最小宽度（DIP）。</summary>
    private const int MinWindowWidth = 1080;

    /// <summary>窗口最小高度（DIP）。</summary>
    private const int MinWindowHeight = 720;

    private readonly AppServices _services;
    private readonly ModelPanel _modelPanel;
    private readonly StartupCoordinator _startup;
    private readonly InstallWorkflow _installWorkflow;
    private readonly MaintenanceWorkflow _maintenance;
    private readonly ModListView _list = new();
    private readonly OperationRunner _operations;
    private readonly InstallRefreshCoordinator _installRefresh;

    /// <summary>主窗口界面状态（A-06）：按钮可用性、忙碌环与取消按钮通过 x:Bind 订阅。</summary>
    public MainWindowViewModel ViewModel { get; } = new();

    /// <summary>UI 入口互斥：同一时刻只允许一个交互入口（含它打开的对话框）在执行，防止重复提交与并发对话框。</summary>
    private readonly SemaphoreSlim _uiActionGate = new(1, 1);

    /// <summary>对话框串行门：WinUI 只允许同时打开一个 ContentDialog，并发 ShowAsync 会直接抛异常。</summary>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>切换游戏时的抑制标记（启动流程要等数据准备完再统一刷新，避免双重 ReloadMods）。</summary>
    private bool _suppressGameReload;

    /// <summary>搜索框输入去抖：每敲键都重建整表会明显卡顿，停顿 200ms 后再过滤。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterDebounce;

    /// <summary>窗口最小尺寸钩子（WinUI 没有托管的最小尺寸 API，用 WM_GETMINMAXINFO 子类化实现）。</summary>
    private WindowMinSize? _minSize;

    /// <summary>内容根节点是否已经加载（Loaded 可能重复触发，只做一次初始化）。</summary>
    private bool _loaded;

    /// <summary>创建主窗口并加载已安装的 MOD。</summary>
    /// <param name="services">组合根装配好的依赖（布局、存储、设置与各用例工厂）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <see langword="null"/>。</exception>
    internal MainWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        InitializeComponent();
        _operations = new OperationRunner(
            // 忙碌环/取消按钮已由 ViewModel 绑定；这里只负责把最新状态同步给 ViewModel。
            _ => UpdateActionButtons(),
            text => ViewModel.Status = text,
            UpdateActionButtons,
            RefreshAfterOperation,
            _services.Logger);

        // AppServices 已在 MainWindow 创建前读取 [settings] theme，这里在内容创建时立即应用，
        // 避免窗口先按系统主题渲染再闪一下（运行期再改由 Settings 保存后调用 ApplyTheme）。
        services.ApplyTheme(Root, services.Theme);
        _modelPanel = new ModelPanel(
            _services.Paths,
            _services.PakEntryPathReader,
            _services.ModelNames,
            _services.PakRepacker,
            _services.FileSystem,
            _services.Logger,
            _services.ModelTextCacheStore,
            _services.BackgroundTasks);
        _installWorkflow = new InstallWorkflow(
            _services.Paths,
            _services.PakEntryPathReader,
            _services.ModelNames,
            _services.Logger);
        _maintenance = new MaintenanceWorkflow(_services);
        _installRefresh = new InstallRefreshCoordinator(this, _services.Logger);
        _startup = new StartupCoordinator(
            _services.Paths,
            _services.Store,
            _services.SettingsCoordinator,
            _services.FileSystem,
            _services.GameDiscovery,
            _services.Logger,
            _services.BackgroundTasks);
        ModsList.ItemsSource = _list.Rows;
        GameCombo.ItemsSource = GameCatalog.All.Select(g => g.DisplayName).ToList();
        UpdateWindowTitle();
        ApplyWindowIcon();
        Root.Loaded += OnRootLoaded;

        // 关闭保护（C-08）：有长任务/忙碌对话框时取消 Alt+F4 与标题栏关闭，优雅等待后自动关窗。
        AppWindow.Closing += OnAppWindowClosing;
    }

    private PakNamingMode PakMode =>
        _services.SettingsCoordinator.FixPakNumber
            ? PakNamingMode.PatchNumbered
            : PakNamingMode.KeepOriginalName;

    /// <summary>当前游戏对应的模型改写游戏枚举（pak 路径清单/候选表按作区分）。</summary>
    private ModelGame CurrentModelGame =>
        ModelGameInfo.TryParseId(_services.GameSession.Game.Id, out ModelGame game) ? game : ModelGame.Wilds;

    /// <summary>安装暂存阶段需要的上下文：由组合根统一装配，窗口只补当前游戏目录。</summary>
    private ModInstallContext Context => _services.CreateInstallContext(_services.GameSession.Game, _services.GameSession.GameRoot);

    /// <summary>关键词非空时视为「搜索模式」：禁止排序与添加 MOD。</summary>
    private bool IsFilterMode => !string.IsNullOrWhiteSpace(ViewModel.FilterKeyword);
}
