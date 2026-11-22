using HunterBox.Application.Common;
using HunterBox.Application.Models;
using HunterBox.Application.Install;
using HunterBox.Domain.Models;
using HunterBox.Infrastructure.Mods;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace HunterBox.App;

/// <summary>MainWindow 的拖拽入口：拖入压缩包安装、拖入文件夹改模型。</summary>
public sealed partial class MainWindow
{
    private void ModsList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
    }

    /// <summary>拖拽入口：统一经 <see cref="RunUiAction"/> 互斥门进入（含其对话框与后续安装流程）。</summary>
    /// <remarks>
    /// 拖拽数据（<see cref="DragEventArgs.DataView"/>）只在事件处理期间有效，而 <see cref="RunUiAction"/>
    /// 会先等待后台保存（最长 10 秒）。因此先同步取 deferral 延长拖拽生命周期，再由入口在
    /// 重入返回、流程结束或异常时统一释放。
    /// </remarks>
    private void ModsList_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        RunUiAction("无法通过拖拽安装", () => DropCoreAsync(e), deferral.Complete);
    }

    /// <summary>
    /// 拖拽核心流程：文件夹改模型 / 压缩包逐个安装。
    /// </summary>
    /// <remarks>
    /// 入口异常兜底与忙碌判定刻意保留在此（外层 <see cref="RunUiAction"/> 另有一道 gate 与通用兜底）：
    /// 这保证即使以后从别处调用核心流程，行为仍与旧入口一致。
    /// </remarks>
    private async Task DropCoreAsync(DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                return;
            }

            // 拖拽事件对象只在处理期间有效：GetStorageItemsAsync 必须在第一个 await 前发起。
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            List<string> paths = [.. items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p))];
            if (paths.Count == 0)
            {
                return;
            }

            // 全为文件夹 = 直接改这些文件夹里的模型 ID；压缩包/单文件仍走安装流程。
            int directoryCount = paths.Count(path => _services.FileSystem.DirectoryExists(path));
            if (directoryCount > 0)
            {
                if (directoryCount != paths.Count)
                {
                    await ShowMessageAsync(
                        "拖入内容",
                        "请分开拖入：文件夹用于直接修改模型ID，压缩包/单文件用于安装 MOD。");
                    return;
                }

                if (_operations.IsBusy)
                {
                    await ShowMessageAsync("MOD文件修改", "有其它操作正在进行，请稍后再试");
                    return;
                }

                foreach (string directory in paths)
                {
                    // 取消扫描后不再处理后续文件夹（已完成的部分保留）。
                    bool scanCanceled = await EditModelFolderAsync(directory);
                    if (scanCanceled)
                    {
                        break;
                    }
                }

                return;
            }

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

            // 先分类再处理，错类型只提示不崩溃；非压缩包不进 Stage。
            IReadOnlyList<InstallEntryDecision> decisions = InstallEntryClassifier.ClassifyAll(
                paths,
                _services.FileSystem);
            List<string> unsupported = [.. decisions
                .Where(d => d.Kind == InstallEntryKind.Unsupported)
                .Select(d => Path.GetFileName(d.Path))];
            List<string> unreadable = [.. decisions
                .Where(d => d.Kind == InstallEntryKind.Unreadable)
                .Select(d => Path.GetFileName(d.Path))];
            if (unreadable.Count > 0)
            {
                // 读不出来（被杀毒软件扫描/其它程序占用）不是类型问题，提示重试而不是“不支持”。
                _services.Logger.WarnBatch([.. unreadable.Select(name => "拖拽安装：无法读取 " + name)]);

                await ShowMessageAsync(
                    "无法读取文件",
                    "以下文件无法读取（可能被杀毒软件扫描或被其它程序占用），请稍后重试：\n"
                    + DialogTextLimits.JoinTruncated(
                        unreadable,
                        DialogTextLimits.DetailLines,
                        "\n",
                        unit: "个",
                        logFile: AppServices.LogFile));
            }

            if (unsupported.Count > 0)
            {
                _services.Logger.WarnBatch([.. unsupported.Select(name => "拖拽安装：不支持的文件类型 " + name)]);

                await ShowMessageAsync(
                    "不支持的文件类型",
                    "以下文件不是可安装的 MOD 文件：\n"
                    + DialogTextLimits.JoinTruncated(
                        unsupported,
                        DialogTextLimits.DetailLines,
                        "\n",
                        unit: "个",
                        logFile: AppServices.LogFile)
                    + "\n\n支持：.zip / .7z / .rar 压缩包、.pak / .lua / .dll 单文件（文件夹拖入=改模型ID）。");
            }

            // 多文件保留逐个安装（C的改进，不回退为只处理第一个）。
            int installed = 0;
            int lastId = 0;
            bool canceled = false;
            bool busy = false;
            foreach (InstallEntryDecision decision in decisions.Where(
                d => d.Kind is not (InstallEntryKind.Unsupported or InstallEntryKind.Unreadable)))
            {
                (InstallStaging? staging, bool stageCanceled, bool stageBusy) =
                    await StageSourceAsync(decision.Path, reinstall: false);
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

            ReloadMods(updateStatus: false);
            if (busy)
            {
                ViewModel.Status = BusyReentryMessage;
            }
            else if (canceled)
            {
                ReportCanceled(installed > 0 ? $"已通过拖拽安装 {installed} 个 MOD，其余未安装" : "本次没有安装任何 MOD");
            }
            else if (installed > 0)
            {
                ViewModel.Status = $"已通过拖拽安装 {installed} 个 MOD";
                SelectAndScrollToMod(lastId);
            }
            else
            {
                ViewModel.Status = RestoreLoadedStatus();
            }

            UpdateActionButtons();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("拖拽安装异常", ex);
            await ShowMessageAsync("无法通过拖拽安装", OperationError.From(ex).UserMessage);
        }
    }

    /// <summary>
    /// 拖文件夹到列表：扫描文件夹里的模型（散装文件 + 根层 pak 条目），
    /// 打开同一个模型编辑器；应用后直接改名文件夹内文件（根层 pak 改 TOC 路径哈希）。
    /// </summary>
    /// <param name="directory">拖入的文件夹路径。</param>
    /// <returns>扫描是否被用户取消（取消后调用方应停止处理后续文件夹）。</returns>
    /// <remarks>
    /// 扫描会遍历整个文件夹并读取根层 pak 的条目表（大 pak 可达数秒），
    /// 因此统一走 <see cref="RunBackgroundAsync{T}"/>：UI 保持 BusyRing/取消按钮，不冻结界面。
    /// </remarks>
    private async Task<bool> EditModelFolderAsync(string directory)
    {
        string full = Path.GetFullPath(directory);
        string displayName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = full;
        }

        ModelGame scanGame = CurrentModelGame;
        (FolderModelScan? scan, string? scanError) = await RunBackgroundAsync(
            "正在扫描MOD文件夹...",
            token =>
            {
                // Scan 内部没有取消点：开始/结束各检查一次，取消只在扫描完成后生效（扫描只读，无副作用）。
                token.ThrowIfCancellationRequested();
                FolderModelScan result = ModelFolderEditor.Scan(
                    full,
                    scanGame,
                    _services.PakEntryPathReader,
                    _services.ModelNames,
                    _services.FileSystem,
                    _services.Logger.Warn);
                token.ThrowIfCancellationRequested();
                return result;
            });

        if (scan is null)
        {
            if (IsCanceled(scanError))
            {
                ReportCanceled("已取消扫描MOD文件夹");
                return true;
            }

            if (string.Equals(scanError, BusyReentryMessage, StringComparison.Ordinal))
            {
                ViewModel.Status = BusyReentryMessage;
                return true;
            }

            _services.Logger.Warn($"扫描MOD文件夹失败（{full}）：{scanError}");
            await ShowMessageAsync("MOD文件夹修改", "无法读取文件夹：" + (scanError ?? "未知错误"));
            return false;
        }

        if (scan.Models.Count == 0)
        {
            string message = "没有可修改的模型";
            if (scan.UnsupportedPaks.Count > 0)
            {
                _services.Logger.WarnBatch([.. scan.UnsupportedPaks.Select(pak => $"MOD文件夹修改：pak 未参与识别 {pak}")]);

                message = "没有可修改的模型。以下 pak 未参与识别："
                    + Environment.NewLine
                    + DialogTextLimits.JoinTruncated(
                        scan.UnsupportedPaks,
                        DialogTextLimits.WarningLines,
                        Environment.NewLine,
                        unit: "个",
                        logFile: AppServices.LogFile);
            }

            await ShowMessageAsync("MOD文件夹修改", message);
            return false;
        }

        ModelGame game = scan.Models[0].Game;
        // 文件夹模式：不显示「仅显示未使用的ID」（文件夹还没安装，没有已用身份可过滤）。
        ModelEditorDialog dialog = new(
            scan.Models,
            new ModelCatalog(game),
            usedIds: null,
            displayName,
            modEnabled: false,
            folderMode: true)
        {
            XamlRoot = Root.XamlRoot,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        IReadOnlyList<(string From, string To)> changes = dialog.Changes;
        if (changes.Count == 0)
        {
            await ShowMessageAsync("MOD文件夹修改", "没有任何改动");
            return false;
        }

        PakModelRewriteService pakService = _services.CreatePakRewrite();
        ModelRewriteSummary? summary = null;
        int folderEpoch = _operations.Epoch;
        IProgress<(int Done, int Total)> progress = CreateOperationProgress(
            folderEpoch,
            p => $"正在改写模型... ({p.Done}/{p.Total})");

        (bool ok, string? error) = await RunOperationAsync("正在改写模型...", token =>
        {
            summary = ModelFolderEditor.Rewrite(
                full,
                changes,
                pakService,
                game,
                _services.PakEntryPathReader,
                _services.FileSystem,
                progress,
                token);
            return Task.CompletedTask;
        });

        // 文件夹改写结果弹窗会截断并提示「见日志」，这里先批量写全提示与跳过的 pak。
        if (summary is not null)
        {
            _services.Logger.WarnBatch(
            [
                .. summary.Warnings.Select(warning => $"模型文件夹改写提示（{displayName}）：{warning}"),
                .. summary.UnsupportedPaks.Select(pak => $"模型文件夹改写跳过 pak（{displayName}）：{pak}"),
            ]);
        }

        if (IsCanceled(error))
        {
            ReportCanceled(summary is { HasChanges: true }
                ? $"已改写 {summary.EntriesChanged} 个 pak 条目、{summary.FilesMoved} 个散装文件，其余未处理"
                : "模型文件未改写");
            return false;
        }

        if (!ok)
        {
            await ShowMessageAsync("MOD文件夹修改失败", error ?? "未知错误");
            return false;
        }

        if (summary is null || !summary.HasChanges)
        {
            string text = "没有任何改动";
            if (summary is not null && (summary.UnsupportedPaks.Count > 0 || summary.Warnings.Count > 0))
            {
                text += Environment.NewLine + Environment.NewLine + ModelRewriteText.Build(summary);
            }

            await ShowMessageAsync("MOD文件夹修改", text);
            return false;
        }

        await ShowMessageAsync(
            "MOD文件夹修改",
            "修改完成。" + Environment.NewLine + Environment.NewLine + ModelRewriteText.Build(summary));
        ViewModel.Status = $"模型文件夹修改完成：{displayName}"
            + $"（已改写 {summary.EntriesChanged} 个 pak 条目、{summary.FilesMoved} 个散装文件）";
        return false;
    }
    /// <summary>右键菜单用：只重新复制当前选中的单个 MOD。</summary>
    private async Task RecopyOneAsync(ModRow row)
    {
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            await ShowMessageAsync("提示", "请先设置游戏目录");
            return;
        }

        if (!row.Mod.Enabled)
        {
            await ShowMessageAsync("重新复制MOD", "该 MOD 尚未启用，请先启用。");
            return;
        }

        (bool ok, string? error) = await RunOperationAsync("正在重新复制...", token =>
            CreateActivation().Recopy(row.Mod, PakMode, token));

        if (!ok)
        {
            if (IsCanceled(error))
            {
                ReportCanceled("未重新复制该 MOD");
                return;
            }

            await ShowMessageAsync("重新复制失败", error ?? "未知错误");
            return;
        }

        ViewModel.Status = $"已重新复制：{row.Mod.DisplayName}";
    }

}
