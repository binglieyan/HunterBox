using HunterBox.Application.Activation;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>MainWindow 的启用/禁用入口与批量启用禁用。</summary>
public sealed partial class MainWindow
{
    private void EnableSelected_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("启用选中MOD失败", EnableSelectedCoreAsync);

    /// <summary>把 pak 同名冲突整理成用户可照做的提示（最多列出 12 条，其余见日志）。</summary>
    /// <param name="conflict">冲突异常。</param>
    /// <returns>多行提示文本。</returns>
    private string BuildPakConflictText(PakConflictException conflict)
    {
        _services.Logger.WarnBatch([.. conflict.Conflicts.Select(c => $"pak 同名冲突：{c.FileName} 已被「{c.Owner.ModName}」占用")]);
        // 先按条数截断，只为展示项拼前缀，不再物化全部说明串。
        string lines = DialogTextLimits.JoinTruncated(
            conflict.Conflicts,
            DialogTextLimits.ConflictLines,
            Environment.NewLine,
            unit: "个",
            logFile: AppServices.LogFile,
            format: c => $"· {c.FileName}（已被「{c.Owner.ModName}」占用）");
        return $"以下 {conflict.Conflicts.Count} 个 pak 与已启用 MOD 同名但内容不同，无法启用："
            + Environment.NewLine + Environment.NewLine
            + lines
            + Environment.NewLine + Environment.NewLine
            + "请先禁用占用方，或改用「按编号命名」后再启用。";
    }

    /// <summary>「启用选中MOD」：按列表顺序批量启用，失败逐条汇总（不中断其它项）。</summary>
    private async Task EnableSelectedCoreAsync()
    {
        List<ModRow> rows = SelectedRows();
        if (rows.Count == 0)
        {
            await ShowMessageAsync("启用选中MOD", "请先在列表里选中要启用的 MOD");
            return;
        }
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            await ShowMessageAsync("启用选中MOD", "请先设置游戏目录");
            return;
        }

        List<ModRecord> targets = [.. rows.Where(r => !r.Mod.Enabled).Select(r => r.Mod)];
        if (targets.Count == 0)
        {
            await ShowMessageAsync("启用选中MOD", "选中的 MOD 都已经启用。");
            return;
        }

        int skipped = rows.Count - targets.Count;
        ContentDialog confirm = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "启用选中MOD",
            Content = targets.Count == 1
                ? $"确定启用「{targets[0].DisplayName}」？"
                : $"是否按照顺序启用选中的 {targets.Count} 个 MOD？\n后启用的会覆盖先启用的同路径文件。"
                    + (skipped > 0 ? $"\n（已启用中的 {skipped} 个会跳过）" : string.Empty),
            PrimaryButtonText = "启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        int enableEpoch = _operations.Epoch;
        IProgress<(int Done, int Total)> progress = CreateOperationProgress(
            enableEpoch,
            p => $"正在启用选中的 MOD... ({p.Done}/{p.Total})");
        BatchActivationResult? batch = null;
        (bool ok, string? error) = await RunOperationAsync("正在启用选中的 MOD...", token =>
        {
            batch = new ModBatchActivator(CreateActivation()).Enable(targets, PakMode, token, progress);
        });

        if (IsCanceled(error))
        {
            LogBatchWarnings(batch, "启用");
            ReportCanceled(batch is null
                ? "未启用任何 MOD"
                : $"已启用 {batch.Processed}/{batch.Total} 个 MOD，未启用部分保持原状");
            return;
        }

        if (!ok)
        {
            LogBatchWarnings(batch, "启用");
            await ShowMessageAsync("启用选中MOD失败", (error ?? "未知错误") + BuildBatchWarningsSuffix(batch));
            return;
        }

        if (batch is null)
        {
            return;
        }

        if (batch.Canceled)
        {
            LogBatchWarnings(batch, "启用");
            ReportCanceled($"已启用 {batch.Processed}/{batch.Total} 个 MOD，未启用部分保持原状");
            return;
        }

        _services.Logger.WarnBatch([.. batch.Failures.Select(failure => "启用失败：" + failure)]);

        if (batch.Failures.Count > 0)
        {
            ViewModel.Status = $"已启用 {batch.Processed} 个 MOD，{batch.Failures.Count} 个失败";
            await ShowMessageAsync("部分 MOD 启用失败",
                $"成功 {batch.Processed} 个，失败 {batch.Failures.Count} 个：\n\n"
                + DialogTextLimits.JoinTruncated(
                    batch.Failures,
                    DialogTextLimits.WarningLines,
                    "\n",
                    unit: "个",
                    logFile: AppServices.LogFile));
            return;
        }

        await ShowBatchWarningsAsync(batch, "启用");
        ViewModel.Status = $"已按顺序启用 {targets.Count} 个 MOD";
    }

    /// <summary>通知界面重新读取该 MOD 的启用状态（不重建列表，保留滚动位置）。</summary>
    /// <param name="mod">状态已变化的 MOD。</param>
    private void RefreshRowEnabled(ModRecord mod) =>
        _list.AllRows.FirstOrDefault(r => r.Mod.Id == mod.Id)?.NotifyEnabledChanged();

    /// <summary>
    /// 勾选列：入口自身完成「重入判定 + 异常兜底」，
    /// 避免用 fire-and-forget 丢弃 Task（连点曾并发打开两个 ContentDialog，第二个直接抛异常且无人观察）。
    /// </summary>
    private void RowEnable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ModRow row)
        {
            return;
        }

        // 勾选字形是数据驱动的，点击目标始终 = 真实启用状态的取反，不依赖控件自身的勾选状态。
        RunUiAction("切换启用状态", () => SetEnabledAsync(row.Mod, !row.Mod.Enabled));
    }

    private void DisableAll_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("禁用全部MOD失败", DisableAllCoreAsync);

    /// <summary>禁用当前游戏全部已启用 MOD（确认 → 批量禁用 → 汇总失败）。</summary>
    private async Task DisableAllCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            await ShowMessageAsync("提示", "请先设置游戏目录");
            return;
        }

        List<ModRecord> enabled = [.. _services.Store.ModsFor(_services.GameSession.Game.Id).Where(m => m.Enabled)];
        if (enabled.Count == 0)
        {
            await ShowMessageAsync("禁用全部MOD", "当前游戏没有已启用的 MOD");
            return;
        }

        ContentDialog confirm = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "禁用全部MOD",
            Content = $"确定禁用当前游戏的 {enabled.Count} 个 MOD？\n禁用会还原被覆盖的游戏原文件。",
            PrimaryButtonText = "禁用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        int disableEpoch = _operations.Epoch;
        IProgress<(int Done, int Total)> progress = CreateOperationProgress(
            disableEpoch,
            p => $"正在禁用全部... ({p.Done}/{p.Total})");
        BatchActivationResult? batch = null;
        (bool ok, string? error) = await RunOperationAsync("正在禁用全部...", token =>
        {
            batch = new ModBatchActivator(CreateActivation()).Disable(enabled, PakMode, token, progress);
        });

        if (IsCanceled(error))
        {
            LogBatchWarnings(batch, "禁用");
            ReportCanceled(batch is null
                ? "未禁用任何 MOD"
                : $"已禁用 {batch.Processed}/{batch.Total} 个 MOD，未处理部分保持原状");
            return;
        }

        if (!ok)
        {
            LogBatchWarnings(batch, "禁用");
            await ShowMessageAsync("禁用全部MOD失败", (error ?? "未知错误") + BuildBatchWarningsSuffix(batch));
            return;
        }

        if (batch is null)
        {
            return;
        }

        if (batch.Canceled)
        {
            LogBatchWarnings(batch, "禁用");
            ReportCanceled($"已禁用 {batch.Processed}/{batch.Total} 个 MOD，未处理部分保持原状");
            return;
        }

        if (batch.Failures.Count > 0)
        {
            _services.Logger.WarnBatch([.. batch.Failures.Select(failure => "禁用失败：" + failure)]);

            ViewModel.Status = $"禁用失败：{batch.Failures.Count} 个 MOD 未禁用";
            await ShowMessageAsync("禁用全部MOD失败",
                $"{batch.Failures.Count} 个 MOD 禁用失败：\n"
                + DialogTextLimits.JoinTruncated(
                    batch.Failures,
                    DialogTextLimits.WarningLines,
                    "\n",
                    unit: "个",
                    logFile: AppServices.LogFile));
            return;
        }

        await ShowBatchWarningsAsync(batch, "禁用");
        ViewModel.Status = $"已禁用 {enabled.Count} 个 MOD";
    }

    /// <summary>把批量取消/失败路径已产生的逐项告警写入日志。</summary>
    /// <param name="batch">批量结果；可能为 <see langword="null"/>。</param>
    /// <param name="actionName">动作名（启用/禁用）。</param>
    private void LogBatchWarnings(BatchActivationResult? batch, string actionName)
    {
        if (batch is null || batch.Warnings.Count == 0)
        {
            return;
        }

        _services.Logger.WarnBatch([.. batch.Warnings.Select(warning => $"{actionName}（含取消/失败）：{warning}")]);
    }

    /// <summary>把批量告警拼到错误提示后面（失败路径也要让用户看到已处理部分的风险提示）。</summary>
    /// <param name="batch">批量结果；可能为 <see langword="null"/>。</param>
    /// <returns>附加文本；没有告警时为空串。</returns>
    private static string BuildBatchWarningsSuffix(BatchActivationResult? batch)
    {
        if (batch is null || batch.Warnings.Count == 0)
        {
            return string.Empty;
        }

        return Environment.NewLine + Environment.NewLine
            + FormatBatchWarnings(batch, $"另有 {batch.Warnings.Count} 条已处理项的提示：");
    }

    /// <summary>展示批量操作的逐项警告（不影响成功判定；最多弹 8 条，其余写日志）。</summary>
    /// <param name="batch">批量结果。</param>
    /// <param name="actionName">动作名（启用/禁用）。</param>
    private async Task ShowBatchWarningsAsync(BatchActivationResult batch, string actionName)
    {
        if (batch.Warnings.Count == 0)
        {
            return;
        }

        _services.Logger.WarnBatch([.. batch.Warnings.Select(warning => $"{actionName}：{warning}")]);

        await ShowMessageAsync(
            "批量操作提示",
            FormatBatchWarnings(
                batch,
                $"已处理 {batch.Processed}/{batch.Total} 个 MOD，其中有 {batch.Warnings.Count} 条提示："));
    }

    /// <summary>统一的批量告警格式化：标题 + 最多 8 条 + 其余提示「见日志」。</summary>
    /// <param name="batch">批量结果。</param>
    /// <param name="header">首行标题。</param>
    /// <returns>可直接展示的多行文本。</returns>
    private static string FormatBatchWarnings(BatchActivationResult batch, string header) =>
        header
        + Environment.NewLine
        + DialogTextLimits.JoinTruncated(
            batch.Warnings,
            DialogTextLimits.WarningLines,
            Environment.NewLine,
            unit: "条",
            logFile: AppServices.LogFile);

    private Task ToggleAsync(ModRecord mod) => SetEnabledAsync(mod, !mod.Enabled);

    private async Task SetEnabledAsync(ModRecord mod, bool enable)
    {
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            await ShowMessageAsync("提示", "请先设置游戏目录");
            ReloadMods();
            return;
        }

        // 冲突检查与随后的启用共用同一个用例实例（占用索引只构建一次），
        // 扫描本身放到后台线程，避免大 MOD 库在 UI 线程同步规范化路径造成卡顿。
        ModActivationService activation = CreateActivation();
        if (enable)
        {
            IReadOnlyList<string> conflicts = await Task.Run(() => activation.FindLooseFileConflicts(mod));
            if (conflicts.Count > 0)
            {
                _services.Logger.WarnBatch([.. conflicts.Select(file => $"MOD 文件冲突：{file}")]);
                ContentDialog conflictDialog = new()
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "MOD 文件冲突",
                    Content = $"以下 {conflicts.Count} 个文件与已启用 MOD 冲突（后启用的会覆盖先启用的，序号大的优先）：\n\n"
                        + DialogTextLimits.JoinTruncated(
                            conflicts,
                            DialogTextLimits.ConflictLines,
                            "\n",
                            unit: "个",
                            logFile: AppServices.LogFile)
                        + "\n\n是否继续启用？",
                    PrimaryButtonText = "继续启用",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await ShowDialogAsync(conflictDialog) != ContentDialogResult.Primary)
                {
                    ReloadMods();
                    return;
                }
            }
        }

        bool previous = mod.Enabled;
        ActivationReport? report = null;
        PakConflictException? pakConflict = null;
        (bool ok, string? error) = await RunOperationAsync(enable ? "正在启用..." : "正在禁用...", token =>
        {
            try
            {
                report = enable
                    ? activation.Enable(mod, PakMode, token)
                    : activation.Disable(mod, PakMode, token);
            }
            catch (PakConflictException conflict)
            {
                // 结构化冲突留给 UI 线程渲染（异常消息本身也会兜底展示）。
                pakConflict = conflict;
                throw;
            }
        });

        if (!ok)
        {
            // 失败时绝不能让内存里的启用状态与磁盘不一致（否则后续任意 Save 会把它落盘）。
            if (mod.Enabled != previous)
            {
                mod.Enabled = previous;
                try
                {
                    await SaveStoreAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _services.Logger.Error("回滚启用状态失败", ex);
                }
            }

            if (IsCanceled(error))
            {
                RefreshRowEnabled(mod);
                ReportCanceled(enable ? "MOD 未被启用" : "MOD 未被禁用");
                ReloadMods();
                return;
            }

            RefreshRowEnabled(mod);
            if (pakConflict is not null)
            {
                // A-17：把 Conflicts 逐条展示（文件名 + 占用方），而不是只依赖异常消息。
                await ShowMessageAsync("pak 同名冲突", BuildPakConflictText(pakConflict));
                return;
            }

            await ShowMessageAsync(enable ? "启用失败" : "禁用失败", error ?? "未知错误");
            return;
        }

        RefreshRowEnabled(mod);

        // 警告必须让用户看到（P0-2 的“保留并提示”依赖这条通道），同时留日志便于事后排查。
        if (report is not null && report.Warnings.Count > 0)
        {
            _services.Logger.WarnBatch(
                [.. report.Warnings.Select(warning => (enable ? "启用" : "禁用") + $" {mod.DisplayName}：" + warning)]);

            await ShowMessageAsync(
                enable ? "启用提示" : "禁用提示",
                DialogTextLimits.JoinTruncated(
                    report.Warnings,
                    DialogTextLimits.WarningLines,
                    Environment.NewLine,
                    unit: "条",
                    logFile: AppServices.LogFile));
        }

        ViewModel.Status = enable ? $"已启用：{mod.DisplayName}" : $"已禁用：{mod.DisplayName}";
    }

    private ModActivationService CreateActivation() => _services.CreateActivation(_services.GameSession.Game, _services.GameSession.GameRoot);

}
