using HunterBox.Domain.Archives;
using HunterBox.Application.Common;
using Microsoft.UI.Xaml;

namespace HunterBox.App;

/// <summary>MainWindow 的 busy/取消/进度包装与入口保护辅助。</summary>
public sealed partial class MainWindow
{

    /// <summary>执行一个有取消检查点的同步后台操作（解压/批量/清理等长任务用这个重载）。</summary>
    /// <param name="message">进行中的状态提示。</param>
    /// <param name="action">后台执行的动作；循环内可调用 <c>token.ThrowIfCancellationRequested</c>。</param>
    /// <returns>成功与否，以及失败原因（取消时为 <c>已取消</c>）。</returns>
    private Task<(bool Ok, string? Error)> RunOperationAsync(string message, Action<CancellationToken> action) =>
        RunOperationAsync(message, token =>
        {
            action(token);
            return Task.CompletedTask;
        });
    private Task<(bool Ok, string? Error)> RunOperationAsync(string message, Func<CancellationToken, Task> action) =>
        _operations.RunAsync(message, action);
    /// <summary>忙碌/取消 UI 是否处于「操作进行中」的固定提示（重入时返回）。</summary>
    private const string BusyReentryMessage = OperationRunner.BusyReentryMessage;

    /// <summary>判断带取消令牌的 <c>RunOperationAsync</c>的结果是否为用户取消。</summary>
    private static bool IsCanceled(string? error) => OperationRunner.IsCanceled(error);

    /// <summary>取消后的状态栏提示（可带「已完成 n 项」明细；不得当成功写状态）。</summary>
    private void ReportCanceled(string? detail = null) =>
        ViewModel.Status = detail is null ? "操作已取消" : "操作已取消：" + detail;

    /// <summary>
    /// 创建带操作代次的进度回调：上一次操作晚到的进度（<see cref="Progress{T}"/> 投递到 UI 队列）
    /// 会被直接丢弃，避免覆盖本次操作的最终状态（状态栏曾永久停在「正在…」）。
    /// </summary>
    /// <param name="epoch">创建进度时的操作代次（<see cref="OperationRunner.Epoch"/>）。</param>
    /// <param name="format">进度值 → 状态栏文案。</param>
    /// <returns>UI 线程安全的进度回调。</returns>
    private IProgress<(int Done, int Total)> CreateOperationProgress(
        int epoch,
        Func<(int Done, int Total), string> format) =>
        CreateProgressCore(epoch, format);

    /// <summary>解压进度专用重载（<see cref="ArchiveProgress"/>）。</summary>
    /// <param name="epoch">创建进度时的操作代次。</param>
    /// <param name="format">进度值 → 状态栏文案。</param>
    /// <returns>UI 线程安全的进度回调。</returns>
    private IProgress<ArchiveProgress> CreateArchiveProgress(
        int epoch,
        Func<ArchiveProgress, string> format) =>
        CreateProgressCore(epoch, format);
    private IProgress<T> CreateProgressCore<T>(int epoch, Func<T, string> format) =>
        _operations.CreateProgress(epoch, format);

    /// <summary>
    /// 统一 UI 交互入口保护：同一时刻只允许一个入口（含其对话框）执行，异常写日志并提示用户。
    /// </summary>
    /// <remarks>
    /// 旧实现里大量 <c>_ = SomeAsync()</c> fire-and-forget：双击会并发打开 ContentDialog（WinUI 直接抛异常），
    /// 异常随 Task 被丢弃，用户「点了没反应」；async void 处理器漏 try/catch 时则直接冒到全局崩溃提示。
    /// 统一走这里：重入判定（<c>_operations.IsBusy</c> + 互斥门）+ 异常兜底（<see cref="TryShowMessageAsync"/>）。
    /// </remarks>
    /// <param name="title">异常提示标题。</param>
    /// <param name="action">入口逻辑。</param>
    /// <param name="completion">
    /// 入口结束（含重入提前返回与异常）时必被调用的收尾动作，例如释放拖拽 deferral。
    /// </param>
    private async void RunUiAction(string title, Func<Task> action, Action? completion = null)
    {
        if (_operations.IsBusy)
        {
            ViewModel.Status = BusyReentryMessage;
            completion?.Invoke();
            return;
        }

        if (!_uiActionGate.Wait(0))
        {
            // 另一个交互入口（含对话框）正在进行：忽略这次重复触发，不并发弹窗/提交。
            completion?.Invoke();
            return;
        }

        try
        {
            // 上一条后台保存可能仍在序列化记录；先等它完成，避免新操作与保存并发改动记录（C-07）。
            await WaitForPendingIndexSavesAsync();
            await action();
        }
        catch (Exception ex)
        {
            OperationError error = OperationError.From(ex);
            _services.Logger.Error(
                title + "入口异常：" + (error.TechnicalDetail ?? error.UserMessage),
                ex);
            await TryShowMessageAsync(title, error.UserMessage);
        }
        finally
        {
            _uiActionGate.Release();
            completion?.Invoke();
        }
    }
    /// <summary>
    /// 请求取消当前长任务：确实有进行中任务时立即给出反馈（禁用按钮 + 状态栏「已请求取消…」），
    /// 避免没有进度回调的操作点了取消却看起来「没反应」。
    /// </summary>
    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_operations.CancellationSource is null)
        {
            // 没有进行中的任务：保持按钮原状，不写误导性的取消文案。
            return;
        }

        _operations.Cancel();
        ViewModel.MarkCancelRequested();
        ViewModel.Status = "已请求取消…";
    }
    /// <summary>
    /// 等待后台模型识别等忙碌任务结束：安装提交前最多等一小段时间，
    /// 避免用户走完向导后直接撞上「有其它操作正在进行」。
    /// </summary>
    /// <param name="timeout">最长等待时间。</param>
    /// <returns>超时前空闲时返回 <see langword="true"/>。</returns>
    private async Task<bool> WaitForBackgroundOperationAsync(TimeSpan timeout)
    {
        long started = Environment.TickCount64;
        bool announced = false;
        while (_operations.IsBusy)
        {
            if (Environment.TickCount64 - started >= timeout.TotalMilliseconds)
            {
                return false;
            }

            if (!announced)
            {
                ViewModel.Status = "正在等待后台模型识别完成…";
                announced = true;
            }

            await Task.Delay(100);
        }

        return true;
    }

    private Task<(T? Value, string? Error)> RunBackgroundAsync<T>(
        string message,
        Func<CancellationToken, T> action,
        bool showStatus = true) =>
        _operations.RunBackgroundAsync(message, action, showStatus);
    /// <summary>选中并滚动到指定 MOD。</summary>
    private void SelectAndScrollToMod(int id)
    {
        ModRow? row = _list.Rows.FirstOrDefault(r => r.Mod.Id == id);
        if (row is null)
        {
            return;
        }

        try
        {
            ModsList.SelectedItems.Clear();
            ModsList.SelectedItem = row;
            ModsList.ScrollIntoView(row);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _services.Logger.Warn($"定位 MOD 行失败：{ex.Message}");
        }
    }

}
