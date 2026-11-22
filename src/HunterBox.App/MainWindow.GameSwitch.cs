using HunterBox.Domain.Games;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>MainWindow 的游戏切换：初始选择、写设置、刷新列表与标题。</summary>
public sealed partial class MainWindow
{

    private void SelectInitialGame()
    {
        // 初始游戏由会话统一解析（回退规则只在 GameSession/SettingsCoordinator 内）。
        int index = Math.Max(0, GameCatalog.All.ToList().FindIndex(g => g.Id == _services.GameSession.Game.Id));
        GameCombo.SelectedIndex = index;
    }

    private async void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = GameCombo.SelectedIndex;
        if (index < 0 || index >= GameCatalog.All.Count)
        {
            return;
        }

        GameProfile previousGame = _services.GameSession.Game;
        GameProfile selectedGame = GameCatalog.All[index];
        // 启动流程可能在本次切换的某个 await 期间结束并复位 _suppressGameReload；
        // 进入时捕获一次，保证「启动期切换不刷新」的语义不会被晚到的续体改写。
        bool suppressReload = _suppressGameReload;
        _services.GameSession.Select(selectedGame);

        // 会话已切到新游戏：立即清空旧游戏的行与选中，避免异步探测期间
        // 用旧行构造新游戏服务或让按钮保持可用。
        _list.Rebuilding = true;
        try
        {
            ModsList.SelectedItems.Clear();
            _list.Rows.Clear();
            _list.AllRows.Clear();
        }
        finally
        {
            _list.Rebuilding = false;
        }

        ViewModel.SetSelectedIds([]);
        ViewModel.CountText = "0 个";
        UpdateActionButtons();

        try
        {
            if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
            {
                // 首次切到未配置游戏时注册表/Steam VDF 探测较慢：放到后台，避免阻塞 UI（P-24）。
                // 探测与写回统一走启动期同一用例：EnsureDetected 只填缺失路径并自行记录日志。
                await Task.Run(() => _services.GameDiscovery.EnsureDetected(selectedGame));
                if (!ReferenceEquals(_services.GameSession.Game, selectedGame))
                {
                    // 等待期间用户又切换了游戏：本次结果作废，交给后一次选择处理。
                    return;
                }

                _services.GameSession.ReloadRoot();
            }

            ViewModel.GamePathText = string.IsNullOrWhiteSpace(_services.GameSession.GameRoot) ? "未设置游戏目录" : _services.GameSession.GameRoot;
            UpdateWindowTitle();
            ViewModel.SetSelectedIds([]);

            // 启动流程由 OnRootLoaded 在数据准备完后统一刷新，避免双重 ReloadMods。
            if (!suppressReload)
            {
                ReloadMods();
            }

            UpdateActionButtons();
        }
        catch (Exception ex)
        {
            // 刷新失败必须回滚选择，否则「组合框=新游戏、列表=旧游戏」，右键操作会作用到错误记录。
            _services.Logger.Error("切换游戏后刷新失败", ex);
            _services.GameSession.Select(previousGame);

            // 回滚选择时临时抑制刷新；若外层启动流程本来就在抑制，恢复外层值而不是无条件 false。
            bool previousSuppress = _suppressGameReload;
            _suppressGameReload = true;
            try
            {
                GameCombo.SelectedIndex = Math.Max(0, GameCatalog.All.ToList().FindIndex(g => g.Id == previousGame.Id));
            }
            finally
            {
                _suppressGameReload = previousSuppress;
            }

            _services.GameSession.ReloadRoot();
            ViewModel.GamePathText = string.IsNullOrWhiteSpace(_services.GameSession.GameRoot) ? "未设置游戏目录" : _services.GameSession.GameRoot;
            UpdateWindowTitle();
            ViewModel.Status = "切换游戏失败：" + ex.Message;

            // 切换开始时已清空列表：回滚后按旧游戏重建，否则界面为空且按钮长期禁用。
            if (!suppressReload)
            {
                try
                {
                    ReloadMods(updateStatus: false);
                }
                catch (Exception reloadError)
                {
                    _services.Logger.Error("回滚切换游戏后重建列表失败", reloadError);
                }
            }

            UpdateActionButtons();
        }

        // 刷新成功后才记住本次游戏；刷新失败时 INI 仍保留旧游戏，避免下次启动进入被回滚的游戏。
        try
        {
            _services.SettingsCoordinator.SetCurrentGame(_services.GameSession.Game.Id);
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("保存上次游戏失败：" + ex.Message);
            ViewModel.Status = "提示：无法写入配置文件（" + ex.Message + "），本次切换仍然生效。";
        }
    }

}
