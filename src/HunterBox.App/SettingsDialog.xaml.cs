using HunterBox.Application.Common;
using HunterBox.Infrastructure.Games;
using HunterBox.Application.Install;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;

using HunterBox.Infrastructure.Logging;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>
/// 「设置」对话框：编辑游戏路径、存档路径、安装选项、pak 校验与界面主题，并统一保存。
/// </summary>
/// <remarks>
/// <para>
/// 「忽略踩蘑菇」「过滤怪猎模型」「file.list」已按 决策移除（有意精简 / 内嵌清单已可用）。
/// 保存统一走「保存设置」主按钮：只有全部校验通过才写 INI，非法路径会给提示并保留磁盘上的原值。
/// </para>
/// <para>
/// WinUI 不允许同时打开两个 <see cref="ContentDialog"/>，所以关键项变化的确认不用嵌套对话框，
/// 而是在本对话框内显示风险说明并把按钮改成「仍然保存」，再点一次才真正保存。
/// </para>
/// </remarks>
public sealed partial class SettingsDialog : ContentDialog
{
    /// <summary>保存按钮的默认文案。</summary>
    private const string SaveButtonText = "保存设置";

    /// <summary>存在风险设置时的二次确认文案。</summary>
    private const string ConfirmRiskButtonText = "仍然保存";

    private readonly SettingsCoordinator _coordinator;
    private readonly HunterBox.Application.Common.IFileSystem _fileSystem;
    private readonly GameDiscoveryService _discovery;
    private readonly GameProfile _game;
    private readonly Window? _owner;
    private readonly GameSettingsSnapshot _initial;
    private readonly ThemePreference _initialTheme;
    private readonly IAppLogger _logger;
    private bool _saved;
    private bool _riskConfirmed;

    /// <summary>按当前配置初始化对话框。</summary>
    /// <param name="coordinator">设置读写协调器（键名、默认值与批量保存都在 Application）。</param>
    /// <param name="fileSystem">文件系统端口（路径校验）。</param>
    /// <param name="discovery">游戏路径发现用例（缺失路径探测）。</param>
    /// <param name="gamePathText">当前游戏目录（用于 INI 尚未写入时的回退展示）。</param>
    /// <param name="game">当前游戏；为 <see langword="null"/> 时从配置里的 <c>[settings] game</c> 解析。</param>
    /// <param name="owner">打开对话框的主窗口；用于给目录选择器提供窗口句柄（不传则从 XamlRoot 推断）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="coordinator"/> 或 <paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public SettingsDialog(
        SettingsCoordinator coordinator,
        HunterBox.Application.Common.IFileSystem fileSystem,
        GameDiscoveryService discovery,
        string gamePathText,
        GameProfile? game = null,
        Window? owner = null,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(discovery);

        InitializeComponent();
        _coordinator = coordinator;
        _fileSystem = fileSystem;
        _discovery = discovery;
        _game = game ?? coordinator.ResolveCurrentGame();
        _owner = owner;
        _initial = coordinator.Read(_game);
        _initialTheme = AppTheme.Parse(_initial.Theme);
        _logger = logger ?? AppLogAdapter.Instance;

        Populate(gamePathText);
        Loaded += OnLoadedDetectPaths;
    }

    /// <summary>对话框加载后补齐缺失的路径：探测放到后台线程，避免构造期卡 UI。</summary>
    private async void OnLoadedDetectPaths(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedDetectPaths;
        await DetectMissingPathsAsync();
    }

    /// <summary>用户手动「重新识别」：只失效缺失项的缓存后重新探测。</summary>
    private async void RedetectPaths_Click(object sender, RoutedEventArgs e)
    {
        _discovery.Invalidate(
            _game.Id,
            gameRoot: string.IsNullOrWhiteSpace(GamePathBox.Text),
            saveRoots: string.IsNullOrWhiteSpace(SavePathBox.Text));
        await DetectMissingPathsAsync();
    }

    /// <summary>探测并补齐为空的游戏/存档路径；已填写的值保持不变。</summary>
    private async Task DetectMissingPathsAsync()
    {
        bool needGame = string.IsNullOrWhiteSpace(GamePathBox.Text);
        bool needSave = string.IsNullOrWhiteSpace(SavePathBox.Text);
        if (!needGame && !needSave)
        {
            StatusText.Text = "游戏目录与存档路径都已设置。";
            return;
        }

        try
        {
            (string? game, string? save) = await Task.Run(() => (
                needGame ? _discovery.FindGameRoot(_game) : null,
                needSave ? _discovery.FindSaveRoot(_game) : null));
            if (needGame && !string.IsNullOrWhiteSpace(game) && string.IsNullOrWhiteSpace(GamePathBox.Text))
            {
                GamePathBox.Text = game;
                StatusText.Text = "已自动识别游戏目录，点击「保存设置」后生效。";
            }

            if (needSave && !string.IsNullOrWhiteSpace(save) && string.IsNullOrWhiteSpace(SavePathBox.Text))
            {
                SavePathBox.Text = save;
                if (StatusText.Text.Length == 0)
                {
                    StatusText.Text = "已自动识别存档目录，点击「保存设置」后生效。";
                }
            }
        }
        catch (Exception ex)
        {
            // async void 探测兜底：自动识别失败只降级为手动填写，绝不冒到全局崩溃。
            _logger.Warn("自动探测游戏/存档路径失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把当前界面值写回配置（主按钮已保存时为空操作）。
    /// 保留此方法是为了兼容 MainWindow 现有的「Primary 后调用 Save」接线。
    /// </summary>
    public void Save()
    {
        if (_saved)
        {
            return;
        }

        if (_coordinator.IsReadOnly)
        {
            return;
        }

        SaveCore(GamePathBox.Text.Trim(), SavePathBox.Text.Trim());
        _saved = true;
    }

    private void Populate(string gamePathText)
    {
        GameText.Text = "当前游戏：" + _game.DisplayName;

        string gamePath = _initial.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) && !string.IsNullOrWhiteSpace(gamePathText))
        {
            gamePath = gamePathText.Trim();
        }

        // 缺省路径由 OnLoadedDetectPaths 在后台探测补齐，构造期不做注册表/Steam VDF 读取。
        string savePath = _initial.SavePath;

        GamePathBox.Text = gamePath;
        SavePathBox.Text = savePath;

        ThemeCombo.SelectedIndex = _initialTheme switch
        {
            ThemePreference.Light => 1,
            ThemePreference.Dark => 2,
            _ => 0,
        };

        if (_initial.InstallOption == InstallSourceMode.Copy)
        {
            OptionCopy.IsChecked = true;
        }
        else if (_initial.InstallOption == InstallSourceMode.Move)
        {
            OptionMove.IsChecked = true;
        }
        else
        {
            OptionNone.IsChecked = true;
        }

        FixPakBox.IsChecked = _initial.FixPakNumber;
        CheckPakFilesBox.IsChecked = _initial.CheckPakFiles;
        CheckGameRunningBox.IsChecked = _initial.CheckGameRunning;

        // 已有配置里的路径失效时直接提示（对齐 CheckGamePath 精神，不自动清空）。
        if (!string.IsNullOrWhiteSpace(_initial.GamePath))
        {
            if (!_fileSystem.DirectoryExists(_initial.GamePath))
            {
                StatusText.Text = "提示：当前游戏目录不存在，请重新选择。";
            }
            else if (!GameDirectoryProbe.IsGameDirectory(_game, _initial.GamePath))
            {
                StatusText.Text = $"提示：游戏目录里找不到 {_game.ExecutableName}，请重新选择。";
            }
        }
    }

    private async void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await DialogPickers.PickFolderAsync(ResolveWindowHandle());
            _owner?.Activate();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!_fileSystem.DirectoryExists(folder))
            {
                ShowPathError("游戏目录不存在：" + folder + "\n当前路径保持不变，请重新选择。");
                return;
            }

            if (!GameDirectoryProbe.IsGameDirectory(_game, folder))
            {
                ShowPathError($"所选目录里找不到 {_game.ExecutableName}。\n当前路径保持不变，请选择游戏安装根目录。");
                return;
            }

            GamePathBox.Text = folder;
            StatusText.Text = "已选择游戏目录：" + folder;
            ResetRiskPrompt();
        }
        catch (Exception ex)
        {
            // 选择器（FolderPicker COM）异常不能冒到全局：状态栏 + 日志提示，对话框保持可用。
            _logger.Error("选择游戏目录失败", ex);
            StatusText.Text = "选择游戏目录失败：" + ex.Message;
        }
    }

    private async void BrowseSavePath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await DialogPickers.PickFolderAsync(ResolveWindowHandle());
            _owner?.Activate();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!_fileSystem.DirectoryExists(folder))
            {
                ShowPathError("存档目录不存在：" + folder + "\n当前路径保持不变，请重新选择。");
                return;
            }

            SavePathBox.Text = folder;
            StatusText.Text = "已选择存档目录：" + folder;
            ResetRiskPrompt();
        }
        catch (Exception ex)
        {
            // 同「选择游戏目录」：选择器异常只在对话框内提示，不让进程崩溃。
            _logger.Error("选择存档目录失败", ex);
            StatusText.Text = "选择存档目录失败：" + ex.Message;
        }
    }

    private nint ResolveWindowHandle()
    {
        if (_owner is not null)
        {
            return DialogPickers.WindowHandleOf(_owner);
        }

        try
        {
            if (XamlRoot?.ContentIslandEnvironment is { } environment)
            {
                return Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            // 拿不到当前窗口句柄时由调用方按「用户取消」处理。
        }

        return 0;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string gamePath = GamePathBox.Text.Trim();
        string savePath = SavePathBox.Text.Trim();
        if (!TryValidatePaths(gamePath, savePath, out string pathError))
        {
            args.Cancel = true;
            StatusText.Text = pathError;
            ShowPathError(pathError);
            return;
        }

        List<string> risks = BuildRiskNotes();
        if (risks.Count > 0 && !_riskConfirmed)
        {
            RiskText.Text = string.Join("\n", risks)
                + $"\n\n确认继续请再次点击「{ConfirmRiskButtonText}」。";
            RiskText.Visibility = Visibility.Visible;
            PrimaryButtonText = ConfirmRiskButtonText;
            _riskConfirmed = true;
            args.Cancel = true;
            return;
        }

        if (_coordinator.IsReadOnly)
        {
            args.Cancel = true;
            StatusText.Text = "配置文件为只读，本次修改不会保存：" + (_coordinator.LoadWarning ?? "请检查文件权限后重启。");
            return;
        }

        try
        {
            SaveCore(gamePath, savePath);
            _saved = true;
            RiskText.Visibility = Visibility.Collapsed;
            StatusText.Text = "已保存";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            args.Cancel = true;
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private bool TryValidatePaths(string gamePath, string savePath, out string error)
    {
        error = string.Empty;
        if (gamePath.Length > 0)
        {
            if (!_fileSystem.DirectoryExists(gamePath))
            {
                error = "游戏目录不存在：\n" + gamePath;
                return false;
            }

            if (!GameDirectoryProbe.IsGameDirectory(_game, gamePath))
            {
                error = $"游戏目录里找不到 {_game.ExecutableName}，请选择游戏安装根目录。";
                return false;
            }
        }

        if (savePath.Length > 0 && !_fileSystem.DirectoryExists(savePath))
        {
            error = "存档目录不存在：\n" + savePath;
            return false;
        }

        return true;
    }

    /// <summary>显示对话框内的错误提示浮层（ContentDialog 内不能再嵌套 ContentDialog）。</summary>
    private void ShowPathError(string message)
    {
        ErrorText.Text = message;
        ErrorOverlay.Visibility = Visibility.Visible;
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) => ErrorOverlay.Visibility = Visibility.Collapsed;

    private List<string> BuildRiskNotes()
    {
        List<string> notes = [];
        bool fixPak = FixPakBox.IsChecked == true;
        bool checkGameRunning = CheckGameRunningBox.IsChecked == true;
        if (fixPak != _initial.FixPakNumber)
        {
            notes.Add(fixPak
                ? "已开启「自动修正PAK」：正在启用中的 MOD 若编号方式改变，可能需要重新复制MOD 才能完全生效。"
                : "已关闭「自动修正PAK」：pak 会按原文件名放进 pak_mods，多个 MOD 同名时容易残留或互相覆盖。");
        }

        if (_initial.CheckGameRunning && !checkGameRunning)
        {
            notes.Add("已关闭「检测游戏状态」：游戏运行时启用/禁用/清理 MOD 可能残留部分文件，建议先退出游戏再操作。");
        }

        return notes;
    }

    private void ResetRiskPrompt()
    {
        _riskConfirmed = false;
        RiskText.Visibility = Visibility.Collapsed;
        if (PrimaryButtonText == ConfirmRiskButtonText)
        {
            PrimaryButtonText = SaveButtonText;
        }
    }

    private void CriticalSetting_Changed(object sender, RoutedEventArgs e) => ResetRiskPrompt();

    private void SaveCore(string gamePath, string savePath)
    {
        // 键名、默认值与批量落盘由 Application 的 SettingsCoordinator 统一处理。
        _coordinator.Save(
            _game,
            new GameSettingsSnapshot(
                gamePath,
                savePath,
                SelectedInstallOption(),
                FixPakBox.IsChecked == true,
                CheckPakFilesBox.IsChecked == true,
                CheckGameRunningBox.IsChecked == true,
                AppTheme.ToIni(SelectedTheme())));
    }

    /// <summary>读取下拉选择的外观主题（未知值按「跟随系统」处理）。</summary>
    private ThemePreference SelectedTheme()
    {
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            return AppTheme.Parse(tag);
        }

        return ThemePreference.System;
    }

    private InstallSourceMode SelectedInstallOption()
    {
        if (OptionCopy.IsChecked == true)
        {
            return InstallSourceMode.Copy;
        }

        return OptionMove.IsChecked == true ? InstallSourceMode.Move : InstallSourceMode.None;
    }
}
