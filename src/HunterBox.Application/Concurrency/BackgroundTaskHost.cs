using System.Diagnostics;

namespace HunterBox.Application.Concurrency;

/// <summary>
/// 串行后台任务宿主：同一时刻最多一个任务，登记顺序即执行顺序（A-07）。
/// </summary>
/// <remarks>
/// <para>
/// 用于替代界面层的 <c>_saveChain</c> + <c>_pendingIndexSaves</c>：写盘任务天然串行，
/// 关窗只需等待 <see cref="WhenIdleAsync"/>，不再轮询计数器。
/// </para>
/// <para>线程安全；<see cref="WhenIdleAsync"/> 用空闲信号而非轮询，任务结束时立即唤醒等待方。</para>
/// </remarks>
public sealed class BackgroundTaskHost : IBackgroundTaskHost
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private TaskCompletionSource<bool>? _idle;
    private int _pending;

    /// <summary>尚未完成的后台任务数量。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>按登记顺序串行执行一个后台任务。</summary>
    /// <param name="work">要执行的工作；前一个任务完成（无论成功失败）后才开始。</param>
    /// <returns>本次任务的完成任务；工作抛出的异常会保留在这个任务上。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> 为 <see langword="null"/>。</exception>
    public Task EnqueueAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            Task previous = _tail;
            _pending++;
            if (_idle is null)
            {
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _tail = Task.Run(() => RunAfterAsync(previous, work));
            return _tail;
        }
    }

    /// <summary>等待全部已登记任务完成。</summary>
    /// <remarks>等待期间新登记的任务也会被纳入；只有确实空闲时才返回 <see langword="true"/>。</remarks>
    /// <param name="timeout">最长等待时间；使用 <see cref="Timeout.InfiniteTimeSpan"/> 表示一直等待。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>在超时前空闲时返回 <see langword="true"/>；超时仍有任务时返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> 为负且不是无限等待。</exception>
    public async Task<bool> WhenIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "等待时长不能为负（无限等待请用 Timeout.InfiniteTimeSpan）。");
        }

        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            Task idleTask;
            lock (_gate)
            {
                if (_pending == 0)
                {
                    return true;
                }

                idleTask = _idle!.Task;
            }

            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : timeout - Stopwatch.GetElapsedTime(started);
            if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await idleTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    /// <summary>等待前序任务后执行本次任务；无论结果如何都减少未完成计数并唤醒空闲等待方。</summary>
    private async Task RunAfterAsync(Task previous, Func<Task> work)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 前序任务的异常由它自己的返回任务观察；串行队列必须继续执行后续任务。
        }

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            TaskCompletionSource<bool>? idle;
            lock (_gate)
            {
                _pending--;
                idle = _pending == 0 ? _idle : null;
                if (idle is not null)
                {
                    _idle = null;
                }
            }

            idle?.TrySetResult(true);
        }
    }
}
