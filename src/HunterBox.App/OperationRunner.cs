using HunterBox.Application.Common;

namespace HunterBox.App;

/// <summary>
/// 长任务编排器：统一忙碌门禁、取消、异常、进度代次与操作后刷新。
/// </summary>
/// <remarks>
/// 从 <see cref="MainWindow"/> 抽出的第一块职责。MainWindow 只负责把状态写回控件
/// （BusyRing/取消按钮/状态栏/按钮可用性/列表刷新），这里不直接依赖具体控件。
/// 同一时刻只允许一个长任务，重复进入返回 <see cref="BusyReentryMessage"/>。
/// </remarks>
internal sealed class OperationRunner
{
    /// <summary>有其它操作进行中时的固定提示（也用于调用方区分重入）。</summary>
    public const string BusyReentryMessage = "有其它操作正在进行，请稍后再试";

    /// <summary>取消操作的固定结果文案；调用方不得把它当成功或失败弹窗。</summary>
    public const string CanceledMessage = "已取消";

    private readonly Action<bool> _setBusyUi;
    private readonly Action<string> _setStatus;
    private readonly Action _updateActionButtons;
    private readonly Action _refreshAfterOperation;
    private readonly IAppLogger _logger;

    /// <summary>装配长任务编排器。</summary>
    /// <param name="setBusyUi">切换忙碌 UI（BusyRing/取消按钮）。</param>
    /// <param name="setStatus">写状态栏文本。</param>
    /// <param name="updateActionButtons">刷新操作按钮可用性。</param>
    /// <param name="refreshAfterOperation">操作结束后刷新列表（由窗口决定轻量/整表）。</param>
    /// <param name="logger">日志端口。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public OperationRunner(
        Action<bool> setBusyUi,
        Action<string> setStatus,
        Action updateActionButtons,
        Action refreshAfterOperation,
        IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(setBusyUi);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(updateActionButtons);
        ArgumentNullException.ThrowIfNull(refreshAfterOperation);
        ArgumentNullException.ThrowIfNull(logger);

        _setBusyUi = setBusyUi;
        _setStatus = setStatus;
        _updateActionButtons = updateActionButtons;
        _refreshAfterOperation = refreshAfterOperation;
        _logger = logger;
    }

    /// <summary>是否正有一个长任务在跑。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>当前长任务的取消源；空闲时为 <see langword="null"/>。</summary>
    public CancellationTokenSource? CancellationSource { get; private set; }

    /// <summary>操作代次：每次退出忙碌递增，用于丢弃晚到的进度回调。</summary>
    public int Epoch { get; private set; }

    /// <summary>刚结束的后台操作错误（用于在列表刷新后把状态栏改回错误提示）。</summary>
    public string? LastOperationError { get; private set; }

    /// <summary>判断 <see cref="RunAsync"/> 的结果是否为用户取消。</summary>
    /// <param name="error">返回的错误文案。</param>
    /// <returns>是取消时返回 <see langword="true"/>。</returns>
    public static bool IsCanceled(string? error) =>
        string.Equals(error, CanceledMessage, StringComparison.Ordinal);

    /// <summary>
    /// 执行一个可取消的长任务：忙碌门禁、后台执行、异常记录、结束刷新与最终状态栏文案。
    /// </summary>
    /// <param name="message">进行中的状态提示。</param>
    /// <param name="action">后台动作（可协作取消）。</param>
    /// <returns>成功与否，以及失败原因（取消时为 <see cref="CanceledMessage"/>）。</returns>
    public async Task<(bool Ok, string? Error)> RunAsync(
        string message,
        Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return (false, BusyReentryMessage);
        }

        using CancellationTokenSource cts = new();
        bool entered = false;
        bool canceled = false;
        try
        {
            EnterBusy(message, cts);
            entered = true;
            await Task.Run(async () => await action(cts.Token).ConfigureAwait(false), cts.Token)
                .ConfigureAwait(true);
            return (true, null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            canceled = true;
            _logger.Info(message + " 已取消");
            return (false, CanceledMessage);
        }
        catch (Exception ex)
        {
            OperationError error = OperationError.From(ex);
            if (error.Kind == OperationErrorKind.Unexpected && error.TechnicalDetail is not null)
            {
                // 未预期故障的原始细节只进日志；其它类别仍按既有格式记录完整异常。
                _logger.Error(message + " 失败：" + error.TechnicalDetail, ex);
            }
            else
            {
                _logger.Error(message + " 失败", ex);
            }

            LastOperationError = error.UserMessage;
            return (false, error.UserMessage);
        }
        finally
        {
            if (entered)
            {
                ExitBusy();
            }
            else
            {
                // EnterBusy 自身可能在写 UI 回调时抛异常（窗口关闭、控件已释放等）：
                // 此时 IsBusy/CancellationSource 可能已被置位，必须补偿复位，
                // 否则后续所有入口会永久返回「有其它操作正在进行」。
                AbortBusy();
            }

            // finally 里绝不能抛异常：列表刷新失败不能覆盖真正的操作结果。
            try
            {
                _refreshAfterOperation();
            }
            catch (Exception ex)
            {
                _logger.Error("操作后刷新列表失败", ex);
                _setStatus("操作已完成，但刷新列表失败：" + ex.Message);
            }

            if (canceled)
            {
                _setStatus("操作已取消");
            }
            else if (LastOperationError is not null)
            {
                _setStatus("操作失败：" + LastOperationError);
                LastOperationError = null;
            }
        }
    }

    /// <summary>后台执行一个带返回值的操作（忙碌/取消/异常/日志，不刷新列表）。</summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="message">进行中的状态提示。</param>
    /// <param name="action">后台执行的动作（可协作取消）。</param>
    /// <param name="showStatus">
    /// 是否把进行中/取消/失败文案写状态栏。清单刷新后的模型缓存预热等「显示增强」后台任务
    /// 应传 <see langword="false"/>：仍然亮忙碌环/取消按钮（busy/取消可感知），但不覆盖操作结果文案。
    /// </param>
    /// <returns>返回值（失败/取消时为默认值），以及失败原因或 <see cref="CanceledMessage"/>。</returns>
    public async Task<(T? Value, string? Error)> RunBackgroundAsync<T>(
        string message,
        Func<CancellationToken, T> action,
        bool showStatus = true)
    {
        if (IsBusy)
        {
            return (default, BusyReentryMessage);
        }

        using CancellationTokenSource cts = new();
        bool entered = false;
        try
        {
            EnterBusy(message, cts, showStatus);
            entered = true;
            return (await Task.Run(() => action(cts.Token), cts.Token), null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.Info(message + " 已取消");
            if (showStatus)
            {
                _setStatus("操作已取消");
            }

            return (default, CanceledMessage);
        }
        catch (Exception ex)
        {
            OperationError error = OperationError.From(ex);
            _logger.Error(message + " 失败", ex);
            if (showStatus)
            {
                _setStatus("操作失败：" + error.UserMessage);
            }

            return (default, error.UserMessage);
        }
        finally
        {
            if (entered)
            {
                ExitBusy();
            }
            else
            {
                // 同 RunAsync：进入忙碌阶段的回调抛异常时补偿复位，避免 IsBusy 永久为 true。
                AbortBusy();
            }
        }
    }

    /// <summary>请求取消当前任务；在下一个安全点生效，已完成的部分保留。</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts = CancellationSource;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作刚好结束：取消源已释放，忽略即可。
        }
    }

    /// <summary>创建带操作代次的进度回调；上一次操作晚到的进度会被丢弃。</summary>
    /// <param name="epoch">创建进度时的操作代次。</param>
    /// <param name="format">进度值 → 状态栏文案。</param>
    /// <returns>UI 线程安全的进度回调。</returns>
    public IProgress<T> CreateProgress<T>(int epoch, Func<T, string> format) =>
        new Progress<T>(value =>
        {
            if (epoch != Epoch)
            {
                return;
            }

            _setStatus(ProgressText(format(value)));
        });

    /// <summary>进度文案：已请求取消时固定显示「已请求取消…」。</summary>
    /// <param name="text">原始进度文案。</param>
    /// <returns>取消请求后的固定文案，否则原样返回。</returns>
    public string ProgressText(string text) =>
        CancellationSource?.IsCancellationRequested == true ? "已请求取消…" : text;

    private void EnterBusy(string message, CancellationTokenSource cts, bool showStatus = true)
    {
        IsBusy = true;
        LastOperationError = null;
        CancellationSource = cts;
        _setBusyUi(true);
        if (showStatus)
        {
            _setStatus(message);
        }

        _updateActionButtons();
    }

    private void ExitBusy()
    {
        Epoch++;
        CancellationSource = null;
        IsBusy = false;
        _setBusyUi(false);
        _updateActionButtons();
    }

    /// <summary>
    /// <see cref="EnterBusy"/> 中途抛异常时的补偿复位：清掉可能已置位的忙碌状态，
    /// 并「尽力而为」地复位 UI（补偿路径自身绝不再向外抛异常）。
    /// </summary>
    private void AbortBusy()
    {
        Epoch++;
        CancellationSource = null;
        IsBusy = false;
        try
        {
            _setBusyUi(false);
        }
        catch (Exception ex)
        {
            _logger.Error("补偿复位忙碌 UI 失败", ex);
        }

        try
        {
            _updateActionButtons();
        }
        catch (Exception ex)
        {
            _logger.Error("补偿刷新操作按钮失败", ex);
        }
    }
}
