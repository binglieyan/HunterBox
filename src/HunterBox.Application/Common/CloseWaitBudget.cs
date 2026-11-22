namespace HunterBox.Application.Common;

/// <summary>
/// 关窗等待的墙钟预算：从创建时刻起累计，只减不增，不能通过新的等待轮次重置。
/// </summary>
/// <remarks>
/// 关窗流程用它限制「等待后台保存/操作结束」的总时长；中途出现新任务时可以继续等待，
/// 但剩余时间仍按最初开始时刻计算，避免持续续期把 60 秒变成无限。
/// 抽出到 Application 是为了用可控时钟做计时测试（App 的关窗流程本身依赖 WinUI）。
/// </remarks>
public sealed class CloseWaitBudget
{
    private readonly TimeSpan _limit;
    private readonly IClock _clock;
    private readonly DateTimeOffset _startedAt;

    /// <summary>创建预算。</summary>
    /// <param name="limit">总等待上限，必须大于 0。</param>
    /// <param name="clock">时钟端口；为 <see langword="null"/> 时使用系统时钟。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> 不大于 0。</exception>
    public CloseWaitBudget(TimeSpan limit, IClock? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);
        _limit = limit;
        _clock = clock ?? SystemClock.Instance;
        _startedAt = _clock.UtcNow;
    }

    /// <summary>剩余毫秒数（向上取整）；已用尽时为 0。</summary>
    public int RemainingMilliseconds
    {
        get
        {
            TimeSpan remaining = _limit - (_clock.UtcNow - _startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            return (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);
        }
    }

    /// <summary>预算是否已用尽。</summary>
    public bool IsExhausted => RemainingMilliseconds == 0;
}
