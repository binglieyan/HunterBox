using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>MainWindow 的设置入口。</summary>
public sealed partial class MainWindow
{

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        RunUiAction("设置失败", SettingsCoreAsync);

    private async Task SettingsCoreAsync()
    {
        // 旧 sjmodbox.ini 已在 App 启动时迁移；对话框直接读取迁移后的设置。
        SettingsDialog dialog = new(_services.SettingsCoordinator, _services.FileSystem, _services.GameDiscovery, _services.GameSession.GameRoot, _services.GameSession.Game, this, _services.Logger)
        {
            XamlRoot = Root.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        dialog.Save();

        // 主题保存后立即生效（改窗口根元素 RequestedTheme，ContentDialog 已关闭，无需重启）。
        _services.ApplyTheme(Root, AppTheme.Read(_services.Settings));
        ApplyTitleBarTheme();

        // 设置里可能改了游戏路径/存档路径：同步到主窗口并刷新列表。
        _services.GameSession.ReloadRoot();
        ViewModel.GamePathText = string.IsNullOrWhiteSpace(_services.GameSession.GameRoot) ? "未设置游戏目录" : _services.GameSession.GameRoot;
        ReloadMods(updateStatus: false);
        UpdateActionButtons();
        ViewModel.Status = "设置已保存";
    }

}
