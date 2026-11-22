namespace HunterBox.Application.Concurrency;

/// <summary>
/// 后台任务等待辅助：把超时与任务故障转换为 <see langword="false"/>，调用方取消原样传播。
/// </summary>
/// <remarks>
/// 用于「等待某次保存/准备完成，但失败只做提示、不把整体流程判失败」的启动/收尾路径；
/// 与 <see cref="BackgroundTaskHost.WhenIdleAsync"/> 的区别是只等单个任务，并且不抛出任务故障。
/// </remarks>
public static class BackgroundTaskWait
{
    /// <summary>等待单个任务完成。</summary>
    /// <param name="task">要等待的任务。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>任务在超时前正常完成时返回 <see langword="true"/>；超时或任务故障返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> 为负且不是无限等待。</exception>
    /// <exception cref="OperationCanceledException">调用方取消（原样传播，不吞成失败；任务已完成时也会检查）。</exception>
    public static async Task<bool> TryWaitAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "等待时长不能为负（无限等待请用 Timeout.InfiniteTimeSpan）。");
        }

        // 目标任务已完成时 WaitAsync 不会再检查令牌，入口先显式检查，保证「调用方取消原样传播」始终成立。
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 任务自身故障/取消：只表示「未确认完成」，由调用方决定是否提示。
            return false;
        }
    }
}
