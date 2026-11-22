using System.Collections.ObjectModel;
using System.Diagnostics;
using HunterBox.Application.Common;
using HunterBox.Application.Saves;
using HunterBox.Domain.Games;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HunterBox.App;

/// <summary>
/// 存档管理对话框：目录备份列表 + 备份当前存档 / 恢复 / 重命名 / 删除 / 打开目录 / 导入旧存档 zip。
/// </summary>
/// <remarks>
/// 对齐的 <c>SaveManagerDialog</c>：名称/时间/大小三列、倒序、自动选中第一项；
/// 恢复/删除前用内联确认面板二次确认（避免 ContentDialog 嵌套），重命名使用内联输入面板。
/// 备份格式为目录复制（<c>Saves/&lt;gameId&gt;/&lt;时间戳&gt;/</c>）， <c>Saves_&lt;appid&gt;/*.zip</c>
/// 通过 <see cref="ILegacySaveImporter"/> 导入为同样的目录备份。
/// 忙碌（备份/恢复/删除/导入写盘）时拦截关闭（Esc/关闭按钮由 <see cref="OnDialogClosing"/> 拦，
/// Alt+F4 关主窗口由主窗口的关闭保护通过 <see cref="IBusyCloseGuard"/> 拦），避免长任务被中断。
/// </remarks>
public sealed partial class SaveManagerDialog : ContentDialog, IBusyCloseGuard
{
    private readonly GameProfile _game;
    private readonly string _saveDirectory;
    private readonly SaveBackupService _service;
    private readonly ILegacySaveImporter _legacyImporter;
    private readonly IAppLogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly ObservableCollection<BackupRow> _rows = [];
    private SaveBackup? _selected;
    private SaveBackup? _renameTarget;
    private int _reloadEpoch;
    private IReadOnlyList<LegacySaveArchive> _legacyArchives = [];
    private Action? _pendingConfirm;
    private bool _busy;

    /// <summary>正在进行的旧存档导入取消源；空闲时为 <see langword="null"/>。</summary>
    private CancellationTokenSource? _importCts;

    /// <summary>创建存档管理对话框。</summary>
    /// <param name="game">当前游戏。</param>
    /// <param name="saveDirectory">游戏存档目录（为空或不存在时禁用备份/恢复并在顶部提示）。</param>
    /// <param name="service">存档备份服务。</param>
    /// <param name="legacyImporter">旧存档 zip 导入器（必填，由组合根装配）。</param>
    /// <param name="logger">日志端口（必填，由组合根装配）。</param>
    /// <param name="fileSystem">文件系统端口（由组合根强制传入，不在 App 层回退到 Infrastructure 实现）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public SaveManagerDialog(
        GameProfile game,
        string saveDirectory,
        SaveBackupService service,
        ILegacySaveImporter legacyImporter,
        IAppLogger logger,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(legacyImporter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileSystem);

        InitializeComponent();
        _game = game;
        _saveDirectory = saveDirectory ?? string.Empty;
        _service = service;
        _legacyImporter = legacyImporter;
        _logger = logger;
        _fileSystem = fileSystem;

        SavesList.ItemsSource = _rows;
        // 组合根保证导入器非空，不再保留不可达的隐藏分支。
        ImportLegacyButton.Visibility = Visibility.Visible;

        string saveText = string.IsNullOrWhiteSpace(_saveDirectory)
            ? "未设置（请先在「游戏设置」里配置存档路径）"
            : _fileSystem.DirectoryExists(_saveDirectory) ? _saveDirectory : _saveDirectory + "（目录不存在）";
        PathText.Text = $"游戏：{_game.DisplayName}\n存档目录：{saveText}\n备份目录：{_service.SavesRoot(_game.Id)}";

        _ = ReloadAsync();
    }

    /// <summary>当前列表里的备份（按时间倒序），关闭对话框后主窗口可用于状态提示。</summary>
    public IReadOnlyList<SaveBackup> Backups => [.. _rows.Select(r => r.Backup)];

    private bool SaveDirectoryReady =>
        !string.IsNullOrWhiteSpace(_saveDirectory) && _fileSystem.DirectoryExists(_saveDirectory);

    private async Task ReloadAsync(string? selectName = null)
    {
        int epoch = ++_reloadEpoch;
        IReadOnlyList<SaveBackup> backups;
        try
        {
            backups = await _service.ListAsync(_game.Id);
        }
        catch (Exception ex)
        {
            if (epoch == _reloadEpoch)
            {
                Report("读取存档备份失败", ex);
            }

            return;
        }

        // 旧刷新晚到：丢弃，避免覆盖更新一代的列表。
        if (epoch != _reloadEpoch)
        {
            return;
        }

        _rows.Clear();
        foreach (SaveBackup backup in backups)
        {
            _rows.Add(new BackupRow(backup));
        }

        EmptyText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int index = 0;
        if (selectName is not null)
        {
            int found = backups.ToList().FindIndex(b => string.Equals(b.Name, selectName, StringComparison.Ordinal));
            if (found >= 0)
            {
                index = found;
            }
        }

        SavesList.SelectedIndex = backups.Count == 0 ? -1 : index;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = !_busy;
        bool hasSelection = _selected is not null;
        // 一次刷新只探测一次存档目录，避免同一轮 UI 更新三次 DirectoryExists。
        bool saveReady = SaveDirectoryReady;
        BackupButton.IsEnabled = idle && saveReady;
        RestoreButton.IsEnabled = idle && hasSelection && saveReady;
        RenameButton.IsEnabled = idle && hasSelection;
        DeleteButton.IsEnabled = idle && hasSelection;
        OpenFolderButton.IsEnabled = idle;
        ImportLegacyButton.IsEnabled = idle;
        ImportConfirmButton.IsEnabled = idle;
        ToolTipService.SetToolTip(
            BackupButton,
            saveReady ? "把当前游戏存档目录整体复制为一个新备份" : "存档目录未设置或不存在，请先在「游戏设置」里配置存档路径");
        ToolTipService.SetPlacement(BackupButton, PlacementMode.Top);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null)
        {
            SetStatus(message);
        }

        UpdateButtons();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>把结构化校验问题映射为状态栏可读文案（最多显示 3 项，其余给总数）。</summary>
    /// <param name="verification">失败的结构化校验结果。</param>
    /// <returns>用户可读文案。</returns>
    private static string BuildVerificationIssueText(SaveBackupVerification verification)
    {
        if (verification.Issues.Count == 0)
        {
            return "备份哈希校验不通过";
        }

        string text = "备份哈希校验不通过：" + string.Join("；", verification.Issues.Take(3).Select(FormatIssue));
        return verification.Issues.Count > 3
            ? text + $"；另有 {verification.Issues.Count - 3} 项"
            : text;
    }

    /// <summary>单条校验问题的中文文案。</summary>
    /// <param name="issue">结构化问题。</param>
    /// <returns>用户可读文案。</returns>
    private static string FormatIssue(SaveBackupIssue issue) => issue.Kind switch
    {
        SaveBackupIssueKind.ManifestUnreadable => "哈希清单读取失败",
        SaveBackupIssueKind.ManifestTooLarge => "哈希清单超过大小上限",
        SaveBackupIssueKind.InvalidPath => "非法路径：" + issue.RelativePath,
        SaveBackupIssueKind.MissingFile => "文件缺失：" + issue.RelativePath,
        SaveBackupIssueKind.SizeMismatch => "文件大小不一致：" + issue.RelativePath,
        SaveBackupIssueKind.HashMismatch => "文件哈希不一致：" + issue.RelativePath,
        SaveBackupIssueKind.UnexpectedFile => "备份中存在清单外文件：" + issue.RelativePath,
        SaveBackupIssueKind.PostCopyMissingFile => "还原后文件缺失：" + issue.RelativePath,
        SaveBackupIssueKind.PostCopyMismatch => "还原后校验不一致：" + issue.RelativePath,
        _ => issue.RelativePath,
    };

    private void Report(string title, Exception exception)
    {
        string message = exception.Message.Length > 160 ? exception.Message[..160] + "…" : exception.Message;
        SetStatus($"{title}：{message}");
        _logger.Error(title, exception);
    }

    private void HidePanels()
    {
        _pendingConfirm = null;
        _renameTarget = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Collapsed;
        ImportPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowConfirm(string message, Action onConfirm)
    {
        HidePanels();
        _pendingConfirm = onConfirm;
        ConfirmText.Text = message;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!SaveDirectoryReady)
        {
            SetStatus("存档目录未设置或不存在，请先在「游戏设置」里配置存档路径");
            return;
        }

        string saveDirectory = _saveDirectory;
        HidePanels();
        SetBusy(true, "正在备份当前存档...");
        try
        {
            SaveBackup created = await Task.Run(() => _service.Backup(_game.Id, saveDirectory));
            _logger.Info($"存档备份完成：{_game.Id}/{created.Name} ← {saveDirectory}（{created.SizeBytes} 字节）");
            SetStatus($"备份完成：{created.Name}（{SaveBackupService.FormatSize(created.SizeBytes)}）");
            await ReloadAsync(created.Name);
        }
        catch (Exception ex)
        {
            Report("备份存档失败", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected is not SaveBackup backup)
        {
            return;
        }

        if (!SaveDirectoryReady)
        {
            SetStatus("存档目录未设置或不存在，无法恢复");
            return;
        }

        ShowConfirm(
            $"确定用「{backup.Name}」覆盖当前存档？\n\n当前存档目录：{_saveDirectory}\n"
            + "同名存档文件会被覆盖（备份里没有的文件会保留），此操作不可撤销。",
            () => _ = RestoreAsync(backup));
    }

    private async Task RestoreAsync(SaveBackup backup)
    {
        SetBusy(true, "正在校验并恢复存档...");
        try
        {
            string saveDirectory = _saveDirectory;
            // P-12：还原前的 Verify 与 Restore 内的源校验重复一次全量哈希；Restore 返回它实际用的校验结果，
            // 界面直接复用（有清单时失败会抛 SaveBackupVerificationException），不再额外 Verify 一遍。
            SaveBackupVerification verification = await Task.Run(
                () => _service.Restore(backup, saveDirectory));
            _logger.Info($"存档恢复完成：{_game.Id}/{backup.Name} → {saveDirectory}"
                + (verification.HasManifest ? "（备份源哈希校验通过）" : "（该备份无哈希清单，未校验）"));
            SetStatus(verification.HasManifest
                ? $"已恢复存档：{backup.Name}（备份源哈希校验通过，{verification.FileCount} 个文件）"
                : $"已恢复存档：{backup.Name}（旧备份无哈希清单，未校验）");
        }
        catch (SaveBackupVerificationException ex)
        {
            // A-16/Application 只返回结构化问题；已回滚时必须明确告诉用户当前存档是安全的旧版本。
            string prefix = ex.RolledBack ? "恢复失败，已回滚到还原前的存档：" : "恢复失败：";
            SetStatus(prefix + BuildVerificationIssueText(ex.Verification));
            _logger.Error("存档备份校验失败", ex);
        }
        catch (InvalidDataException ex)
        {
            SetStatus("恢复失败：备份哈希校验不通过");
            Report("存档备份校验失败", ex);
        }
        catch (Exception ex)
        {
            Report("恢复存档失败", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected is not SaveBackup backup)
        {
            return;
        }

        HidePanels();
        _renameTarget = backup;
        RenameBox.Text = backup.Name;
        RenamePanel.Visibility = Visibility.Visible;
        RenameBox.Focus(FocusState.Programmatic);
    }

    private void RenameOk_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _renameTarget is not SaveBackup target)
        {
            return;
        }

        string name = RenameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            SetStatus("名称不能为空");
            return;
        }

        try
        {
            SaveBackup renamed = _service.Rename(target, name);
            _logger.Info($"存档备份重命名：{_game.Id}/{target.Name} → {renamed.Name}");
            RenamePanel.Visibility = Visibility.Collapsed;
            _renameTarget = null;
            SetStatus($"已重命名为：{renamed.Name}");
            _ = ReloadAsync(renamed.Name);
        }
        catch (ArgumentException ex)
        {
            SetStatus("名称非法：" + ex.Message);
        }
        catch (IOException ex)
        {
            Report("重命名失败", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Report("重命名失败", ex);
        }
    }

    private void RenameCancel_Click(object sender, RoutedEventArgs e)
    {
        _renameTarget = null;
        RenamePanel.Visibility = Visibility.Collapsed;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            RenameOk_Click(sender, new RoutedEventArgs());
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            RenameCancel_Click(sender, new RoutedEventArgs());
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected is not SaveBackup backup)
        {
            return;
        }

        ShowConfirm(
            $"确定删除「{backup.Name}」？\n\n将递归删除备份目录：{backup.Directory}\n此操作不可恢复。",
            () => _ = DeleteAsync(backup));
    }

    private async Task DeleteAsync(SaveBackup backup)
    {
        SetBusy(true, "正在删除备份...");
        try
        {
            await Task.Run(() => _service.Delete(backup));
            _logger.Info($"存档备份已删除：{_game.Id}/{backup.Name}（{backup.Directory}）");
            SetStatus($"已删除：{backup.Name}");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Report("删除备份失败", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e)
    {
        Action? action = _pendingConfirm;
        _pendingConfirm = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    private void ConfirmNo_Click(object sender, RoutedEventArgs e)
    {
        _pendingConfirm = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = _service.SavesRoot(_game.Id);
            _fileSystem.CreateDirectory(root);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{root}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Report("打开备份目录失败", ex);
        }
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        HidePanels();
        SetBusy(true, "正在读取旧存档...");
        try
        {
            // 目录枚举与文件元数据放到后台线程，避免旧备份很多时阻塞 UI。
            ILegacySaveImporter importer = _legacyImporter;
            GameProfile game = _game;
            string directory = importer.LegacySaveDirectory(game);
            IReadOnlyList<LegacySaveArchive> archives = await Task.Run(() => importer.List(game));
            if (archives.Count == 0)
            {
                SetStatus($"未找到旧存档 zip：{directory}");
                return;
            }

            _legacyArchives = archives;
            LegacyHintText.Text =
                $"旧备份目录：{directory}\n找到 {archives.Count} 个 zip；导入后生成新的目录备份（只读旧文件、不删除）。";
            LegacyCombo.ItemsSource = archives
                .Select(a => $"{a.FileName}    {a.LastWriteTime.ToLocalTime():yyyy-MM-dd HH:mm}    {SaveBackupService.FormatSize(a.SizeBytes)}")
                .ToList();
            LegacyCombo.SelectedIndex = 0;
            ImportPanel.Visibility = Visibility.Visible;
            SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            Report("读取旧存档失败", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportConfirm_Click(object sender, RoutedEventArgs e)
    {
        int index = LegacyCombo.SelectedIndex;
        if (_busy || index < 0 || index >= _legacyArchives.Count)
        {
            return;
        }

        LegacySaveArchive archive = _legacyArchives[index];
        ILegacySaveImporter importer = _legacyImporter;
        using CancellationTokenSource cts = new();
        _importCts = cts;
        SetBusy(true, "正在导入旧存档...");
        try
        {
            SaveBackup imported = await Task.Run(() => importer.Import(_game, archive, cts.Token), cts.Token);
            ImportPanel.Visibility = Visibility.Collapsed;
            SetStatus($"已导入旧存档：{archive.FileName} → {imported.Name}");
            await ReloadAsync(imported.Name);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消导入旧存档");
        }
        catch (Exception ex)
        {
            Report("导入旧存档失败", ex);
        }
        finally
        {
            _importCts = null;
            SetBusy(false);
        }
    }

    private void ImportCancel_Click(object sender, RoutedEventArgs e)
    {
        // 导入进行中时请求取消：导入在条目/分块边界停止并清理半成品备份。
        _importCts?.Cancel();
        ImportPanel.Visibility = Visibility.Collapsed;
        SetStatus(string.Empty);
    }

    private void SavesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = (SavesList.SelectedItem as BackupRow)?.Backup;
        UpdateButtons();
    }

    /// <summary>是否有写盘类长任务在跑（备份/恢复/删除/导入）；主窗口关闭保护会查询它。</summary>
    bool IBusyCloseGuard.IsBusy => _busy;

    /// <summary>主窗口尝试关闭被忙碌状态拦下时，在对话框内给出可见提示。</summary>
    void IBusyCloseGuard.OnCloseBlocked() => SetStatus("有操作正在进行，完成后再关闭");

    /// <summary>
    /// ContentDialog 关闭拦截：忙碌时取消这次关闭（Esc / 「关闭」按钮 / Alt+F4 最终落到对话框时都会经过这里），
    /// 让备份/恢复/删除/导入等写盘任务完整跑完，不产生半新半旧的存档目录。
    /// </summary>
    /// <param name="sender">对话框。</param>
    /// <param name="args">关闭事件参数；置 <see cref="ContentDialogClosingEventArgs.Cancel"/> 可取消关闭。</param>
    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_busy)
        {
            return;
        }

        args.Cancel = true;
        SetStatus("有操作正在进行，完成后再关闭");
    }

    /// <summary>列表行：把 <see cref="SaveBackup"/> 转成三列显示用的文本。</summary>
    internal sealed class BackupRow
    {
        public BackupRow(SaveBackup backup)
        {
            Backup = backup;
            Name = backup.Name;
            TimeText = backup.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            SizeText = SaveBackupService.FormatSize(backup.SizeBytes);
        }

        public SaveBackup Backup { get; }

        public string Name { get; }

        public string TimeText { get; }

        public string SizeText { get; }
    }
}
