using System.Diagnostics.CodeAnalysis;
using HunterBox.Application.Common;
using HunterBox.Domain.Games;
using HunterBox.Infrastructure.Logging;
using HunterBox.Infrastructure.Migration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>「导入数据」对话框：展示检测到的第三方HunterBox数据并执行只读复制导入。</summary>
/// <remarks>
/// 旧数据来源：<c>Mods_&lt;appid&gt;</c>（含 info.xml）、<c>sjmodbox.ini</c>、<c>Backup_&lt;appid&gt;</c>、
/// <c>Saves_&lt;appid&gt;</c>（存档由存档模块导入；本对话框只导入 MOD 与原文件备份）。
/// 导入为只读复制，不移动/删除旧文件；旧启用状态不沿用（新HunterBox要求文件实际落盘才为启用）。
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "导入结束路径已在 finally 中释放 CancellationTokenSource；未导入时字段为 null。")]
public sealed partial class LegacyMigrationDialog : ContentDialog, IBusyCloseGuard
{
    private readonly LegacyMigrationService _service;
    private readonly LegacyInventory _inventory;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _importCts;
    private bool _importing;

    /// <summary>导入结果；对话框取消或没有导入时为 <see langword="null"/>。</summary>
    public LegacyImportResult? Result { get; private set; }

    /// <inheritdoc />
    bool IBusyCloseGuard.IsBusy => _importing;

    /// <inheritdoc />
    void IBusyCloseGuard.OnCloseBlocked()
    {
        if (!_importing)
        {
            return;
        }

        // 主窗口 Alt+F4 被拦下：同时请求取消导入，让用户知道需要等导入线程退出。
        _importCts?.Cancel();
        StatusText.Text = "正在导入旧数据：已请求取消，导入结束前不会关闭窗口";
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>用已检测到的旧数据构建对话框。</summary>
    /// <param name="service">已装配的旧数据迁移服务（由调用方组合根创建）。</param>
    /// <param name="inventory">启动阶段已算出的检测结果；对话框不再重复扫描旧库。</param>
    /// <param name="games">当前支持的游戏（用于显示名）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    internal LegacyMigrationDialog(
        LegacyMigrationService service,
        LegacyInventory inventory,
        IReadOnlyList<GameProfile> games,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(games);

        InitializeComponent();
        _service = service;
        _inventory = inventory;
        _logger = logger ?? AppLogAdapter.Instance;

        List<string> lines = [];
        foreach (LegacyGameInventory game in _inventory.Games)
        {
            string displayName = games.FirstOrDefault(g => g.Id == game.GameId)?.DisplayName ?? game.GameId;
            lines.Add($"· {displayName}：{game.ModCount} 个旧 MOD 记录"
                + (game.HasBackup ? "（含原文件备份）" : string.Empty));
        }

        if (_inventory.LegacyIniPath is not null)
        {
            lines.Add("· 旧配置文件 sjmodbox.ini（游戏/存档路径等会自动迁移）");
        }

        if (_inventory.LegacySaveDirectories.Count > 0)
        {
            lines.Add($"· {_inventory.LegacySaveDirectories.Count} 个旧存档备份目录（可在「存档管理→导入旧存档 zip」中导入）");
        }

        if (_inventory.LegacyDownloadDirectory is not null)
        {
            lines.Add("· 旧安装包归档目录 Download（不会自动复制，可手动迁移）");
        }

        if (_inventory.UnreadablePaths.Count > 0)
        {
            // 只有不可读目录时也必须提示「可能不完整」。
            lines.Add($"· 注意：{_inventory.UnreadablePaths.Count} 个旧路径不可读，检测结果可能不完整");
        }

        SummaryText.Text = lines.Count == 0
            ? "没有检测到旧HunterBox的数据（Mods_<appid> / sjmodbox.ini / Backup_<appid>）。"
            : string.Join(Environment.NewLine, lines);
        IsPrimaryButtonEnabled = _inventory.Games.Count > 0;
        EmptyHintText.Visibility = _inventory.HasAnything ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <remarks>
    /// 导入会把旧 <c>Mods_&lt;appid&gt;</c> / <c>Backup_&lt;appid&gt;</c> 整目录复制进新HunterBox（可能数百 MB~数 GB），
    /// 因此必须离开 UI 线程执行：用 <see cref="ContentDialogButtonClickEventArgs.GetDeferral"/> 让对话框
    /// 在后台导入期间保持打开并显示进度文案，避免「未响应」。
    /// 取消：<c>Import</c> 已支持 <see cref="CancellationToken"/>（每个 MOD/备份之间检查），
    /// 取消时已完成的记录保留；导入期间禁用主按钮防止重复导入。
    /// </remarks>
    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            IsPrimaryButtonEnabled = false;
            _importing = true;
            _importCts = new CancellationTokenSource();
            CancelImportButton.Visibility = Visibility.Visible;
            CancelImportButton.IsEnabled = true;
            StatusText.Text = "正在导入旧数据，请稍候…";
            StatusText.Visibility = Visibility.Visible;
            CancellationToken token = _importCts.Token;
            IProgress<(int Done, int Total)> progress = new Progress<(int Done, int Total)>(p =>
            {
                StatusText.Text = p.Total > 0
                    ? $"正在导入旧数据... {p.Done * 100 / p.Total}%（{p.Done}/{p.Total}）"
                    : "正在导入旧数据，请稍候…";
            });

            // 复制/读写在后台线程执行；取消/异常带回 UI 线程再更新对话框。
            (LegacyImportResult? imported, string? error, bool canceled) = await Task.Run(() =>
            {
                try
                {
                    return (_service.Import(_inventory, token, progress), (string?)null, false);
                }
                catch (OperationCanceledException)
                {
                    return ((LegacyImportResult?)null, (string?)null, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    return ((LegacyImportResult?)null, ex.Message, false);
                }
            });

            if (canceled)
            {
                args.Cancel = true;
                StatusText.Text = "导入已取消；已完成的 MOD 记录会保留。";
                return;
            }

            if (imported is null)
            {
                args.Cancel = true;
                StatusText.Text = "导入失败：" + (error ?? "未知错误");
                return;
            }

            if (imported.ImportedMods == 0 && imported.SkippedMods == 0)
            {
                args.Cancel = true;
                StatusText.Text = "没有可导入的 MOD 数据。";
                return;
            }

            // 只有真正导入成功才允许关闭对话框（否则由 MainWindow 拿不到 Result）。
            Result = imported;
            // 结果弹窗只展示前 N 条提示，先把全部提示批量写入日志。
            _logger.WarnBatch([.. imported.Warnings.Select(warning => "旧数据导入提示：" + warning)]);
        }
        catch (Exception ex)
        {
            // async void 兜底：任何意外都不能让对话框无声关闭或让进程崩溃。
            args.Cancel = true;
            StatusText.Text = "导入失败：" + ex.Message;
            StatusText.Visibility = Visibility.Visible;
            _logger.Error("导入旧数据异常", ex);
        }
        finally
        {
            _importing = false;
            _importCts?.Dispose();
            _importCts = null;
            CancelImportButton.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = _inventory.Games.Count > 0;
            deferral.Complete();
        }
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _importCts?.Cancel();
        CancelImportButton.IsEnabled = false;
        StatusText.Text = "正在取消导入…";
    }

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_importing)
        {
            return;
        }

        // 导入期间不允许关闭对话框：先请求取消，等导入线程退出后再允许关闭。
        args.Cancel = true;
        _importCts?.Cancel();
        StatusText.Text = "正在取消导入…";
    }

    /// <summary>把导入结果格式化成用户可读的总结。</summary>
    /// <param name="result">导入结果。</param>
    /// <returns>多行总结。</returns>
    public static string Describe(LegacyImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<string> lines =
        [
            $"已导入 {result.ImportedMods} 个 MOD（跳过 {result.SkippedMods} 个），迁移 {result.ImportedBackupFiles} 个原文件备份。",
            "旧启用状态不沿用，导入后全部为未启用；请按需重新启用。",
        ];
        if (result.Warnings.Count > 0)
        {
            lines.Add($"另有 {result.Warnings.Count} 条提示：");
            // 先截断再加「· 」前缀，日志已在导入完成时写全。
            lines.Add(DialogTextLimits.JoinTruncated(
                result.Warnings,
                DialogTextLimits.DetailLines,
                Environment.NewLine,
                unit: "条",
                logFile: AppServices.LogFile,
                format: warning => "· " + warning));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
