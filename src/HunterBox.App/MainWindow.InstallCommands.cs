using HunterBox.Application.Common;
using HunterBox.Application.Install;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>MainWindow 的安装/重装/更新入口与安装向导提交（拆出主窗口文件）。</summary>
public sealed partial class MainWindow
{

    /// <summary>「添加MOD」入口：统一经 <see cref="RunUiAction"/> 互斥门进入，异常兜底由入口自身保留。</summary>
    private void Install_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("安装失败", InstallCoreAsync);

    /// <summary>
    /// 「添加MOD」核心流程：选包 → 逐个后台解析/安装。
    /// </summary>
    /// <remarks>
    /// 入口异常兜底与忙碌判定刻意保留在此（外层 <see cref="RunUiAction"/> 另有一道 gate 与通用兜底）：
    /// 这保证即使以后从别处调用核心流程，行为仍与旧入口一致。
    /// </remarks>
    private async Task InstallCoreAsync()
    {
        try
        {
            if (IsFilterMode)
            {
                await ShowMessageAsync("添加MOD", "请先退出搜索模式：清空关键词，然后再点击搜索");
                return;
            }

            if (_operations.IsBusy)
            {
                await ShowMessageAsync("添加MOD", "有其它操作正在进行，请稍后再试");
                return;
            }

            IReadOnlyList<string> files = await DialogPickers.PickModArchivesAsync(this);
            RestoreAfterPicker();
            if (files.Count == 0)
            {
                return;
            }

            int installed = 0;
            int lastId = 0;
            bool canceled = false;
            bool busy = false;
            foreach (string file in files)
            {
                // 解压与识别放到后台线程，界面保持 BusyRing 不冻结； 可取消。
                (InstallStaging? staging, bool stageCanceled, bool stageBusy) = await StageSourceAsync(file, reinstall: false);
                if (stageBusy)
                {
                    busy = true;
                    break;
                }

                if (stageCanceled)
                {
                    canceled = true;
                    break;
                }
                if (staging is null)
                {
                    continue;
                }

                using (staging)
                {
                    (InstallResult? result, bool commitCanceled) = await ShowInstallDialogAsync(staging, "安装 MOD", "安装");
                    if (commitCanceled)
                    {
                        canceled = true;
                        break;
                    }
                    if (result is null)
                    {
                        continue;
                    }

                    installed++;
                    lastId = result.Record.Id;
                }
            }

            // 安装批次结束后的重建、状态文案与选中逻辑由协调器统一决定（A-06）。
            _installRefresh.AfterInstallBatch(installed, lastId, busy, canceled);
        }
        catch (Exception ex)
        {
            // 入口兜底，失败弹窗 + 日志（外层 RunUiAction 还有一道通用兜底，绝不让异常冒到全局）。
            _services.Logger.Error("添加MOD入口异常", ex);
            await ShowMessageAsync("安装失败", OperationError.From(ex).UserMessage);
        }
    }

    private void Reinstall_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("重装失败", ReinstallCoreAsync);

    /// <summary>重装：优先用记录的安装源，找不到时让用户选包，再走公共重装流程。</summary>
    private async Task ReinstallCoreAsync()
    {
        if (SelectedRow is not ModRow row)
        {
            return;
        }

        if (row.Mod.Enabled)
        {
            await ShowMessageAsync("重装MOD", "请先禁用MOD，再操作");
            return;
        }

        // 优先用记录的安装源；找不到再让用户选包。
        string? source = InstallSourceLocator.FindExisting(row.Mod, _services.FileSystem);
        if (source is null)
        {
            string? recorded = InstallSourceLocator.GetRecorded(row.Mod);
            if (!string.IsNullOrWhiteSpace(recorded))
            {
                await ShowMessageAsync(
                    "找不到原始安装文件",
                    $"记录的安装源已不存在：\n{recorded}\n\n请重新选择安装包。");
            }

            source = await DialogPickers.PickSingleArchiveAsync(this);
            RestoreAfterPicker();
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }
        }

        await RunReinstallFlowAsync(row, source, "重装 MOD", "重装");
    }

    private void UpdateMod_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("更新失败", UpdateModCoreAsync);

    private async Task UpdateModCoreAsync()
    {
        try
        {
            if (SelectedRow is not ModRow row)
            {
                return;
            }

            if (row.Mod.Enabled)
            {
                await ShowMessageAsync("用新包更新", "请先禁用MOD，再操作");
                return;
            }

            if (_operations.IsBusy)
            {
                await ShowMessageAsync("用新包更新", "有其它操作正在进行，请稍后再试");
                return;
            }

            // 更新 = 另选一个本地包，仍走安装向导（可改元数据/选组/手动树）。
            string? source = await DialogPickers.PickSingleArchiveAsync(this);
            RestoreAfterPicker();
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            await RunReinstallFlowAsync(row, source, "用新包更新", "更新");
        }
        catch (Exception ex)
        {
            _services.Logger.Error("更新MOD入口异常", ex);
            await ShowMessageAsync("更新失败", OperationError.From(ex).UserMessage);
        }
    }

    /// <summary>重装/更新公共流程：后台解析 → 安装向导 → 后台提交（保留 Id/Index/GameId）。</summary>
    private async Task RunReinstallFlowAsync(ModRow row, string source, string title, string primaryText)
    {
        (InstallStaging? staging, bool stageCanceled, bool stageBusy) =
            await StageSourceAsync(source, reinstall: true, row.Mod.Id);
        if (stageBusy || stageCanceled || staging is null)
        {
            return;
        }

        using (staging)
        {
            (InstallResult? result, bool canceled) = await ShowInstallDialogAsync(staging, title, primaryText);
            if (canceled || result is null)
            {
                return;
            }
        }

        // 重装/更新后的缓存失效、重建、状态文案与选中由协调器统一处理（A-06）。
        _installRefresh.AfterReinstall(row.Mod.Id, row.Mod.DisplayName, primaryText);
    }

    /// <summary>当前「安装选项」：0=无操作 1=复制到HunterBox Download 2=移动到HunterBox Download。</summary>
    private InstallSourceMode SourceArchiveMode => InstallSourceSettings.Parse(
        _services.Settings.Get(InstallSourceSettings.Section, InstallSourceSettings.Key, "0"));

    /// <summary>后台解析安装源（解压/识别在 Task.Run，界面不冻结： 可取消）。</summary>
    /// <param name="source">安装源路径。</param>
    /// <param name="reinstall">是否重装/更新。</param>
    /// <param name="modId">重装目标 MOD ID。</param>
    /// <returns>暂存结果（失败为 <see langword="null"/>），以及是否被用户取消。</returns>
    private async Task<(InstallStaging? Staging, bool Canceled, bool Busy)> StageSourceAsync(
        string source,
        bool reinstall,
        int modId = 0)
    {
        string message = reinstall ? "正在解析安装包（重装/更新）..." : "正在解析安装包...";
        // 解压进度回调到状态栏（Progress<T> 会回到 UI 线程）；用操作代次丢弃晚到进度。
        int stageEpoch = _operations.Epoch;
        IProgress<ArchiveProgress> progress = CreateArchiveProgress(
            stageEpoch,
            p => p.Percent > 0 ? $"{message} {p.Percent}%" : message);
        (InstallStaging? staging, string? error) = await RunBackgroundAsync(message, token =>
            InstallWorkflow.Stage(Context.Installer, source, reinstall, modId, progress, token));

        if (IsCanceled(error))
        {
            _services.Logger.Info($"解析安装包已取消：{source}");
            ViewModel.Status = "操作已取消";
            return (null, true, false);
        }

        if (string.Equals(error, BusyReentryMessage, StringComparison.Ordinal))
        {
            // 忙碌门禁拒绝不是「解析失败」：只提示，不记假错误日志、不弹「无法解析安装包」。
            // 通过 Busy 状态返回给调用方，避免把用户已选文件静默跳过。
            ViewModel.Status = BusyReentryMessage;
            return (null, false, true);
        }

        if (staging is null && !string.IsNullOrWhiteSpace(error))
        {
            _services.Logger.Error($"解析 MOD 失败：{source}", new InvalidOperationException(error));
            await ShowMessageAsync("无法解析安装包", $"{Path.GetFileName(source)}：{error}");
        }

        return (staging, false, false);
    }

    /// <summary>显示安装向导并在后台提交（可取消）；失败弹窗并记录日志。</summary>
    /// <param name="staging">已解压并识别的安装暂存。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="primaryButtonText">主按钮文案（安装/重装/更新）。</param>
    /// <returns>安装结果（取消/失败为 <see langword="null"/>），以及是否被用户取消后台提交。</returns>
    private async Task<(InstallResult? Result, bool Canceled)> ShowInstallDialogAsync(
        InstallStaging staging,
        string title,
        string primaryButtonText)
    {
        // 对话框 InfoBar 只展示前 N 条，先把全部安装/FOMOD 告警批量写入日志。
        _services.Logger.WarnBatch([.. staging.Inspection.Warnings.Select(warning => "安装提示：" + warning)]);
        if (staging.Fomod is not null)
        {
            _services.Logger.WarnBatch([.. staging.Fomod.Warnings.Select(warning => "安装提示（FOMOD）：" + warning)]);
        }

        InstallDialog dialog = new(staging, _services.FileSystem, SourceArchiveMode)
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            PrimaryButtonText = primaryButtonText,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            ViewModel.Status = "已取消安装";
            return (null, false);
        }

        // 在 UI 线程读取一次控件快照；后台提交时不再访问 UI（避免跨线程访问 TextBox 崩溃）。
        InstallCommitRequest request;
        try
        {
            request = dialog.CaptureCommitRequest();
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync(staging.IsReinstall ? "重装失败" : "安装失败", ex.Message);
            return (null, false);
        }

        InstallResult? result = null;
        ModelGame installGame = CurrentModelGame;

        // 后台模型预热可能正占用忙碌门；先等它结束，而不是让用户走完向导后收到「有其它操作正在进行」。
        if (!await WaitForBackgroundOperationAsync(TimeSpan.FromSeconds(30)))
        {
            await ShowMessageAsync(
                staging.IsReinstall ? "重装失败" : "安装失败",
                "后台仍在识别模型，请稍后再试安装。");
            return (null, false);
        }

        (bool ok, string? error) = await RunOperationAsync(
            staging.IsReinstall ? "正在重装..." : "正在安装...",
            async token =>
            {
                // 复制/提交阶段在后台线程执行；取消只在写暂存/复制时生效，保证不留下半成品与记录。
                result = _installWorkflow.Commit(staging, request, installGame, token);
                await Task.CompletedTask;
            });

        if (IsCanceled(error))
        {
            ViewModel.Status = "操作已取消";
            return (null, true);
        }

        if (!ok)
        {
            await ShowMessageAsync(staging.IsReinstall ? "重装失败" : "安装失败", error ?? "未知错误");
            return (null, false);
        }

        if (result is not null && result.Warnings.Count > 0)
        {
            // 批量写全日志后，弹窗里的「其余见日志」才成立。
            _services.Logger.WarnBatch([.. result.Warnings.Select(warning => "安装提示：" + warning)]);

            await ShowMessageAsync(
                "安装提示",
                DialogTextLimits.JoinTruncated(
                    result.Warnings,
                    DialogTextLimits.WarningLines,
                    "\n",
                    unit: "条",
                    logFile: AppServices.LogFile));
        }

        return (result, false);
    }

}
