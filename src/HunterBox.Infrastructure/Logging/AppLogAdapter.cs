using HunterBox.Application.Common;

namespace HunterBox.Infrastructure.Logging;

/// <summary>
/// <see cref="IAppLogger"/> 的生产实现：把端口调用转给进程级 <see cref="AppLog"/>（A-23）。
/// </summary>
/// <remarks>
/// 单实例应用下日志目录与写入器仍是进程级共享；适配器只负责把「消费者依赖接口」与
/// 「实现是静态文件日志」解耦，测试可替换为内存实现。
/// </remarks>
public sealed class AppLogAdapter : IAppLogger
{
    /// <summary>进程内共享实例（无状态，可安全复用）。</summary>
    public static AppLogAdapter Instance { get; } = new();

    /// <inheritdoc />
    public void Info(string message) => AppLog.Info(message);

    /// <inheritdoc />
    public void Warn(string message) => AppLog.Warn(message);

    /// <inheritdoc />
    public void WarnBatch(IReadOnlyList<string> messages) => AppLog.WarnBatch(messages);

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null) => AppLog.Error(message, exception);
}
