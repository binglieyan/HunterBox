namespace HunterBox.App;

/// <summary>
/// 安装/重装完成后的界面刷新目标：由 <see cref="InstallRefreshCoordinator"/> 调用，
/// 窗口只提供控件操作，不在编排里直接引用控件。
/// </summary>
internal interface IInstallRefreshTarget
{
    /// <summary>重建当前游戏的 MOD 列表，但不覆盖状态栏结论。</summary>
    void ReloadModsForRefresh();

    /// <summary>选中并滚动到指定 MOD。</summary>
    /// <param name="id">MOD ID。</param>
    void SelectAndScrollToMod(int id);

    /// <summary>按忙碌/选中状态刷新按钮可用性。</summary>
    void UpdateActionButtons();

    /// <summary>写入状态栏文本。</summary>
    /// <param name="text">提示文本。</param>
    void SetStatusText(string text);

    /// <summary>生成当前模式下的列表状态文本（搜索模式显示筛选计数与关键词）。</summary>
    /// <returns>状态栏文本。</returns>
    string RestoreLoadedStatus();

    /// <summary>写入取消提示。</summary>
    /// <param name="detail">取消明细。</param>
    void ReportCanceled(string detail);

    /// <summary>使指定 MOD 的模型文本缓存失效。</summary>
    /// <param name="modId">MOD ID。</param>
    void InvalidateModelCache(int modId);
}
