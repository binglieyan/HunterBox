namespace HunterBox.App;

/// <summary>MainWindow 对安装后刷新协调器暴露的最小控件操作面（A-06）。</summary>
public sealed partial class MainWindow : IInstallRefreshTarget
{
    void IInstallRefreshTarget.ReloadModsForRefresh() => ReloadMods(updateStatus: false);

    void IInstallRefreshTarget.SelectAndScrollToMod(int id) => SelectAndScrollToMod(id);

    void IInstallRefreshTarget.UpdateActionButtons() => UpdateActionButtons();

    void IInstallRefreshTarget.SetStatusText(string text) => ViewModel.Status = text;

    string IInstallRefreshTarget.RestoreLoadedStatus() => RestoreLoadedStatus();

    void IInstallRefreshTarget.ReportCanceled(string detail) => ReportCanceled(detail);

    void IInstallRefreshTarget.InvalidateModelCache(int modId) => _modelPanel.Invalidate(modId);
}
