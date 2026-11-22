using HunterBox.Application.Common;

namespace HunterBox.Tests;

/// <summary>关窗墙钟预算只减不增，收尾期新任务不能重置已用时间。</summary>
public class CloseWaitBudgetTests
{
    /// <summary>剩余时间随时钟推进单调递减，用尽后保持 0。</summary>
    [Fact]
    public void RemainingMilliseconds_DecreasesAndNeverResets()
    {
        MutableClock clock = new();
        CloseWaitBudget budget = new(TimeSpan.FromSeconds(60), clock);

        Assert.Equal(60_000, budget.RemainingMilliseconds);
        Assert.False(budget.IsExhausted);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(50_000, budget.RemainingMilliseconds);

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(0, budget.RemainingMilliseconds);
        Assert.True(budget.IsExhausted);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, budget.RemainingMilliseconds);
    }

    /// <summary>不足 1ms 的剩余时间向上取整，避免提前判定用尽。</summary>
    [Fact]
    public void RemainingMilliseconds_RoundsUpPartialMilliseconds()
    {
        MutableClock clock = new();
        CloseWaitBudget budget = new(TimeSpan.FromMilliseconds(10), clock);

        clock.Advance(TimeSpan.FromMilliseconds(9.5));
        Assert.Equal(1, budget.RemainingMilliseconds);
    }

    /// <summary>非正上限必须在构造时拒绝。</summary>
    [Fact]
    public void Constructor_NonPositiveLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloseWaitBudget(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloseWaitBudget(TimeSpan.FromSeconds(-1)));
    }
}
