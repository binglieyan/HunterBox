using HunterBox.Application.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>过期内容戳的预算、轮换、取消、异常隔离与告警。</summary>
public class ModelStampRefreshTests
{
    /// <summary>没有过期候选时不调用刷新委托。</summary>
    [Fact]
    public void RefreshWithinBudget_Empty_DoesNotInvokeCallback()
    {
        int calls = 0;
        ModelStampRefresh.RefreshWithinBudget(
            [],
            cursor: 3,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: _ => calls++,
            CancellationToken.None,
            warnBatch: null,
            out int nextCursor);

        Assert.Equal(0, nextCursor);
        Assert.Equal(0, calls);
    }

    /// <summary>首项已超预算也至少处理一个，保证游标不会永远卡住。</summary>
    [Fact]
    public void RefreshWithinBudget_BudgetAlreadyExceeded_ProcessesAtLeastOne()
    {
        ModRecord[] candidates = [Create(1), Create(2), Create(3)];
        List<int> refreshedIds = [];
        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 999,
            refresh: mod => refreshedIds.Add(mod.Id),
            CancellationToken.None,
            warnBatch: null,
            out int nextCursor);

        Assert.Equal(1, nextCursor);
        Assert.Equal([1], refreshedIds);
    }

    /// <summary>预算耗尽后停止消费，已耗时的检查发生在首个条目之后。</summary>
    [Fact]
    public void RefreshWithinBudget_BudgetExhausted_Stops()
    {
        ModRecord[] candidates = [Create(1), Create(2), Create(3), Create(4)];
        List<int> refreshedIds = [];
        int elapsedCalls = 0;
        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => ++elapsedCalls == 1 ? 0 : 30,
            refresh: mod => refreshedIds.Add(mod.Id),
            CancellationToken.None,
            warnBatch: null,
            out int nextCursor);

        Assert.Equal(2, nextCursor);
        Assert.Equal([1, 2], refreshedIds);
    }

    /// <summary>每轮消费预算内的条目后写回游标，多轮打开覆盖全部候选且不重复。</summary>
    [Fact]
    public void RefreshWithinBudget_RotatesAcrossRounds()
    {
        ModRecord[] candidates = [Create(10), Create(11), Create(12), Create(13)];
        List<int> refreshedIds = [];
        int cursor = 0;

        for (int round = 0; round < candidates.Length; round++)
        {
            ModelStampRefresh.RefreshWithinBudget(
                candidates,
                cursor,
                budgetMilliseconds: 0,
                elapsedMilliseconds: () => 0,
                refresh: mod => refreshedIds.Add(mod.Id),
                CancellationToken.None,
                warnBatch: null,
                out cursor);
        }

        Assert.Equal([10, 11, 12, 13], refreshedIds);
    }

    /// <summary>负游标按模运算回绕到末尾候选。</summary>
    [Fact]
    public void RefreshWithinBudget_NegativeCursor_WrapsAround()
    {
        ModRecord[] candidates = [Create(1), Create(2), Create(3)];
        List<int> refreshedIds = [];
        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: -1,
            budgetMilliseconds: 0,
            elapsedMilliseconds: () => 0,
            refresh: mod => refreshedIds.Add(mod.Id),
            CancellationToken.None,
            warnBatch: null,
            out int nextCursor);

        Assert.Equal([3], refreshedIds);
        Assert.Equal(0, nextCursor);
    }

    /// <summary>取消令牌在条目边界生效：已取消时不刷新、游标保持原位置。</summary>
    [Fact]
    public void RefreshWithinBudget_Cancelled_Stops()
    {
        ModRecord[] candidates = [Create(1), Create(2), Create(3)];
        using CancellationTokenSource source = new();
        source.Cancel();
        int calls = 0;

        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 1,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: _ => calls++,
            source.Token,
            warnBatch: null,
            out int nextCursor);

        Assert.Equal(1, nextCursor);
        Assert.Equal(0, calls);
    }

    /// <summary>单个候选抛 IO/权限异常只跳过该项，后续候选继续处理并产生告警。</summary>
    [Fact]
    public void RefreshWithinBudget_SingleItemThrows_ContinuesAndWarns()
    {
        ModRecord[] candidates = [Create(1), Create(2), Create(3)];
        List<int> refreshedIds = [];
        List<string> warnings = [];

        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: mod =>
            {
                if (mod.Id == 1)
                {
                    throw new IOException("模拟不可读");
                }

                refreshedIds.Add(mod.Id);
            },
            CancellationToken.None,
            messages => warnings.AddRange(messages),
            out int nextCursor);

        Assert.Equal([2, 3], refreshedIds);
        Assert.Equal(0, nextCursor);
        Assert.Contains(warnings, warning => warning.Contains("MOD 1", StringComparison.Ordinal));
    }

    /// <summary>整批失败时逐项告警最多 10 条，其余合并为一条汇总，且整轮只调用一次批量告警。</summary>
    [Fact]
    public void RefreshWithinBudget_ManyFailures_CapsWarnings()
    {
        ModRecord[] candidates = [.. Enumerable.Range(1, 12).Select(Create)];
        List<string> warnings = [];
        int batchCalls = 0;

        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: _ => throw new IOException("整盘不可读"),
            CancellationToken.None,
            messages =>
            {
                batchCalls++;
                warnings.AddRange(messages);
            },
            out _);

        Assert.Equal(1, batchCalls);
        Assert.Equal(11, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("已省略", StringComparison.Ordinal));
        Assert.Equal(10, warnings.Count(warning => warning.Contains("（MOD ", StringComparison.Ordinal)));
    }

    /// <summary>单项失败只产生一条告警，并且同样通过一次批量调用写出。</summary>
    [Fact]
    public void RefreshWithinBudget_SingleFailure_CallsWarnBatchOnce()
    {
        ModRecord[] candidates = [Create(1), Create(2)];
        List<string> warnings = [];
        int batchCalls = 0;

        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: mod =>
            {
                if (mod.Id == 1)
                {
                    throw new IOException("模拟不可读");
                }
            },
            CancellationToken.None,
            messages =>
            {
                batchCalls++;
                warnings.AddRange(messages);
            },
            out _);

        Assert.Equal(1, batchCalls);
        Assert.Single(warnings);
        Assert.Contains("MOD 1", warnings[0], StringComparison.Ordinal);
    }

    /// <summary>告警回调自身抛异常不能中断刷新。</summary>
    [Fact]
    public void RefreshWithinBudget_WarnThrows_DoesNotInterruptRefresh()
    {
        ModRecord[] candidates = [Create(1), Create(2), Create(3)];
        List<int> refreshedIds = [];

        ModelStampRefresh.RefreshWithinBudget(
            candidates,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: mod =>
            {
                if (mod.Id == 1)
                {
                    throw new IOException("模拟不可读");
                }

                refreshedIds.Add(mod.Id);
            },
            CancellationToken.None,
            _ => throw new InvalidOperationException("日志写入失败"),
            out int nextCursor);

        Assert.Equal([2, 3], refreshedIds);
        Assert.Equal(0, nextCursor);
    }

    /// <summary>负数预算属于调用方错误，直接抛出。</summary>
    [Fact]
    public void RefreshWithinBudget_NegativeBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelStampRefresh.RefreshWithinBudget(
            [],
            cursor: 0,
            budgetMilliseconds: -1,
            elapsedMilliseconds: () => 0,
            refresh: _ => { },
            CancellationToken.None,
            warnBatch: null,
            out _));
    }

    private static ModRecord Create(int id) => new()
    {
        Id = id,
        Index = id,
        GameId = TestGames.Wilds.Id,
        Kind = ModKind.LooseFiles,
        RelativeFiles = ["natives/stm/a.mod3"],
        Metadata = new ModMetadata(),
    };
}
