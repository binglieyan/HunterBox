using HunterBox.Application.Common;

using Microsoft.UI.Windowing;

namespace HunterBox.App;

/// <summary>
/// 主窗口关闭保护（C-08）：有长任务在跑时取消这次关闭、请求协作取消并等待任务退出后再关窗；
/// 忙碌中的 SaveManagerDialog 打开时同样拦截 Alt+F4（长任务不被腰斩）。
/// </summary>
/// <remarks>
/// 关窗保护只做「取消 + 异步等待」，<b>绝不在 UI 线程上同步阻塞</b>：
/// 操作本身在后台线程执行，等待用 <see cref="Task.Delay(int)"/> 让出消息循环，因此不会造成关窗死锁；
/// 空闲（无进行中操作、无忙碌对话框）时 Closing 原样放行，窗口正常关闭。
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>等待操作结束的轮询间隔（毫秒）。</summary>
    private const int CloseWaitPollMilliseconds = 100;

    /// <summary>等待操作结束的上限（毫秒）：超时只提示、不强关，避免把仍在写盘的任务腰斩。</summary>
    private const int MaxCloseWaitMilliseconds = 60_000;

    /// <summary>操作结束后再等一拍（毫秒），让操作自己的收尾提示/状态更新先执行完。</summary>
    private const int CloseAfterOperationDelayMilliseconds = 250;

    /// <summary>收尾后二次确认间隔（毫秒）：捕捉刚好在检查后打开的结果对话框。</summary>
    private const int CloseAfterOperationSettleMilliseconds = 100;

    /// <summary>当前打开的、需要关闭保护的忙碌对话框（由 <see cref="ShowDialogAsync"/> 维护）。</summary>
    private IBusyCloseGuard? _busyCloseGuard;

    /// <summary>用户已请求关闭窗口、但当时有操作在跑：操作结束后自动关闭。</summary>
    private bool _closeAfterOperationPending;

    /// <summary>是否有「等待操作结束后关窗」的异步任务在跑（避免重复排队）。</summary>
    private bool _closeWatcherRunning;

    /// <summary>
    /// 本次关窗请求的墙钟预算：watcher 因 Close() 二次拦截而重建时继续复用，不重置。
    /// 放弃关闭（仍有任务/对话框）后清空，用户下一次关窗重新计时。
    /// </summary>
    private CloseWaitBudget? _closeBudget;

    /// <summary>窗口已获准关闭（正在/即将销毁）：后台显示增强任务不得再调度新的 UI 工作。</summary>
    private bool _windowClosing;

    /// <summary>是否还有任何未完成的后台写盘工作（操作、异步索引保存或模型缓存写盘重试）。</summary>
    private bool HasPendingWork =>
        _operations.IsBusy
        || _busyCloseGuard is { IsBusy: true }
        || _services.BackgroundTasks.PendingCount > 0
        || _modelPanel.HasPendingCacheWrite;

    /// <summary>
    /// 主窗口 Closing 拦截：有进行中操作时取消关闭并请求取消/等待；忙碌对话框打开时同样拦截。
    /// </summary>
    /// <param name="sender">应用窗口。</param>
    /// <param name="args">关闭事件参数；置 <see cref="AppWindowClosingEventArgs.Cancel"/> 可取消本次关闭。</param>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_operations.IsBusy)
        {
            args.Cancel = true;
            _operations.Cancel();
            ViewModel.Status = "有操作正在进行：已请求取消，操作结束后将自动关闭窗口";
            _closeAfterOperationPending = true;
            if (!_closeWatcherRunning)
            {
                _closeWatcherRunning = true;
                _closeBudget ??= new CloseWaitBudget(TimeSpan.FromMilliseconds(MaxCloseWaitMilliseconds));
                _ = CloseAfterOperationAsync(_closeBudget);
            }

            return;
        }

        // 序号整理等低频保存是 fire-and-forget：关窗必须等它落盘，否则后台写盘会被进程退出腰斩。
        if (_services.BackgroundTasks.PendingCount > 0 || _modelPanel.HasPendingCacheWrite)
        {
            args.Cancel = true;
            ViewModel.Status = _modelPanel.HasPendingCacheWrite
                ? "正在保存模型缓存：保存完成后将自动关闭窗口"
                : "正在保存 MOD 索引：保存完成后将自动关闭窗口";
            _closeAfterOperationPending = true;
            if (!_closeWatcherRunning)
            {
                _closeWatcherRunning = true;
                _closeBudget ??= new CloseWaitBudget(TimeSpan.FromMilliseconds(MaxCloseWaitMilliseconds));
                _ = CloseAfterOperationAsync(_closeBudget);
            }

            return;
        }

        if (_busyCloseGuard is { IsBusy: true } guard)
        {
            // 对话框自己的长任务没有协作取消入口：拦下关闭，由对话框显示「完成后再关闭」。
            args.Cancel = true;
            guard.OnCloseBlocked();
            return;
        }

        // 有对话框正在显示（包括结果提示）时不能关窗：ContentDialog.ShowAsync 的等待方
        // 会在已销毁的 XamlRoot 上挂起或抛异常，提示也被窗口关闭吞掉。
        if (_dialogGate.CurrentCount == 0)
        {
            args.Cancel = true;
            ViewModel.Status = "还有对话框未关闭，请先关闭对话框再关闭窗口";
            return;
        }

        // 空闲：正常关闭。
        _windowClosing = true;
        _closeBudget = null;
    }

    /// <summary>等待进行中的操作结束（异步让出 UI 线程），然后按用户请求关闭窗口。</summary>
    /// <remarks>
    /// 操作结束后的 250ms 收尾窗口内可能启动新的操作/模型预热或打开结果对话框，
    /// 因此延迟后必须重新检查忙碌状态与对话框门；不能等到时间就直接 <c>Close()</c>。
    /// 等待预算是一次性的墙钟总预算；收尾期间出现新任务时继续等待，但不会重置已用时间。
    /// </remarks>
    private async Task CloseAfterOperationAsync(CloseWaitBudget budget)
    {
        try
        {
            // 预算由本次关窗请求持有，watcher 重建不会重置总等待时间。
            while (true)
            {
                while (HasPendingWork && !budget.IsExhausted)
                {
                    // 索引保存由后台宿主提供空闲信号；模型缓存写盘重试不在宿主里，单独等待。
                    if (!_operations.IsBusy && _busyCloseGuard is not { IsBusy: true })
                    {
                        int remaining = budget.RemainingMilliseconds;
                        if (!await _services.BackgroundTasks.WhenIdleAsync(
                            TimeSpan.FromMilliseconds(remaining)))
                        {
                            break;
                        }

                        remaining = budget.RemainingMilliseconds;
                        if (remaining <= 0 || !await WaitForModelCacheIdleAsync(remaining))
                        {
                            break;
                        }

                        // 保存已全部完成：重新检查是否又启动了新任务/对话框。
                        continue;
                    }

                    await Task.Delay(CloseWaitPollMilliseconds);
                }

                if (HasPendingWork)
                {
                    _closeAfterOperationPending = false;
                    _closeBudget = null;
                    ViewModel.Status = "操作或索引保存仍在进行，窗口未关闭；请等待完成后再关闭窗口";
                    return;
                }

                if (!_closeAfterOperationPending)
                {
                    return;
                }

                // 再让一拍消息循环：让操作结束后的收尾（状态栏/提示）先执行完。
                await Task.Delay(CloseAfterOperationDelayMilliseconds);

                // 收尾期间可能又启动了新操作/预热/保存：继续等待，但总预算不重置。
                if (HasPendingWork)
                {
                    continue;
                }

                if (_dialogGate.CurrentCount == 0)
                {
                    _closeAfterOperationPending = false;
                    _closeBudget = null;
                    ViewModel.Status = "还有对话框未关闭，窗口未关闭；请关闭对话框后再试";
                    return;
                }

                // 二次确认：操作结果对话框可能刚好在第一次检查后申请到对话框门。
                await Task.Delay(CloseAfterOperationSettleMilliseconds);
                if (HasPendingWork)
                {
                    continue;
                }

                if (_dialogGate.CurrentCount == 0)
                {
                    _closeAfterOperationPending = false;
                    _closeBudget = null;
                    ViewModel.Status = "还有对话框未关闭，窗口未关闭；请关闭对话框后再试";
                    return;
                }

                _closeAfterOperationPending = false;
                _services.Logger.Info("操作与索引保存已结束，按用户请求关闭主窗口");
                // 不在这里提前置 _windowClosing：Close() 会再次经过 OnAppWindowClosing，
                // 若此刻恰好有新操作启动，让拦截逻辑正常生效并重新排队 watcher。
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            // 关窗等待失败不能把异常抛到 UI 线程（窗口保持打开，用户可重试）。
            _services.Logger.Error("等待操作结束后关闭窗口失败", ex);
        }
        finally
        {
            _closeWatcherRunning = false;
            if (_closeAfterOperationPending && !_windowClosing)
            {
                // 关闭请求仍挂起（例如 Close() 被新操作再次拦下）：用同一预算重新调度 watcher，避免窗口永不关闭。
                _closeWatcherRunning = true;
                _ = CloseAfterOperationAsync(budget);
            }
        }
    }

    /// <summary>等待模型文本缓存写盘（含失败重试）结束，超时返回 <see langword="false"/>。</summary>
    /// <param name="milliseconds">最长等待毫秒数。</param>
    /// <returns>空闲时返回 <see langword="true"/>。</returns>
    private async Task<bool> WaitForModelCacheIdleAsync(int milliseconds)
    {
        try
        {
            await _modelPanel.WhenCacheIdleAsync().WaitAsync(TimeSpan.FromMilliseconds(milliseconds));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
