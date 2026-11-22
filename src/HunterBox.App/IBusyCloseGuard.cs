namespace HunterBox.App;

/// <summary>
/// 「忙碌期间禁止关闭」的对话框保护：主窗口关闭时先询问对话框是否正在执行写盘类长任务，
/// 是则取消这次关闭并给出可见反馈，避免 Alt+F4 / 关闭按钮把长任务腰斩成半新半旧的数据。
/// </summary>
/// <remarks>
/// ContentDialog 本身不能真正禁用「关闭」按钮，因此对话框自己实现该接口并在 ContentDialog.Closing
/// 里取消关闭；主窗口的 <c>AppWindow.Closing</c> 再用本接口拦截「对话框打开时 Alt+F4 关主窗口」这条路。
/// </remarks>
internal interface IBusyCloseGuard
{
    /// <summary>对话框是否正在执行不允许中断的长任务（备份/恢复/删除/导入）。</summary>
    bool IsBusy { get; }

    /// <summary>关闭被拦截时给用户的可见反馈（由对话框自己写到内部状态栏）。</summary>
    void OnCloseBlocked();
}
