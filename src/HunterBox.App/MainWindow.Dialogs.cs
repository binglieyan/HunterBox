using HunterBox.Domain.Games;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Migration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HunterBox.App;

/// <summary>MainWindow 的启动提示、旧数据导入与对话框辅助。</summary>
public sealed partial class MainWindow
{

    /// <summary>启动后的提示：索引损坏警告 + 未归属旧 MOD的归游戏引导。</summary>
    private async Task ShowStartupNoticesAsync()
    {
        await TryImportLegacyDataAsync();

        if (!string.IsNullOrWhiteSpace(_services.Store.LoadWarning))
        {
            await ShowMessageAsync("MOD 索引需要修复", _services.Store.LoadWarning!);
        }

        if (_services.Settings.IsReadOnly && !string.IsNullOrWhiteSpace(_services.Settings.LoadWarning))
        {
            await ShowMessageAsync("配置文件只读", _services.Settings.LoadWarning!);
        }

        List<ModRecord> unassigned = [.. _services.Store.UnassignedMods];
        if (unassigned.Count == 0)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "发现未归属的 MOD",
            Content = $"检测到 {unassigned.Count} 个旧数据 MOD 记录没有游戏归属，它们不会出现在任何游戏列表中。\n"
                + $"是否将它们归到当前游戏「{_services.GameSession.Game.DisplayName}」？",
            PrimaryButtonText = "归到当前游戏",
            CloseButtonText = "暂不处理",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        int assigned = _services.CreateGameAssignment()
            .Assign(unassigned.Select(mod => mod.Id), _services.GameSession.Game.Id);

        await SaveStoreAsync();
        ReloadMods(updateStatus: false);
        ViewModel.Status = $"已归属 {assigned} 个 MOD 到「{_services.GameSession.Game.DisplayName}」";
        UpdateActionButtons();
    }

    /// <summary>启动时检测HunterBox数据，询问并执行一次性导入（只读复制，不移动旧文件）。</summary>
    private async Task TryImportLegacyDataAsync()
    {
        if (_services.SettingsCoordinator.IsLegacyImported)
        {
            return;
        }

        LegacyMigrationService scanner = _services.CreateLegacyMigration();
        // 旧库扫描放到后台；结果直接传给对话框，不再在 UI 线程重复 Detect。
        LegacyInventory inventory = await Task.Run(scanner.Detect);
        // 扫描期间窗口可能已关闭，不能再访问 XamlRoot 弹对话框。
        if (_windowClosing)
        {
            return;
        }

        if (!inventory.HasAnything)
        {
            return;
        }

        int modCount = inventory.Games.Sum(g => g.ModCount);
        List<string> summary = [];
        foreach (LegacyGameInventory game in inventory.Games)
        {
            string displayName = GameCatalog.Find(game.GameId)?.DisplayName ?? game.GameId;
            summary.Add($"· {displayName}：{game.ModCount} 个旧 MOD");
        }

        ContentDialog ask = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "检测到旧数据",
            Content = "在程序目录里发现了旧数据：\n\n"
                + string.Join(Environment.NewLine, summary)
                + (inventory.LegacyIniPath is null ? string.Empty : "\n· 旧配置 sjmodbox.ini（会自动迁移）")
                + (inventory.LegacySaveDirectories.Count > 0 ? $"\n· {inventory.LegacySaveDirectories.Count} 个旧存档备份目录" : string.Empty)
                + (inventory.UnreadablePaths.Count > 0
                    ? $"\n· 注意：{inventory.UnreadablePaths.Count} 个旧路径不可读，导入可能不完整"
                    : string.Empty)
                + "\n\n是否现在导入？导入为只读复制，不会移动/删除旧文件。",
            PrimaryButtonText = "导入旧数据",
            CloseButtonText = "暂不导入",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await ShowDialogAsync(ask) != ContentDialogResult.Primary)
        {
            return;
        }

        // 旧数据导入在对话框内用 Task.Run 直接改仓储记录，不在 OperationRunner busy 门禁内；
        // 先等模型预热的单写线程退出，避免与导入的 Metadata 写入交错。
        // 有界等待：预热异常长时间未退出时提示稍后重试，不能把启动提示无限推迟（N-30）。
        const int modelWarmWaitLimitMilliseconds = 10_000;
        int modelWarmWaited = 0;
        while (_modelWarmRunning && modelWarmWaited < modelWarmWaitLimitMilliseconds)
        {
            await Task.Delay(100);
            modelWarmWaited += 100;
        }

        if (_modelWarmRunning)
        {
            await ShowMessageAsync("旧数据导入", "后台仍在识别模型，请稍后再试导入旧数据。");
            return;
        }

        LegacyMigrationDialog dialog = new(_services.CreateLegacyMigration(), inventory, GameCatalog.All, _services.Logger)
        {
            XamlRoot = Root.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Result is null)
        {
            return;
        }

        _services.SettingsCoordinator.MarkLegacyImported();
        await ShowMessageAsync("导入完成", LegacyMigrationDialog.Describe(dialog.Result));
        if (modCount > 0)
        {
            _services.Logger.Info($"旧数据导入完成：{dialog.Result.ImportedMods} 个 MOD");
        }

        ReloadMods();
    }

    /// <summary>
    /// 显示对话框，统一挂上 <see cref="XamlRoot"/> 与当前主题，并通过 <see cref="_dialogGate"/> 串行化。
    /// </summary>
    /// <remarks>
    /// ContentDialog 属于弹出层，不继承窗口根元素（<see cref="Root"/>）的 RequestedTheme；
    /// 不显式设置时，强制「浅色/深色」下对话框仍会跟随系统主题，出现割裂。
    /// WinUI 只允许同时打开一个 ContentDialog：并发 <c>ShowAsync</c> 会抛
    /// <see cref="InvalidOperationException"/>，因此这里用门串行等待，从根上杜绝并发弹窗。
    /// </remarks>
    /// <param name="dialog">已构造好的对话框。</param>
    /// <param name="gateTimeout">
    /// 等待对话框门的上限；<see langword="null"/> 表示一直等（普通交互入口）。
    /// 崩溃提示等「提示本身可能发生在对话框打开期间」的路径传有限超时，超时抛
    /// <see cref="TimeoutException"/> 由调用方回退系统消息框，避免提示被无声挂起。
    /// </param>
    /// <returns>用户选择的按钮结果。</returns>
    /// <exception cref="TimeoutException">等待对话框门超时（仅传了 <paramref name="gateTimeout"/> 时）。</exception>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, TimeSpan? gateTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        // 所有对话框统一在入口检查窗口是否还活着，避免访问已销毁的 XamlRoot。
        if (!IsWindowAlive())
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = _services.ThemeElement;
        if (gateTimeout is { } timeout)
        {
            if (!await _dialogGate.WaitAsync(timeout))
            {
                throw new TimeoutException("等待已有对话框关闭超时");
            }
        }
        else
        {
            await _dialogGate.WaitAsync();
        }

        IBusyCloseGuard? previousGuard = _busyCloseGuard;
        if (dialog is IBusyCloseGuard guard)
        {
            // 对话框忙碌期间拦截主窗口关闭（Alt+F4 关的是主窗口，ContentDialog 的 Closing 拦不到）。
            _busyCloseGuard = guard;
        }

        try
        {
            // 等待对话框门期间窗口也可能关闭：再次检查后再显示。
            return IsWindowAlive() ? await dialog.ShowAsync() : ContentDialogResult.None;
        }
        finally
        {
            _busyCloseGuard = previousGuard;
            _dialogGate.Release();
        }
    }

    /// <summary>窗口是否仍可显示对话框（未在关闭且内容已挂到 XamlRoot）。</summary>
    /// <returns>可显示时返回 <see langword="true"/>。</returns>
    private bool IsWindowAlive() => !_windowClosing && Root.XamlRoot is not null;

    /// <summary>崩溃提示等待已有对话框的上限：超时改用系统消息框，保证提示可见（不静默、不并发弹窗）。</summary>
    private static readonly TimeSpan CrashNoticeDialogTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 用与普通对话框相同的串行门显示崩溃提示（App 的全局异常钩子调用）。
    /// </summary>
    /// <remarks>
    /// 旧实现直接 <c>_ = dialog.ShowAsync()</c>：既有 ContentDialog 打开时会抛
    /// <see cref="InvalidOperationException"/> 且无人观察，提示被静默丢弃。
    /// 现在排队等待同一道门，最多等 <see cref="CrashNoticeDialogTimeout"/>；等待/显示失败时返回
    /// <see langword="false"/>，由 App 回退阻塞式系统消息框，绝不会静默吞掉。
    /// </remarks>
    /// <param name="message">崩溃提示正文。</param>
    /// <returns>对话框成功显示并关闭时为 <see langword="true"/>；宿主不可用或显示失败时为 <see langword="false"/>。</returns>
    internal async Task<bool> TryShowCrashNoticeAsync(string message)
    {
        if (_windowClosing || Root.XamlRoot is null)
        {
            // 窗口关闭中/内容还没挂到 XamlRoot：没有可用的 ContentDialog 宿主。
            return false;
        }

        try
        {
            ContentDialog dialog = new()
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = _services.ThemeElement,
                Title = "程序遇到错误",
                Content = message,
                CloseButtonText = "确定",
            };
            ContentDialogResult result = await ShowDialogAsync(dialog, CrashNoticeDialogTimeout);
            // 窗口在等待期间关闭时 ShowDialogAsync 返回 None，视为未显示，回退系统消息框。
            return result != ContentDialogResult.None || !_windowClosing;
        }
        catch (Exception ex)
        {
            _services.Logger.Error("崩溃提示对话框显示失败，回退系统消息框", ex);
            return false;
        }
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "知道了",
        };
        await ShowDialogAsync(dialog);
    }

    /// <summary>提示用户但「提示本身失败也不能再抛」：对话框打不开时退化为状态栏文案。</summary>
    /// <param name="title">提示标题。</param>
    /// <param name="content">提示正文。</param>
    private async Task TryShowMessageAsync(string title, string content)
    {
        try
        {
            await ShowMessageAsync(title, content);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("显示错误提示失败：" + title, ex);
            ViewModel.Status = title + "：" + content;
        }
    }

}
