using HunterBox.Application.Activation;
using HunterBox.Application.Cleanup;
using HunterBox.Domain.Mods;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using HunterBox.Infrastructure.Games;

namespace HunterBox.App;

/// <summary>MainWindow 的维护命令：卸载/清理/重新复制/存档/启动游戏。</summary>
public sealed partial class MainWindow
{

    private void Uninstall_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("卸载失败", UninstallCoreAsync);

    /// <summary>卸载：确认 →（启用中先安全禁用）→ 删除目录与记录。入口保护由 <see cref="RunUiAction"/> 统一提供。</summary>
    private async Task UninstallCoreAsync()
    {
        if (SelectedRow is not ModRow row)
        {
            return;
        }

        // 任何卸载都先确认（修复：未启用 MOD 原来会无确认直接删目录）。
        ContentDialog confirm = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "卸载 MOD",
            Content = row.Mod.Enabled
                ? $"「{row.Mod.DisplayName}」正在启用中。卸载会先安全禁用（还原被覆盖的游戏原文件），再删除 MOD 文件与记录。\n\n确定继续？"
                : $"确定卸载「{row.Mod.DisplayName}」？\n将删除 Mods/{row.Mod.Id} 目录与 MOD 记录，不可恢复。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        bool removed = false;
        (bool ok, string? error) = await RunOperationAsync("正在卸载...", token =>
        {
            removed = _maintenance.Uninstall(row.Mod, _services.GameSession.Game, _services.GameSession.GameRoot, PakMode, token);
            if (!removed)
            {
                throw new IOException("卸载失败：记录或目录未能删除");
            }
        });

        if (!ok)
        {
            if (IsCanceled(error))
            {
                ReportCanceled("卸载未执行");
                return;
            }

            await ShowMessageAsync("卸载失败", error ?? "未知错误");
            return;
        }

        string[] leftovers = [];
        try
        {
            leftovers = _services.FileSystem.EnumerateDirectories(
                _services.Paths.ModsDirectory, $"{row.Mod.Id}.old-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.Warn($"检查卸载残留目录失败：{ex.Message}");
        }

        ViewModel.Status = leftovers.Length == 0
            ? $"已卸载：{row.Mod.DisplayName}"
            : $"已卸载：{row.Mod.DisplayName}（残留目录 {leftovers.Length} 个，可手动删除：{leftovers[0]}）";
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("清理失败", CleanupCoreAsync);

    /// <summary>删除游戏目录里的 MOD 文件（确认范围 → 后台清理 → 汇总）。</summary>
    private async Task CleanupCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            await ShowMessageAsync("提示", "请先设置游戏目录");
            return;
        }
        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "删除全部MOD文件",
            Content = "「仅删HunterBox安装的」：安全禁用当前游戏的所有 MOD，并还原被覆盖的游戏原文件。\n"
                + "「清理HunterBox记录残留」：在此基础上再按 MOD 记录删除游戏目录里仍存在的HunterBox文件，\n"
                + "不会整目录删除，也不会碰你手动放入的文件。\n\n"
                + "不会删除 MOD 记录与备份，随时可以重新启用；清理后建议先校验游戏完整性。",
            PrimaryButtonText = "仅删HunterBox安装的",
            SecondaryButtonText = "清理HunterBox记录残留",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.None)
        {
            return;
        }
        CleanupScope scope = result == ContentDialogResult.Primary
            ? CleanupScope.BoxInstalledOnly
            : CleanupScope.Everything;

        int cleanupEpoch = _operations.Epoch;
        IProgress<(int Done, int Total)> progress = CreateOperationProgress(
            cleanupEpoch,
            p => $"正在删除MOD文件... ({p.Done}/{p.Total})");
        CleanupReport? report = null;
        (bool ok, string? error) = await RunOperationAsync("正在删除MOD文件...", token =>
        {
            report = _maintenance.Cleanup(_services.GameSession.Game, _services.GameSession.GameRoot, PakMode, scope, token, progress);
        });

        if (IsCanceled(error) || report?.Canceled == true)
        {
            ReportCanceled(report is null
                ? "清理未完成"
                : $"已安全禁用 {report.Disabled}/{report.Total} 个 MOD，清理未完成");
            return;
        }

        if (!ok)
        {
            await ShowMessageAsync("清理失败", error ?? "未知错误");
            return;
        }

        ViewModel.Status = "已清理游戏目录里的 MOD 文件";
        if (report is not null)
        {
            _services.Logger.WarnBatch([.. report.Warnings.Select(warning => "清理提示：" + warning)]);
        }

        string detail = report?.Warnings.Count > 0
            ? Environment.NewLine + Environment.NewLine
                + DialogTextLimits.JoinTruncated(
                    report.Warnings,
                    DialogTextLimits.WarningLines,
                    Environment.NewLine,
                    unit: "条",
                    logFile: AppServices.LogFile)
            : string.Empty;
        await ShowMessageAsync("清理完成",
            (scope == CleanupScope.Everything
                ? "已安全禁用当前游戏的全部 MOD，并清理了游戏目录里的 MOD 残留。\n"
                : "已安全禁用当前游戏的全部 MOD，并还原了被覆盖的游戏原文件。\n")
            + "建议先通过 Steam 校验一次游戏完整性，再重新启用 MOD。" + detail);
    }

    private void Save_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("存档失败", SaveCoreAsync);

    /// <summary>打开存档管理（必要时先探测/选择存档目录）。</summary>
    private async Task SaveCoreAsync()
    {
        string savePath = _services.SettingsCoordinator.ResolveSavePath(_services.GameSession.Game);
        if (string.IsNullOrWhiteSpace(savePath) || !_services.FileSystem.DirectoryExists(savePath))
        {
            string? detected = _services.GameDiscovery.FindSaveRoot(_services.GameSession.Game);
            if (!string.IsNullOrWhiteSpace(detected) && _services.FileSystem.DirectoryExists(detected))
            {
                savePath = detected;
                _services.SettingsCoordinator.SetSavePath(_services.GameSession.Game.Id, savePath);
            }
            else
            {
                string? picked = await DialogPickers.PickFolderAsync(this);
                RestoreAfterPicker();
                if (string.IsNullOrWhiteSpace(picked))
                {
                    return;
                }

                savePath = picked;
                _services.SettingsCoordinator.SetSavePath(_services.GameSession.Game.Id, savePath);
            }
        }

        SaveManagerDialog dialog = new(
            _services.GameSession.Game,
            savePath,
            _services.CreateSaveBackup(),
            _services.CreateLegacySaveImporter(),
            _services.Logger,
            _services.FileSystem)
        { XamlRoot = Root.XamlRoot };
        await ShowDialogAsync(dialog);
        ViewModel.Status = $"存档管理：{_services.GameSession.Game.DisplayName}（{dialog.Backups.Count} 个备份）";
    }

    private void StartGame_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("启动游戏失败", StartGameCoreAsync);

    /// <summary>通过 Steam 启动当前游戏；成功后最小化窗口。</summary>
    private async Task StartGameCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot) || !GameDirectoryProbe.IsGameDirectory(_services.GameSession.Game, _services.GameSession.GameRoot))
        {
            await ShowMessageAsync("启动游戏",
                string.IsNullOrWhiteSpace(_services.GameSession.GameRoot)
                    ? "请先在「游戏设置」里选择游戏目录。"
                    : $"游戏目录里找不到 {_services.GameSession.Game.ExecutableName}，请在「游戏设置」里重新设置游戏路径。");
            return;
        }

        try
        {
            // 通过 steam://run/<appid> 启动，成功后最小化HunterBox窗口。
            MaintenanceWorkflow.Launch(_services.GameSession.Game);
            (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Minimize();
            ViewModel.Status = "已请求 Steam 启动 " + _services.GameSession.Game.DisplayName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await ShowMessageAsync("启动游戏失败", ex.Message);
        }
    }

    private void Recopy_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("重新复制失败", RecopyCoreAsync);

    /// <summary>重新复制当前游戏全部已启用 MOD（确认 → 后台批量复制 → 汇总）。</summary>
    private async Task RecopyCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            await ShowMessageAsync("提示", "请先设置游戏目录");
            return;
        }

        List<ModRecord> enabled = [.. _services.Store.ModsFor(_services.GameSession.Game.Id).Where(m => m.Enabled)];
        if (enabled.Count == 0)
        {
            await ShowMessageAsync("重新复制MOD", "当前游戏没有已启用的 MOD");
            return;
        }

        ContentDialog confirm = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = "重新复制MOD",
            Content = $"是否重新复制当前游戏的全部 {enabled.Count} 个已启用 MOD 到游戏目录？\n（用于游戏更新后恢复HunterBox文件）",
            PrimaryButtonText = "重新复制",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        int recopyEpoch = _operations.Epoch;
        IProgress<(int Done, int Total)> progress = CreateOperationProgress(
            recopyEpoch,
            p => $"正在重新复制... ({p.Done}/{p.Total})");
        BatchActivationResult? batch = null;
        (bool ok, string? error) = await RunOperationAsync("正在重新复制...", token =>
        {
            batch = _maintenance.Recopy(enabled, _services.GameSession.Game, _services.GameSession.GameRoot, PakMode, token, progress);
        });

        if (IsCanceled(error))
        {
            ReportCanceled(batch is null
                ? "未重新复制任何 MOD"
                : $"已重新复制 {batch.Processed}/{batch.Total} 个 MOD，其余保持原状");
            return;
        }

        if (!ok)
        {
            await ShowMessageAsync("重新复制失败", error ?? "未知错误");
            return;
        }

        if (batch is null)
        {
            return;
        }

        if (batch.Canceled)
        {
            ReportCanceled($"已重新复制 {batch.Processed}/{batch.Total} 个 MOD，其余保持原状");
            return;
        }

        if (batch.Failures.Count > 0)
        {
            _services.Logger.Warn("重新复制失败：" + string.Join("；", batch.Failures));
            ViewModel.Status = "重新复制失败：" + batch.Failures[0];
            await ShowMessageAsync(
                "重新复制失败",
                DialogTextLimits.JoinTruncated(
                    batch.Failures,
                    DialogTextLimits.WarningLines,
                    "\n",
                    unit: "条",
                    logFile: AppServices.LogFile));
            return;
        }

        ViewModel.Status = $"已重新复制 {enabled.Count} 个 MOD";
    }

}
