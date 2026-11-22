namespace HunterBox.Application.Concurrency;

/// <summary>
/// 后台任务宿主端口：登记串行写盘任务，暴露未完成数量与空闲等待（A-07）。
/// </summary>
/// <remarks>
/// <para>
/// 界面层不再自己维护「计数器 + 任务链 + 轮询」：所有后台写盘都登记到本宿主，
/// 关窗等生命周期判断只读 <see cref="PendingCount"/> 或等待 <see cref="WhenIdleAsync"/>。
/// </para>
/// <para>宿主不吞任务异常：异常保留在 <see cref="EnqueueAsync"/> 返回的任务上，由登记方观察并记录。</para>
/// </remarks>
public interface IBackgroundTaskHost
{
    /// <summary>尚未完成的后台任务数量。</summary>
    int PendingCount { get; }

    /// <summary>按登记顺序串行执行一个后台任务。</summary>
    /// <param name="work">要执行的工作；前一个任务完成（无论成功失败）后才开始。</param>
    /// <returns>本次任务的完成任务；工作抛出的异常会保留在这个任务上。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> 为 <see langword="null"/>。</exception>
    Task EnqueueAsync(Func<Task> work);

    /// <summary>等待全部已登记任务完成。</summary>
    /// <remarks>返回 <see langword="true"/> 时宿主确实处于空闲；等待期间新登记的任务也会被纳入。</remarks>
    /// <param name="timeout">最长等待时间；使用 <see cref="Timeout.InfiniteTimeSpan"/> 表示一直等待。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>在超时前空闲时返回 <see langword="true"/>；超时仍有任务时返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> 为负且不是无限等待。</exception>
    Task<bool> WhenIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
