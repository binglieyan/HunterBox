using HunterBox.Application.Common;
using HunterBox.Application.Models;

namespace HunterBox.Tests;

/// <summary>写盘调度的去抖、重试上限、独立重试与脏标记语义由单元测试覆盖。</summary>
public class ModelTextCachePersistSchedulerTests
{
    /// <summary>端口返回失败时按上限重试，达到上限后放弃且不再排队。</summary>
    [Fact]
    public async Task Flush_SaveFails_RetriesUpToLimitThenGivesUp()
    {
        FakeStore store = new() { FailuresBeforeSuccess = int.MaxValue };
        CountingLogger logger = new();
        ModelTextCachePersistScheduler queue = CreateQueue(store, logger, maxAttempts: 3);

        Assert.True(queue.Request());
        await queue.FlushAsync();
        await queue.WhenIdleAsync();

        Assert.Equal(3, store.Attempts);
        Assert.Contains(logger.Warnings, warning => warning.Contains("放弃本次写入", StringComparison.Ordinal));

        // 放弃后调度标志已复位：下一次请求可以重新启动写盘。
        Assert.True(queue.Request());
    }

    /// <summary>失败后恢复成功即停止重试，本次请求的脏标记被清空。</summary>
    [Fact]
    public async Task Flush_SaveSucceedsAfterRetry_StopsRetrying()
    {
        FakeStore store = new() { FailuresBeforeSuccess = 2 };
        ModelTextCachePersistScheduler queue = CreateQueue(store, new CountingLogger(), maxAttempts: 3);

        queue.Request();
        await queue.FlushAsync();
        await queue.WhenIdleAsync();

        Assert.Equal(3, store.Attempts);

        // 没有新的请求时再次 Flush 不应重复写盘。
        await queue.FlushAsync();
        Assert.Equal(3, store.Attempts);
    }

    /// <summary>已有任务排队时重复请求只置脏标记，不重复启动。</summary>
    [Fact]
    public void Request_WhileScheduled_ReturnsFalseButKeepsDirty()
    {
        FakeStore store = new();
        ModelTextCachePersistScheduler queue = CreateQueue(store, new CountingLogger(), maxAttempts: 3);

        Assert.True(queue.Request());
        Assert.False(queue.Request());
    }

    /// <summary>重试延迟不占用首次 Flush 任务（不阻塞串行后台宿主）。</summary>
    [Fact]
    public async Task Flush_Failure_ReturnsBeforeRetryDelayCompletes()
    {
        FakeStore store = new() { FailuresBeforeSuccess = 1 };
        TaskCompletionSource retryGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModelTextCachePersistScheduler queue = new(
            store,
            () => new ModelTextCacheSnapshot([new ModelTextCacheEntry(1, "sig", "文本", "stamp", "mhws")], Version: 1),
            new CountingLogger(),
            debounce: TimeSpan.Zero,
            retryDelay: TimeSpan.FromMinutes(1),
            maxAttempts: 3,
            delay: duration => duration == TimeSpan.Zero ? Task.CompletedTask : retryGate.Task);

        queue.Request();
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // 首次尝试已结束、重试仍在等待延迟：说明 FlushAsync 没有把延迟等待塞回调用方。
        Assert.Equal(1, store.Attempts);
        Assert.False(retryGate.Task.IsCompleted);

        retryGate.SetResult();
        await queue.WhenIdleAsync();
        Assert.Equal(2, store.Attempts);
    }

    /// <summary>端口实现抛 IOException 时按失败重试，分支可达。</summary>
    [Fact]
    public async Task Flush_SaveThrows_TreatedAsFailureAndRetries()
    {
        FakeStore store = new() { ThrowOnFirstSave = true };
        ModelTextCachePersistScheduler queue = CreateQueue(store, new CountingLogger(), maxAttempts: 3);

        queue.Request();
        await queue.FlushAsync();
        await queue.WhenIdleAsync();

        Assert.Equal(2, store.Attempts);
    }

    /// <summary>
    /// 版本比较期间到达的变更不能丢——短路口必须复查脏标记并重新快照落盘。
    /// </summary>
    [Fact]
    public async Task Flush_ChangeArrivesDuringVersionCompare_StillSaves()
    {
        FakeStore store = new();
        long version = 1;
        ModelTextCachePersistScheduler? queue = null;
        Func<ModelTextCacheSnapshot> snapshot = () => new ModelTextCacheSnapshot([], version);
        queue = new ModelTextCachePersistScheduler(
            store,
            () => snapshot(),
            new CountingLogger(),
            debounce: TimeSpan.Zero,
            retryDelay: TimeSpan.Zero,
            maxAttempts: 3,
            delay: _ => Task.CompletedTask);

        // 第一次：版本 1 与初始基线 0 不同，写盘一次。
        queue.Request();
        await queue.FlushAsync();
        await queue.WhenIdleAsync();
        Assert.Equal(1, store.Attempts);

        // 第二次：快照返回「与上次写入相同」的版本，但快照之后立即发生了真实变更。
        int snapshotCalls = 0;
        snapshot = () =>
        {
            long captured = version;
            if (snapshotCalls++ == 0)
            {
                version = 2;
                Assert.False(queue.Request());
            }

            return new ModelTextCacheSnapshot([], captured);
        };

        queue.Request();
        await queue.FlushAsync();
        await queue.WhenIdleAsync();

        // 修复前：版本相等直接 MarkIdle 返回，version=2 的变更永远不落盘。
        Assert.Equal(2, store.Attempts);
        Assert.False(queue.IsPending);
    }

    private static ModelTextCachePersistScheduler CreateQueue(
        IModelTextCacheStore store,
        IAppLogger logger,
        int maxAttempts) =>
        new(
            store,
            () => new ModelTextCacheSnapshot([new ModelTextCacheEntry(1, "sig", "文本", "stamp", "mhws")], Version: 1),
            logger,
            debounce: TimeSpan.Zero,
            retryDelay: TimeSpan.Zero,
            maxAttempts: maxAttempts,
            delay: _ => Task.CompletedTask);

    /// <summary>快照版本未变化时不写盘。</summary>
    [Fact]
    public async Task Flush_UnchangedVersion_SkipsSave()
    {
        FakeStore store = new();
        ModelTextCachePersistScheduler queue = new(
            store,
            () => new ModelTextCacheSnapshot([], Version: 0),
            new CountingLogger(),
            debounce: TimeSpan.Zero,
            retryDelay: TimeSpan.Zero,
            maxAttempts: 3,
            delay: _ => Task.CompletedTask);

        queue.Request();
        await queue.FlushAsync();
        await queue.WhenIdleAsync();

        Assert.Equal(0, store.Attempts);
    }

    private sealed class FakeStore : IModelTextCacheStore
    {
        public int Attempts { get; private set; }

        public int FailuresBeforeSuccess { get; init; }

        public bool ThrowOnFirstSave { get; init; }

        public bool TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries)
        {
            entries = [];
            return true;
        }

        public bool Save(IReadOnlyList<ModelTextCacheEntry> entries)
        {
            Attempts++;
            if (ThrowOnFirstSave && Attempts == 1)
            {
                throw new IOException("模拟写盘异常");
            }

            return Attempts > FailuresBeforeSuccess;
        }
    }

    private sealed class CountingLogger : IAppLogger
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void WarnBatch(IReadOnlyList<string> messages) => Warnings.AddRange(messages);

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
