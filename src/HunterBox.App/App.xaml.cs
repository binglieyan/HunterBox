using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HunterBox.Application.Settings;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Logging;
using HunterBox.Infrastructure.Migration;
using HunterBox.Infrastructure.Startup;
using HunterBox.Infrastructure.Storage;
using Microsoft.UI.Xaml;

namespace HunterBox.App;

/// <summary>
/// 应用程序入口：初始化日志、挂全局异常钩子、创建并激活主窗口。
/// </summary>
/// <remarks>
/// <see cref="Microsoft.UI.Xaml.Application.UnhandledException"/> /
/// <see cref="AppDomain.UnhandledException"/> / <see cref="TaskScheduler.UnobservedTaskException"/>
/// 三处未处理异常都写 <c>logs/crash-*.txt</c> 并尽量提示用户。
/// 继承类型必须写全名：项目里存在 <c>HunterBox.Application</c> 命名空间，
/// 简写 <c>Application</c> 会被解析成命名空间而不是 WinUI的类型。
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "单实例守卫随进程生命周期存活，由操作系统在退出时回收；第二实例路径已显式 Dispose。")]
public partial class App : Microsoft.UI.Xaml.Application
{
    private static readonly CrashReporter CrashReporter = new();

    private Microsoft.UI.Xaml.Window? _window;

    /// <summary>
    /// 单实例守卫：进程存活期间持有命名互斥体；为 <see langword="null"/> 表示本实例是第二个实例。
    /// </summary>
    private SingleInstanceGuard? _singleInstance;

    /// <summary>产品版本号：唯一来源是 csproj 的 <c>Version</c>（经程序集版本读取），日志与主窗口标题共用。</summary>
    internal static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";

    /// <summary>初始化应用实例并挂全局异常钩子。</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <inheritdoc />
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 两个实例并发写 Mods/index.json 会整表覆盖（后保存者吞掉先保存者的改动），
        // 备份目录也会被共享；因此第二个实例提示后退出，不创建窗口。
        _singleInstance = new SingleInstanceGuard(SingleInstanceGuard.CurrentName());
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            ShowMessageBox(
                "HunterBox 已经在运行了。" + Environment.NewLine + Environment.NewLine
                + "请切换到已打开的窗口继续使用。同时运行两个实例会互相覆盖 MOD 索引与备份。",
                "HunterBox");
            Exit();
            return;
        }

        InitializeLogging();

        try
        {
            // 组合根唯一装配点：布局、MOD 存储与设置在进入主窗口前全部就绪；
            // 主题也由 AppServices 持有并读好，窗口内容一创建就按偏好渲染（A-23）。
            ISettings settings = AppServices.CreateDefaultSettings();
            // 旧 sjmodbox.ini 设置迁移的唯一入口：读取主题前完成，保证后续设置读取看到迁移结果。
            LegacyMigrationService.MigrateSettings(
                new BoxPaths(AppContext.BaseDirectory),
                settings,
                AppLogAdapter.Instance);
            BoxPaths paths = new(AppContext.BaseDirectory);
            AppServices services = new(paths, ModRepository.Load(paths, AppLogAdapter.Instance), settings);
            services.LoadTheme();
            _window = new MainWindow(services);
            _window.Activate();
        }
        catch (Exception exception)
        {
            // 兜底：构造期读索引/INI 失败不能让进程静默消失。
            HandleStartupFailure(exception);
        }
    }

    private static void InitializeLogging()
    {
        AppLog.Initialize();
        AppLog.Info("================ 会话开始 ================");
        AppLog.Info($"版本：{AppVersion}（{RuntimeInformation.FrameworkDescription}）");
        AppLog.Info($"系统：{RuntimeInformation.OSDescription}（{(Environment.Is64BitProcess ? "x64" : "x86")}）");
        AppLog.Info($"程序目录：{AppContext.BaseDirectory}");
        AppLog.Info($"当前目录：{Environment.CurrentDirectory}");
        AppLog.Info($"日志目录：{AppLog.Directory}（写入失败降级 {AppLog.FallbackDirectory}）");
        AppLog.Info($"启动参数：{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
    }

    private async void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        string? path = ReportCrash("XamlUnhandled", e.Exception);
        e.Handled = true;

        // async void：先标记已处理（避免进程被带走），再等待提示真正显示完；
        // 提示路径内部有统一对话框门 + 异常兜底 + 有限等待，不会死锁也不会静默失败（C-29）。
        try
        {
            await ShowCrashNoticeAsync(path, canContinue: true);
        }
        catch (Exception noticeError)
        {
            // 提示失败也绝不能再抛回 UI 线程（async void 的异常会升级为新的未处理异常）。
            AppLog.Error("显示崩溃提示失败", noticeError);
            ShowMessageBox(BuildCrashMessage(path), "HunterBox 发生错误");
        }
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        // 该事件无法阻止进程退出：尽量同步落盘 + 弹一个阻塞提示后交给系统结束进程。
        string? path = ReportCrash("AppDomain", e.ExceptionObject as Exception);
        ShowBlockingFatalNotice(path);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 后台任务异常没有 UI 现场，只落盘 + 标记已观察，避免进程被升级为崩溃。
        ReportCrash("UnobservedTask", e.Exception);
        e.SetObserved();
    }

    private static string? ReportCrash(string source, Exception? exception)
    {
        try
        {
            string? path = CrashReporter.WriteReport(
                source,
                exception,
                new CrashContext(AppVersion, Arguments: Environment.GetCommandLineArgs()));
            AppLog.Error(
                $"未处理异常[{source}] → {(path is null ? "崩溃报告写入失败：" + CrashReporter.LastError?.Message : path)}");
            return path;
        }
        catch (Exception reportError)
        {
            // 报告器自身故障也不能再引发二次崩溃。
            AppLog.Error("写崩溃报告时再次失败", reportError);
            return null;
        }
    }

    /// <summary>构造期失败：写报告 + 阻塞提示 + 退出（此时还没有可用的 ContentDialog 宿主）。</summary>
    private void HandleStartupFailure(Exception exception)
    {
        string? path = ReportCrash("Startup", exception);
        try
        {
            _window?.Close();
        }
        catch (Exception closeError)
        {
            AppLog.Warn("关闭失败的窗口出错：" + closeError.Message);
        }

        ShowMessageBox(
            "HunterBox 启动失败：" + exception.Message + Environment.NewLine + Environment.NewLine
            + (path is null
                ? "崩溃报告写入失败：" + (CrashReporter.LastError?.Message ?? "未知原因")
                : "崩溃报告已保存到：" + Environment.NewLine + path)
            + Environment.NewLine + Environment.NewLine
            + "请检查HunterBox目录/日志目录的写入权限后重试；若反复出现请把该报告反馈给作者。",
            "HunterBox 启动失败");
        Exit();
    }

    /// <summary>
    /// 尽力用主窗口的统一对话框门说明崩溃；窗口宿不可用或显示失败时回退系统消息框。
    /// </summary>
    /// <remarks>
    /// 该路径会被 XAML 未处理异常钩子 <c>await</c>：不能 fire-and-forget（旧实现丢弃 <c>ShowAsync</c>
    /// 返回的 Task，已有对话框时异常无人观察、提示静默消失），也不能在 UI 线程上同步阻塞。
    /// </remarks>
    /// <param name="path">崩溃报告路径；<see langword="null"/> 表示报告写入失败。</param>
    /// <param name="canContinue">是否提示「可以继续使用」。</param>
    private async Task ShowCrashNoticeAsync(string? path, bool canContinue)
    {
        string message = BuildCrashMessage(path)
            + (canContinue
                ? Environment.NewLine + Environment.NewLine
                    + "错误已记录，程序可以继续使用；若反复出现请重启，并把上面的崩溃报告反馈给作者。"
                : string.Empty);

        try
        {
            if (_window is MainWindow mainWindow && await mainWindow.TryShowCrashNoticeAsync(message))
            {
                return;
            }
        }
        catch (Exception dialogError)
        {
            AppLog.Warn("崩溃提示对话框打开失败，改用系统消息框：" + dialogError.Message);
        }

        ShowMessageBox(message, "HunterBox 发生错误");
    }

    /// <summary>进程即将退出的致命提示：不依赖 WinUI，直接阻塞式系统消息框。</summary>
    private static void ShowBlockingFatalNotice(string? path)
    {
        ShowMessageBox(
            BuildCrashMessage(path) + Environment.NewLine + Environment.NewLine
            + "程序即将退出，请重新启动；若反复出现请把上面的崩溃报告反馈给作者。",
            "HunterBox 发生错误");
    }

    private static string BuildCrashMessage(string? path) =>
        path is null
            ? "程序遇到未处理的错误，且崩溃报告写入失败：" + (CrashReporter.LastError?.Message ?? "未知原因")
            : "程序遇到未处理的错误，详细信息已写入崩溃报告：" + Environment.NewLine + path;

    private static void ShowMessageBox(string text, string caption)
    {
        try
        {
            // MB_OK | MB_ICONERROR | MB_SETFOREGROUND
            _ = MessageBoxW(nint.Zero, text, caption, 0x00000010u | 0x00010000u);
        }
        catch (Exception messageBoxError)
        {
            // WinUI 未处理异常场景下连系统消息框都可能失败，最后静默（报告已尽量落盘）。
            AppLog.Warn("系统消息框打开失败：" + messageBoxError.Message);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}

/// <summary>界面主题偏好，对应 INI <c>[settings] theme</c>的 <c>system</c>/<c>light</c>/<c>dark</c>。</summary>
internal enum ThemePreference
{
    /// <summary>跟随 Windows 浅色/深色设置（默认）。</summary>
    System,

    /// <summary>始终使用浅色。</summary>
    Light,

    /// <summary>始终使用深色。</summary>
    Dark,
}

/// <summary>
/// <c>[settings] theme</c>的读写与枚举转换：App 启动读取、MainWindow 应用、SettingsDialog 保存共用。
/// </summary>
internal static class AppTheme
{
    /// <summary>主题键所在的 INI 段。</summary>
    public const string Section = SettingsKeys.Section;

    /// <summary>主题键名。</summary>
    public const string Key = SettingsKeys.Theme;

    /// <summary>解析 INI 值；无法识别/缺省时按「跟随系统」处理。</summary>
    /// <param name="raw">INI 里的原始值。</param>
    /// <returns>主题偏好。</returns>
    public static ThemePreference Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        _ => ThemePreference.System,
    };

    /// <summary>转成写回 INI的稳定小写值。</summary>
    /// <param name="theme">主题偏好。</param>
    /// <returns><c>system</c>/<c>light</c>/<c>dark</c>。</returns>
    public static string ToIni(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system",
    };

    /// <summary>转成 WinUI 元素主题；<see cref="ThemePreference.System"/> 对应 <see cref="ElementTheme.Default"/>。</summary>
    /// <param name="theme">主题偏好。</param>
    /// <returns>元素主题。</returns>
    public static ElementTheme ToElementTheme(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>从配置读取主题（缺省 = 跟随系统）。</summary>
    /// <param name="settings">设置端口。</param>
    /// <returns>主题偏好。</returns>
    public static ThemePreference Read(ISettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Parse(settings.Get(Section, Key, SettingsDefaults.Theme));
    }
}
