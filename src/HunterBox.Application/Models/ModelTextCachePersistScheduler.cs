using HunterBox.Application.Common;

namespace HunterBox.Application.Models;

/// <summary>
/// 模型文本缓存写盘调度：去抖合并、失败有限次重试、请求去重。
/// </summary>
/// <remarks>
/// <para>
/// 与 UI 无关：调用方提供快照委托与执行时机（后台宿主或线程池），状态机本身可单元测试。
/// </para>
/// <para>
/// 线程模型：<see cref="Request"/> 可从任意线程调用；<see cref="FlushAsync"/> 在后台执行。
/// 生命周期：实例与 <c>ModelPanel</c> 同寿命，不持有 UI 对象；关窗时可调用 <see cref="WhenIdleAsync"/>
/// 等待首次写盘与失败重试全部结束，重试本身仍由独立线程池任务延迟执行，
/// 不占用串行后台队列。
/// </para>
/// <para>
/// 失败策略：端口返回失败或 IO 异常时恢复脏标记并延迟重试，连续失败达到 <c>maxAttempts</c> 后放弃并记录日志；
/// 其它异常按同一上限重试并记录原因，不会静默丢弃待写数据。
/// 快照版本与上次成功写入一致时跳过整份写盘，但会重新检查脏标记：
/// 比较期间到达的变更会在同一轮内重新快照并落盘。
/// </para>
/// </remarks>
public sealed class ModelTextCachePersistScheduler
{
    private readonly object _gate = new();
    private readonly IModelTextCacheStore _store;
    private readonly Func<ModelTextCacheSnapshot> _snapshot;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, Task> _delay;
    private bool _dirty;
    private bool _scheduled;
    private int _consecutiveFailures;
    private long _lastSavedVersion;

    /// <summary>等待本次排队（含失败重试）全部结束时完成；没有排队任务时为 <see langword="null"/>。</summary>
    private TaskCompletionSource? _idle;

    /// <summary>装配写盘调度器。</summary>
    /// <param name="store">持久化端口。</param>
    /// <param name="snapshot">快照委托（在后台线程调用，内部自行加锁）；带回版本号用于跳过无变化写盘。</param>
    /// <param name="logger">日志端口。</param>
    /// <param name="debounce">去抖时长；为 <see langword="null"/> 时使用 250ms。</param>
    /// <param name="retryDelay">失败重试间隔；为 <see langword="null"/> 时使用 500ms。</param>
    /// <param name="maxAttempts">最大连续尝试次数，必须大于 0；默认 3。</param>
    /// <param name="delay">延迟委托（测试可替换为立即完成）；为 <see langword="null"/> 时使用 <see cref="Task.Delay(TimeSpan)"/>。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAttempts"/> 不大于 0。</exception>
    public ModelTextCachePersistScheduler(
        IModelTextCacheStore store,
        Func<ModelTextCacheSnapshot> snapshot,
        IAppLogger logger,
        TimeSpan? debounce = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = 3,
        Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        _store = store;
        _snapshot = snapshot;
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
        _maxAttempts = maxAttempts;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>请求一次写盘；已有任务在排队/执行时只置脏标记。</summary>
    /// <returns>调用方需要启动 <see cref="FlushAsync"/> 时返回 <see langword="true"/>。</returns>
    public bool Request()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_scheduled)
            {
                return false;
            }

            _scheduled = true;
            return true;
        }
    }

    /// <summary>后台执行一次写盘（含去抖）；失败时安排独立重试任务，不在本任务内等待。</summary>
    /// <returns>首次尝试完成（或已安排重试）时结束的写盘任务。</returns>
    public Task FlushAsync() => FlushCoreAsync(applyDebounce: true);

    /// <summary>当前是否有排队/执行中的写盘（含等待中的失败重试）。</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _scheduled;
            }
        }
    }

    /// <summary>等待当前排队（含失败重试）全部结束；没有排队任务时立即完成。</summary>
    /// <remarks>用完成源代替轮询；关窗流程据此等待重试任务，不把写盘腰斩。</remarks>
    /// <returns>空闲时完成的任务。</returns>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            if (!_scheduled)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    private async Task FlushCoreAsync(bool applyDebounce)
    {
        try
        {
            if (applyDebounce)
            {
                await _delay(_debounce).ConfigureAwait(false);
            }

            while (true)
            {
                lock (_gate)
                {
                    if (!_dirty)
                    {
                        MarkIdleLocked();
                        return;
                    }

                    _dirty = false;
                }

                ModelTextCacheSnapshot snapshot = _snapshot();
                if (snapshot.Version == _lastSavedVersion)
                {
                    // 内容没有变化（例如预热全部命中）时不重写整份缓存。
                    // 快照与版本比较之间可能又有 Store/Invalidate 到达，
                    // 此时不能直接返回，必须重新检查脏标记，避免这次变更永远不落盘。
                    lock (_gate)
                    {
                        if (!_dirty)
                        {
                            MarkIdleLocked();
                            return;
                        }
                    }

                    continue;
                }

                bool saved;
                string? error = null;
                try
                {
                    saved = _store.Save(snapshot.Entries);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 端口契约要求返回 false；这里防御性处理第三方实现抛异常（由测试覆盖）。
                    saved = false;
                    error = ex.Message;
                }

                if (saved)
                {
                    lock (_gate)
                    {
                        _consecutiveFailures = 0;
                        _lastSavedVersion = snapshot.Version;
                    }

                    continue;
                }

                int failures;
                lock (_gate)
                {
                    // 失败计数只在锁内读写，避免并发重试时多算/少算一次。
                    failures = ++_consecutiveFailures;
                }

                if (failures >= _maxAttempts)
                {
                    _logger.Warn(
                        $"保存模型文本缓存失败（已连续尝试 {failures} 次，放弃本次写入）：{error ?? "端口返回失败"}");
                    lock (_gate)
                    {
                        MarkIdleLocked();
                        _consecutiveFailures = 0;
                    }

                    return;
                }

                _logger.Warn(
                    $"保存模型文本缓存失败，稍后重试（{failures}/{_maxAttempts}）：{error ?? "端口返回失败"}");
                ScheduleRetry();
                return;
            }
        }
        catch (Exception ex)
        {
            // 后台任务异常必须在这里观察并记录，不能让宿主任务带着故障结束。
            _logger.Warn($"保存模型文本缓存失败（不影响使用）：{ex.Message}");
            int failures;
            lock (_gate)
            {
                _dirty = true;
                failures = ++_consecutiveFailures;
            }

            if (failures < _maxAttempts)
            {
                _logger.Warn($"保存模型文本缓存异常，稍后重试（{failures}/{_maxAttempts}）");
                ScheduleRetry();
                return;
            }

            _logger.Warn($"保存模型文本缓存异常（已连续尝试 {failures} 次，放弃本次写入）");
            lock (_gate)
            {
                MarkIdleLocked();
                _consecutiveFailures = 0;
            }
        }
    }

    /// <summary>把重试放到独立线程池任务：延迟等待不占用串行后台宿主。</summary>
    private void ScheduleRetry()
    {
        lock (_gate)
        {
            _dirty = true;
            _ = Task.Run(async () =>
            {
                await _delay(_retryDelay).ConfigureAwait(false);
                await FlushCoreAsync(applyDebounce: false).ConfigureAwait(false);
            });
        }
    }

    /// <summary>标记空闲并唤醒所有等待者（调用方必须持有 <see cref="_gate"/>）。</summary>
    private void MarkIdleLocked()
    {
        _scheduled = false;
        _idle?.TrySetResult();
        _idle = null;
    }
}
