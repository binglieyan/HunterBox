using HunterBox.Application.Common;
using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace HunterBox.App;

/// <summary>MainWindow 的首帧加载与窗口初始化/主题/标题栏。</summary>
public sealed partial class MainWindow
{

    /// <summary>
    /// 首帧加载：窗口框架先渲染，数据修复/重扫放到后台线程，最后只刷新一次列表。
    /// </summary>
    /// <remarks>
    /// 旧实现把「注册表探测 → 旧路径修复 → Mods 重扫 → 两次 ReloadMods（每次逐 MOD 写索引）」
    /// 全串行跑在 UI 线程上，MOD/pak 多时窗口白屏数十秒（用户观感=卡死）。
    /// 现在：<see cref="Task.Yield"/> 让首帧先绘制 → <see cref="RunBackgroundAsync{T}"/> 在后台做修复/重扫
    /// → 抑制 <see cref="GameCombo_SelectionChanged"/> 里的立即刷新 → 最后统一 <see cref="ReloadMods"/> 一次。
    /// </remarks>
    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            ResizeForDpi(DefaultWidth, DefaultHeight);
            _minSize ??= new WindowMinSize(MinWindowWidth, MinWindowHeight);
            _minSize.Install(WinRT.Interop.WindowNative.GetWindowHandle(this));
            ApplyTitleBarTheme();
            Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
            Activated += OnWindowActivated;

            // 让出一次消息循环：Loaded 在首次布局期间触发，先渲染首帧再开始重 IO。
            await Task.Yield();

            bool startupSaveFailed = false;
            (StartupPreparation? preparation, string? preparationError) = await RunBackgroundAsync(
                "正在准备 MOD 数据...",
                token => _startup.Prepare(token));
            if (preparation is null)
            {
                if (IsCanceled(preparationError))
                {
                    ReportCanceled("启动准备已取消，列表可能不完整");
                }
                else if (!string.IsNullOrWhiteSpace(preparationError)
                    && !string.Equals(preparationError, BusyReentryMessage, StringComparison.Ordinal))
                {
                    // 修复/重扫属于「尽力而为」：失败也要继续加载列表，不能让启动卡住。
                    _services.Logger.Warn("启动准备失败，继续加载列表：" + preparationError);
                }
            }
            else
            {
                _startup.Log(preparation);

                // 启动保存是登记的异步任务；等它落盘一次。失败提示留到启动流程末尾再追加，
                // 否则会被模型识别/ReloadMods 的状态覆盖。
                startupSaveFailed = !await _startup.WaitForPendingSaveAsync();
            }

            // SelectInitialGame 会同步触发 SelectionChanged；启动阶段只允许最后统一刷新一次。
            _suppressGameReload = true;
            bool modelPrewarmCanceled = false;
            bool modelPrewarmClassified = false;
            try
            {
                SelectInitialGame();

                // 启动预热放到后台：先补自动分类、再算备注文本，避免首次 Reload 在 UI 线程扫 pak。
                ModelGame modelGame = CurrentModelGame;
                string prewarmGameId = _services.GameSession.Game.Id;
                List<ModRecord> mods = [.. _services.Store.ModsFor(prewarmGameId)];
                // 预热前按 MOD 数扩容，避免首次启动（无持久缓存）时结果被初始容量淘汰。
                _modelPanel.EnsureTextCacheCapacity(mods.Count);
                // 先渲染列表（缓存未命中的行显示占位），再等后台预热；
                // 升级首启整库重扫期间用户至少能看到列表与滚动，而不是空白等待。
                ReloadMods(scheduleWarm: false);
                // 首帧行构建已经算过文本签名，后台预热复用同一记录实例的签名，不再全库哈希两遍。
                Dictionary<int, KnownTextSignature> knownSignatures = [];
                foreach (ModRow row in _list.AllRows)
                {
                    knownSignatures[row.Mod.Id] = KnownTextSignature.From(row.Mod, row.TextSignature);
                }

                // 跨会话条目的根层 pak 戳校验已并入后台预热，不再在启动关键路径上做全量 stat。
                // showStatus 传 false：上面 ReloadMods 已写「已加载 N 个 MOD」，而
                // OperationRunner.ExitBusy 只复位忙碌态、不恢复状态栏；若这里写「正在识别模型...」，
                // 预热结束后状态栏会永久停在那句，直到用户手动刷新（启动状态栏残留的修复）。
                (ModelPrewarmResult? warm, string? warmError) = await RunBackgroundAsync(
                    "正在识别模型...",
                    token => _modelPanel.Prewarm(mods, modelGame, token, knownSignatures),
                    showStatus: false);
                modelPrewarmCanceled = warm?.Canceled == true || IsCanceled(warmError);
                if (warm is not null)
                {
                    modelPrewarmClassified = warm.ClassificationChanged;
                    // 取消时也回填「已完成项」并落盘：后台预热已把自动分类写进内存记录，
                    // 这里不保存就会「界面显示分类、磁盘没有记录」，退出即丢失（C-23）。
                    _modelPanel.DropInvalidated(warm.InvalidatedIds, prewarmGameId);
                    _modelPanel.StoreText(prewarmGameId, warm.TextEntries);
                    foreach ((int id, string signature) in warm.NoModelEntries)
                    {
                        _classificationNoModel[ClassificationKey(prewarmGameId, id)] = signature;
                    }

                    if (warm.ClassificationChanged)
                    {
                        await TrySaveStoreAsync("启动自动分类");
                    }

                    if (warm.Canceled)
                    {
                        _services.Logger.Info($"启动模型预热已取消：已保存取消前完成的 {warm.TextEntries.Count} 项模型信息");
                    }
                }
                else if (!IsCanceled(warmError) && !string.IsNullOrWhiteSpace(warmError))
                {
                    // 预热失败曾只写日志，用户会以为已完成；给一次可感知的状态提示（不弹窗）。
                    // 该提示是一次性的，后续预热成功时由 ClearModelWarmFailureStatus 清除。
                    _services.Logger.Warn("启动模型预热失败：" + warmError);
                    ViewModel.Status = ModelWarmFailureStatus;
                }
            }
            finally
            {
                _suppressGameReload = false;
            }

            // 只有分类改写了显示字段才整表重建；文本变化用原地回填，避免启动时列表闪烁/滚动回顶。
            if (modelPrewarmClassified)
            {
                ReloadMods(scheduleWarm: !modelPrewarmCanceled);
            }
            else
            {
                // 一次遍历完成回填与「是否还有缺失」判定，全部命中时不再空跑一轮预热。
                bool missing = BackfillModelTexts();
                if (!modelPrewarmCanceled && missing)
                {
                    ScheduleModelWarm();
                }
            }

            if (modelPrewarmCanceled)
            {
                ReportCanceled("模型识别已停止，已保存取消前完成的部分");
            }

            UpdateActionButtons();
            if (startupSaveFailed)
            {
                // 追加到最终状态（不覆盖「已加载 N 个 MOD」/取消提示），用户能实际看到保存失败。
                ViewModel.AppendStatus("索引保存失败，修复/重扫结果可能未落盘（下次启动会重新检测，详见日志）");
            }

            await ShowStartupNoticesAsync();
        }
        catch (Exception ex)
        {
            // async void 兜底：启动流程任何异常都只提示，不让进程崩溃。
            OperationError error = OperationError.From(ex);
            _services.Logger.Error("启动初始化失败", ex);
            ViewModel.Status = "启动初始化失败：" + error.UserMessage;
            await TryShowMessageAsync("启动初始化失败", error.UserMessage);
        }
    }

    private void ResizeForDpi(int width, int height)
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }

    /// <summary>窗口标题：品牌 + 版本 + 当前游戏，切换游戏时更新。</summary>
    private void UpdateWindowTitle() =>
        Title = $"HunterBox v{App.AppVersion} - {_services.GameSession.Game.DisplayName}";

    /// <summary>设置窗口/任务栏图标（exe的 ApplicationIcon 与 Assets/hunterbox.ico 双份兜底）。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "hunterbox.ico");
            if (_services.FileSystem.FileExists(icon))
            {
                AppWindow.SetIcon(icon);
                return;
            }

            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                AppWindow.SetIcon(exe);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("设置窗口图标失败：" + ex.Message);
        }
    }

    /// <summary>窗口重新激活后重新应用标题栏主题（系统选择器关闭后 DWM 可能重置颜色）。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(ApplyTitleBarTheme);
    }

    /// <summary>系统选择器关闭后把焦点/激活状态还回主窗口（标题栏颜色由 Activated 重新应用）。</summary>
    private void RestoreAfterPicker() => Activate();

    /// <summary>让系统标题栏颜色跟随内容主题，消除「浅色标题栏 + 深色内容」割裂。</summary>
    private void ApplyTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        bool dark = Root.ActualTheme != ElementTheme.Light;
        Color background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        Color foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        Color hover = dark ? Color.FromArgb(255, 51, 51, 51) : Color.FromArgb(255, 229, 229, 229);
        Color pressed = dark ? Color.FromArgb(255, 64, 64, 64) : Color.FromArgb(255, 209, 209, 209);
        Color inactive = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 96, 96, 96);

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        // 非活动状态（例如打开系统文件/目录选择器时）也要保持主题色，
        // 否则 Windows 会回退到默认非活动色，看起来标题栏突然变黑。
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactive;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactive;
    }

}
