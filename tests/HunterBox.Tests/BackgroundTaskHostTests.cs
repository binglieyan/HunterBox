using System.Collections.Concurrent;
using HunterBox.Application.Concurrency;

namespace HunterBox.Tests;

/// <summary>后台任务宿主：串行登记、未完成计数与空闲等待（A-07）。</summary>
public class BackgroundTaskHostTests
{
    [Fact]
    public async Task EnqueueAsync_RunsWorkSeriallyInRegistrationOrder()
    {
        BackgroundTaskHost host = new();
        ConcurrentQueue<int> order = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = host.EnqueueAsync(async () =>
        {
            order.Enqueue(1);
            await gate.Task;
            order.Enqueue(2);
        });
        Task second = host.EnqueueAsync(() =>
        {
            order.Enqueue(3);
            return Task.CompletedTask;
        });

        await WaitUntilAsync(() => order.Contains(1));
        Assert.Equal(new[] { 1 }, order);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public async Task PendingCount_TracksRunningAndQueuedWork()
    {
        BackgroundTaskHost host = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = host.EnqueueAsync(() => gate.Task);
        Task second = host.EnqueueAsync(() => Task.CompletedTask);

        await WaitUntilAsync(() => host.PendingCount == 2);
        Assert.Equal(2, host.PendingCount);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(0, host.PendingCount);
    }

    [Fact]
    public async Task EnqueueAsync_FailedWork_DoesNotBlockFollowingWork()
    {
        BackgroundTaskHost host = new();
        bool secondRan = false;

        Task first = host.EnqueueAsync(() => throw new InvalidOperationException("boom"));
        Task second = host.EnqueueAsync(() =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second;

        Assert.True(secondRan);
        Assert.Equal(0, host.PendingCount);
    }

    [Fact]
    public async Task WhenIdleAsync_NoPendingWork_ReturnsTrue()
    {
        BackgroundTaskHost host = new();

        Assert.True(await host.WhenIdleAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task WhenIdleAsync_PendingWorkExceedsTimeout_ReturnsFalse()
    {
        BackgroundTaskHost host = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = host.EnqueueAsync(() => gate.Task);

        bool idle = await host.WhenIdleAsync(TimeSpan.FromMilliseconds(20));

        Assert.False(idle);

        gate.SetResult();
        await work;
        Assert.True(await host.WhenIdleAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task WhenIdleAsync_WaitsForCompletion_ReturnsTrue()
    {
        BackgroundTaskHost host = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = host.EnqueueAsync(() => gate.Task);

        Task<bool> waiting = host.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted);

        gate.SetResult();

        Assert.True(await waiting);
        await work;
    }

    [Fact]
    public async Task WhenIdleAsync_NegativeTimeout_Throws()
    {
        BackgroundTaskHost host = new();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => host.WhenIdleAsync(TimeSpan.FromMilliseconds(-2)));
    }

    /// <summary>调用方取消必须原样传播，不能被等待辅助吞成「任务失败」。</summary>
    [Fact]
    public async Task BackgroundTaskWait_CallerCancellation_Propagates()
    {
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BackgroundTaskWait.TryWaitAsync(never.Task, TimeSpan.FromSeconds(5), cancellation.Token));
    }

    /// <summary>任务故障与超时都按「未确认完成」返回 false。</summary>
    [Fact]
    public async Task BackgroundTaskWait_FaultedOrTimedOut_ReturnsFalse()
    {
        Task faulted = Task.FromException(new InvalidOperationException("boom"));
        Assert.False(await BackgroundTaskWait.TryWaitAsync(faulted, TimeSpan.FromSeconds(1)));

        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(await BackgroundTaskWait.TryWaitAsync(never.Task, TimeSpan.FromMilliseconds(20)));
    }

    /// <summary>正常完成返回 true。</summary>
    [Fact]
    public async Task BackgroundTaskWait_Completed_ReturnsTrue()
    {
        Assert.True(await BackgroundTaskWait.TryWaitAsync(Task.CompletedTask, TimeSpan.FromSeconds(1)));
    }

    /// <summary>负超时是参数错误，必须显式抛出而不是当成「未完成」。</summary>
    [Fact]
    public async Task BackgroundTaskWait_NegativeTimeout_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BackgroundTaskWait.TryWaitAsync(Task.CompletedTask, TimeSpan.FromMilliseconds(-2)));

        Assert.True(await BackgroundTaskWait.TryWaitAsync(
            Task.CompletedTask,
            Timeout.InfiniteTimeSpan));
    }

    /// <summary>任务已完成时也必须观察调用方取消，契约不因完成状态而改变。</summary>
    [Fact]
    public async Task BackgroundTaskWait_CompletedTaskWithCanceledToken_Throws()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BackgroundTaskWait.TryWaitAsync(Task.CompletedTask, TimeSpan.FromSeconds(1), cancellation.Token));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("等待条件超时");
    }
}
