using HunterBox.Application.Common;
using HunterBox.Infrastructure.Logging;

namespace HunterBox.App;

/// <summary>
/// 安装/重装/更新后的刷新协调器：集中决定「何时重建列表、写什么状态、选中哪一行」，
/// 窗口只实现 <see cref="IInstallRefreshTarget"/> 的控件操作（A-06）。
/// </summary>
internal sealed class InstallRefreshCoordinator
{
    private readonly IInstallRefreshTarget _target;
    private readonly IAppLogger _logger;

    /// <summary>装配协调器。</summary>
    /// <param name="target">界面刷新目标（通常由主窗口实现）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> 为 <see langword="null"/>。</exception>
    public InstallRefreshCoordinator(IInstallRefreshTarget target, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <summary>一批安装结束后的刷新与状态提示。</summary>
    /// <param name="installed">成功安装的数量。</param>
    /// <param name="lastId">最后一个成功安装的 MOD ID（0 表示没有）。</param>
    /// <param name="busy">是否因其它操作占用而提前结束。</param>
    /// <param name="canceled">是否被用户取消。</param>
    public void AfterInstallBatch(int installed, int lastId, bool busy, bool canceled)
    {
        _target.ReloadModsForRefresh();
        if (busy)
        {
            _target.SetStatusText(OperationRunner.BusyReentryMessage);
        }
        else if (canceled)
        {
            _target.ReportCanceled(
                installed > 0 ? $"已安装 {installed} 个 MOD，其余未安装" : "本次没有安装任何 MOD");
        }
        else if (installed > 0)
        {
            _target.SetStatusText($"已安装 {installed} 个 MOD");
            _target.SelectAndScrollToMod(lastId);
        }
        else
        {
            _target.SetStatusText(_target.RestoreLoadedStatus());
        }

        _target.UpdateActionButtons();
    }

    /// <summary>重装/更新成功后的刷新与状态提示。</summary>
    /// <param name="modId">被替换的 MOD ID。</param>
    /// <param name="displayName">显示名（状态栏文案）。</param>
    /// <param name="primaryText">操作名（重装/更新）。</param>
    public void AfterReinstall(int modId, string displayName, string primaryText)
    {
        // 重装/更新会替换 MOD 文件：文件清单可能没变但 pak 内容已变，模型缓存必须失效。
        _target.InvalidateModelCache(modId);
        try
        {
            _target.ReloadModsForRefresh();
        }
        catch (Exception ex)
        {
            _logger.Error("重装/更新后刷新列表失败", ex);
        }

        _target.SetStatusText($"{primaryText}完成：{displayName}");
        _target.SelectAndScrollToMod(modId);
        _target.UpdateActionButtons();
    }
}
